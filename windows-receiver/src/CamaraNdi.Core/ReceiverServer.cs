using System.Net;
using System.Net.Sockets;

namespace CamaraNdi.Core;

/// <summary>
/// Escucha a todos los celulares en un solo socket UDP (medios + control) y responde al
/// descubrimiento por broadcast en otro puerto.
/// </summary>
public sealed class ReceiverServer : IDisposable
{
    private readonly ReceiverSettings _settings;
    private readonly Socket _media;
    private readonly Socket _discovery;
    private readonly Thread _mediaThread;
    private readonly Thread _discoveryThread;
    private readonly Thread _housekeeping;
    private volatile bool _running = true;
    private readonly object _gate = new();
    private readonly Dictionary<string, PhoneSession> _byDevice = new();
    private readonly Dictionary<uint, PhoneSession> _bySession = new();

    public event Action<PhoneSession>? SessionAdded;
    public event Action<PhoneSession>? SessionRemoved;
    /// <summary>Se disparó cuando un celular fue rechazado (p. ej. receptor lleno).</summary>
    public event Action<string>? Notice;

    public int MediaPort { get; }

    public ReceiverServer(ReceiverSettings settings)
    {
        _settings = settings;
        // Probar en segundo plano qué decodifica la GPU (los celulares lo reciben en HELLO_ACK).
        Decoding.DecoderProbe.StartAsync();
        _media = CreateUdp(settings.MediaPort, 8 * 1024 * 1024);
        MediaPort = ((IPEndPoint)_media.LocalEndPoint!).Port;
        _discovery = CreateUdp(settings.DiscoveryPort, 256 * 1024, allowBroadcast: true);
        _mediaThread = new Thread(MediaLoop) { IsBackground = true, Name = "UDP medios", Priority = ThreadPriority.Highest };
        _discoveryThread = new Thread(DiscoveryLoop) { IsBackground = true, Name = "UDP descubrimiento" };
        _housekeeping = new Thread(HousekeepingLoop) { IsBackground = true, Name = "Mantenimiento" };
        _mediaThread.Start();
        _discoveryThread.Start();
        _housekeeping.Start();
        Log.Info($"Receptor escuchando en UDP {MediaPort} (medios) y {settings.DiscoveryPort} (descubrimiento)");
    }

    private static Socket CreateUdp(int port, int rcvBuf, bool allowBroadcast = false)
    {
        var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        s.ReceiveBufferSize = rcvBuf;
        s.SendBufferSize = 1024 * 1024;
        if (allowBroadcast) s.EnableBroadcast = true;
        if (OperatingSystem.IsWindows())
        {
            // Evita que un "ICMP puerto inalcanzable" (celular que se fue) rompa ReceiveFrom con WSAECONNRESET.
            const int SIO_UDP_CONNRESET = unchecked((int)0x9800000C);
            try { s.IOControl(SIO_UDP_CONNRESET, new byte[] { 0, 0, 0, 0 }, null); } catch { }
        }
        s.Bind(new IPEndPoint(IPAddress.Any, port));
        return s;
    }

    public IReadOnlyList<PhoneSession> Sessions
    {
        get { lock (_gate) return _byDevice.Values.ToList(); }
    }

    public void Send(IPEndPoint ep, byte[] data)
    {
        try { _media.SendTo(data, ep); }
        catch (ObjectDisposedException) { }
        catch (SocketException ex) { Log.Warn($"Error enviando a {ep}: {ex.SocketErrorCode}"); }
    }

    private void MediaLoop()
    {
        var buf = GC.AllocateUninitializedArray<byte>(65536, pinned: true);
        EndPoint any = new IPEndPoint(IPAddress.Any, 0);
        while (_running)
        {
            int n;
            EndPoint from = any;
            try
            {
                n = _media.ReceiveFrom(buf, ref from);
            }
            catch (SocketException ex) when (ex.SocketErrorCode is SocketError.ConnectionReset or SocketError.MessageSize)
            {
                continue;
            }
            catch (ObjectDisposedException) { break; }
            catch (SocketException ex)
            {
                if (!_running) break;
                Log.Error("Error de socket de medios", ex);
                Thread.Sleep(10);
                continue;
            }
            try
            {
                Handle(buf.AsSpan(0, n), (IPEndPoint)from);
            }
            catch (Exception ex)
            {
                Log.Error("Paquete inválido", ex);
            }
        }
    }

    private void Handle(ReadOnlySpan<byte> p, IPEndPoint from)
    {
        if (!Protocol.TryReadHeader(p, out var type, out var sid)) return;
        long now = Clock.NowMs;
        PhoneSession? s;
        switch (type)
        {
            case Protocol.Video:
                lock (_gate) _bySession.TryGetValue(sid, out s);
                if (s == null || s.IsBlocked(now)) return;
                if (!Protocol.TryReadVideo(p, out var vh)) return;
                s.OnVideo(vh, p[Protocol.VideoHeaderSize..], now);
                break;

            case Protocol.Ping:
                lock (_gate) _bySession.TryGetValue(sid, out s);
                if (s == null || s.IsBlocked(now)) return;
                s.OnPing(now);
                Send(from, Protocol.BuildPong(sid, p));
                break;

            case Protocol.Hello:
                HandleHello(p, sid, from, now);
                break;

            case Protocol.Bye:
                lock (_gate) _bySession.TryGetValue(sid, out s);
                s?.OnBye(now);
                break;

            case Protocol.Discover:
                ReplyDiscover(from, sid, _media);
                break;
        }
    }

    private void HandleHello(ReadOnlySpan<byte> p, uint sid, IPEndPoint from, long now)
    {
        var hello = Protocol.ParseJson<HelloMessage>(p);
        if (hello == null || string.IsNullOrWhiteSpace(hello.DeviceId)) return;
        PhoneSession? s;
        bool added = false;
        lock (_gate)
        {
            if (IsRecentlyRemoved(hello.DeviceId)) return;
            _byDevice.TryGetValue(hello.DeviceId, out s);
            if (s != null && s.IsBlocked(now)) return;
            if (s == null)
            {
                if (!hello.Streaming) return;
                int active = _byDevice.Count;
                if (active >= _settings.MaxPhones)
                {
                    var msg = $"Receptor lleno ({_settings.MaxPhones} celulares). Desconecta uno o sube el límite.";
                    var nack = new HelloAckMessage { Receiver = Environment.MachineName, Accepted = false, Reason = msg };
                    Send(from, Protocol.BuildJson(Protocol.HelloAck, sid, nack));
                    Notice?.Invoke($"Rechazado {hello.Name} ({hello.Model}): {msg}");
                    return;
                }
                s = new PhoneSession(hello.DeviceId, _settings, Send);
                _byDevice[hello.DeviceId] = s;
                added = true;
                Log.Info($"Nuevo celular: {hello.Name} / {hello.Model} ({from}) id={hello.DeviceId}");
            }
            // Una sesión nueva del mismo celular (la app se reinició) reemplaza a la vieja.
            foreach (var kv in _bySession.Where(kv => kv.Value == s && kv.Key != sid).ToList()) _bySession.Remove(kv.Key);
            _bySession[sid] = s;
        }
        s.OnHello(hello, sid, from, now);
        if (added) SessionAdded?.Invoke(s);
    }

    /// <summary>Desconecta y elimina un celular (botón "Desconectar").</summary>
    public void Remove(PhoneSession s, bool notifyPhone = true)
    {
        if (notifyPhone) s.SendDisconnect();
        lock (_gate)
        {
            _byDevice.Remove(s.DeviceId);
            foreach (var kv in _bySession.Where(kv => kv.Value == s).ToList()) _bySession.Remove(kv.Key);
        }
        // Mantener el bloqueo de 3 s aunque ya no esté en el diccionario.
        _recentlyRemoved[s.DeviceId] = Clock.NowMs + 3000;
        Task.Run(() => s.Dispose());
        SessionRemoved?.Invoke(s);
        Log.Info($"Celular quitado: {s.NdiName}");
    }

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, long> _recentlyRemoved = new();

    private void DiscoveryLoop()
    {
        var buf = new byte[2048];
        while (_running)
        {
            EndPoint from = new IPEndPoint(IPAddress.Any, 0);
            int n;
            try { n = _discovery.ReceiveFrom(buf, ref from); }
            catch (ObjectDisposedException) { break; }
            catch (SocketException) { if (!_running) break; continue; }
            if (!Protocol.TryReadHeader(buf.AsSpan(0, n), out var type, out var sid) || type != Protocol.Discover) continue;
            ReplyDiscover((IPEndPoint)from, sid, _discovery);
        }
    }

    private void ReplyDiscover(IPEndPoint to, uint sid, Socket via)
    {
        var reply = Protocol.BuildJson(Protocol.DiscoverReply, sid, new DiscoverReplyMessage
        {
            Receiver = Environment.MachineName,
            Port = MediaPort,
        });
        try { via.SendTo(reply, to); } catch (Exception ex) { Log.Warn("No se pudo responder al descubrimiento: " + ex.Message); }
    }

    private void HousekeepingLoop()
    {
        while (_running)
        {
            Thread.Sleep(1000);
            long now = Clock.NowMs;
            foreach (var kv in _recentlyRemoved)
                if (kv.Value < now) _recentlyRemoved.TryRemove(kv.Key, out _);
        }
    }

    internal bool IsRecentlyRemoved(string deviceId) =>
        _recentlyRemoved.TryGetValue(deviceId, out var until) && until > Clock.NowMs;

    /// <summary>Direcciones IPv4 locales (para mostrarlas al usuario y escribirlas a mano en el celular).</summary>
    public static IReadOnlyList<string> LocalAddresses()
    {
        var list = new List<string>();
        try
        {
            foreach (var ni in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up) continue;
                if (ni.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Loopback) continue;
                foreach (var a in ni.GetIPProperties().UnicastAddresses)
                    if (a.Address.AddressFamily == AddressFamily.InterNetwork)
                        list.Add($"{a.Address} ({ni.Name})");
            }
        }
        catch { }
        return list;
    }

    public void Dispose()
    {
        _running = false;
        List<PhoneSession> all;
        lock (_gate) { all = _byDevice.Values.ToList(); _byDevice.Clear(); _bySession.Clear(); }
        // No se envía DISCONNECT: así los celulares siguen intentando y vuelven solos
        // cuando el receptor se abre de nuevo.
        foreach (var s in all) s.Dispose();
        try { _media.Close(); } catch { }
        try { _discovery.Close(); } catch { }
    }
}
