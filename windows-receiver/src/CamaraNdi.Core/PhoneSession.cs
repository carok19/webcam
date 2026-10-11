using System.Net;
using CamaraNdi.Core.Decoding;
using CamaraNdi.Core.Ndi;

namespace CamaraNdi.Core;

public enum SessionState
{
    Conectando,
    EnVivo,
    EnEspera,
    SinSenal,
    Desconectado,
}

/// <summary>Foto inmutable del estado de una sesión, para la interfaz.</summary>
public sealed record SessionSnapshot(
    string DeviceId,
    string NdiName,
    string PhoneName,
    string Model,
    SessionState State,
    string Codec,
    int Width,
    int Height,
    int Fps,
    double OutputFps,
    double RxMbps,
    int PhoneBitrateKbps,
    double LossPct,
    double LostPct,
    double JitterMs,
    double ReceiverLatencyMs,
    double TotalLatencyMs,
    double RttMs,
    long FramesDropped,
    string Decoder,
    int NdiConnections,
    string ThermalLevel,
    int BatteryPct,
    double BatteryTempC,
    bool Charging,
    string LatencyMode,
    int JitterBufferMs,
    string Endpoint);

/// <summary>
/// Un celular conectado: jitter buffer + decodificador + fuente NDI, en su propio hilo.
/// </summary>
public sealed class PhoneSession : IDisposable
{
    private readonly object _gate = new();
    private readonly FrameAssembler _assembler = new();
    private readonly AutoResetEvent _signal = new(false);
    private readonly Thread _thread;
    private readonly ReceiverSettings _settings;
    private readonly Action<IPEndPoint, byte[]> _send;
    private readonly VideoBufferPool _pool = new();
    private volatile bool _running = true;

    private VideoDecoder? _decoder;
    private NdiSender? _ndi;
    private string? _pendingRename;
    private bool _recreateDecoder;
    private readonly List<VideoBuffer> _playout = new();

    // Identidad / red
    public string DeviceId { get; }
    public uint SessionId { get; private set; }
    public IPEndPoint? Endpoint { get; private set; }
    private HelloMessage _hello = new();
    private long _lastPacketMs;
    private long _lastHelloMs;
    private bool _standby;
    private long _blockedUntil;

    // Estado/estadísticas (escritas por el hilo de trabajo)
    private SessionSnapshot _snapshot;
    private long _lastKfRequestMs = -10_000;
    private long _lastReportMs;
    private long _lastStatsMs;
    private ReportCounters _prev;
    private long _framesOutput;
    private long _lateSkipped;
    private double _jitterMs;
    private long _lastFrameArrivalMs = -1;
    private long _lastFramePtsUs;
    private double _latencySum;
    private int _latencyCount;
    private double _decodeSum;
    private int _decodeCount;
    private double _outputFps;
    private double _rxMbps;
    private double _lossPct;
    private double _lostPct;
    private double _avgLatency;
    private int _ndiConnections;
    private readonly double[] _offsetWindow = new double[90];
    private int _offsetCount;
    private int _offsetIdx;

    // Miniatura
    private byte[]? _thumb;
    private int _thumbW, _thumbH;
    private long _thumbVersion;
    private long _lastThumbMs;

    private record struct ReportCounters(long Expected, long Missing, long Lost, long Dropped, long Output, long Bytes);

    public PhoneSession(string deviceId, ReceiverSettings settings, Action<IPEndPoint, byte[]> send)
    {
        DeviceId = deviceId;
        _settings = settings;
        _send = send;
        _snapshot = BuildSnapshot(SessionState.Conectando);
        _thread = new Thread(Run) { IsBackground = true, Name = "Sesion " + deviceId, Priority = ThreadPriority.AboveNormal };
        _thread.Start();
    }

    public string NdiName
    {
        get
        {
            lock (_gate)
            {
                if (_settings.Names.TryGetValue(DeviceId, out var n) && !string.IsNullOrWhiteSpace(n)) return n;
                return DefaultName(_hello);
            }
        }
    }

    public static string DefaultName(HelloMessage h)
    {
        var name = string.IsNullOrWhiteSpace(h.Name) ? "Celular" : h.Name.Trim();
        return string.IsNullOrWhiteSpace(h.Model) ? name : $"{name} - {h.Model.Trim()}";
    }

    public SessionSnapshot Snapshot { get { lock (_gate) return _snapshot; } }

    public bool IsBlocked(long now) => now < Interlocked.Read(ref _blockedUntil);

    /// <summary>Devuelve la miniatura BGRA si cambió desde <paramref name="version"/>.</summary>
    public bool TryGetThumbnail(ref long version, out byte[]? bgra, out int width, out int height)
    {
        lock (_gate)
        {
            bgra = _thumb; width = _thumbW; height = _thumbH;
            if (_thumb == null || version == _thumbVersion) return false;
            version = _thumbVersion;
            return true;
        }
    }

    // ---------------- Llamado desde el hilo de red ----------------

    public void OnHello(HelloMessage hello, uint sessionId, IPEndPoint ep, long now)
    {
        bool rename = false;
        lock (_gate)
        {
            if (sessionId != SessionId)
            {
                if (SessionId != 0) Log.Info($"{DeviceId}: nueva sesión {sessionId:X8} (antes {SessionId:X8}), reinicio del flujo");
                SessionId = sessionId;
                _assembler.Reset();
                _recreateDecoder = true;
                _offsetCount = 0;
            }
            if (!_settings.Names.ContainsKey(DeviceId) && DefaultName(hello) != DefaultName(_hello) && _ndi != null) rename = true;
            _hello = hello;
            Endpoint = ep;
            _lastHelloMs = now;
            _lastPacketMs = now;
            _standby = !hello.Streaming;
            int jitter = _settings.UsePhoneLatencyMode ? ReceiverSettings.JitterForMode(hello.LatencyMode, _settings.JitterMs) : _settings.JitterMs;
            _assembler.JitterMs = jitter;
        }
        if (rename) RequestRename(null);
        var ack = new HelloAckMessage
        {
            Receiver = Environment.MachineName,
            NdiName = NdiName,
            JitterMs = _assembler.JitterMs,
            Accepted = true,
        };
        _send(ep, Protocol.BuildJson(Protocol.HelloAck, sessionId, ack));
        _signal.Set();
    }

    public void OnBye(long now)
    {
        lock (_gate) { _standby = true; _lastPacketMs = now; }
        Log.Info($"{NdiName}: el celular dejó de transmitir");
    }

    public void OnPing(long now)
    {
        lock (_gate) _lastPacketMs = now;
    }

    public void OnVideo(in VideoPacketHeader h, ReadOnlySpan<byte> payload, long now)
    {
        lock (_gate)
        {
            _assembler.OnPacket(h, payload, now);
            _lastPacketMs = now;
            _standby = false;
        }
        _signal.Set();
    }

    // ---------------- Acciones de la interfaz ----------------

    public void RequestRename(string? newName)
    {
        lock (_gate)
        {
            if (newName != null)
            {
                if (string.IsNullOrWhiteSpace(newName)) _settings.Names.Remove(DeviceId);
                else _settings.Names[DeviceId] = newName.Trim();
            }
            _pendingRename = NdiName;
        }
        _signal.Set();
    }

    public void RequestKeyframe()
    {
        var ep = Endpoint;
        if (ep != null) _send(ep, Protocol.BuildKeyframeRequest(SessionId, 3));
    }

    /// <summary>El usuario pulsó "Desconectar": avisamos al celular y bloqueamos 3 s los paquetes en vuelo.</summary>
    public void SendDisconnect()
    {
        var ep = Endpoint;
        Interlocked.Exchange(ref _blockedUntil, Clock.NowMs + 3000);
        if (ep != null)
        {
            var msg = Protocol.BuildJson(Protocol.Disconnect, SessionId, new DisconnectMessage { Reason = "Desconectado desde el receptor" });
            for (int i = 0; i < 3; i++) _send(ep, msg);
        }
    }

    // ---------------- Hilo de trabajo ----------------

    private void Run()
    {
        while (_running)
        {
            int wait = 5;
            try
            {
                wait = Step();
            }
            catch (Exception ex)
            {
                Log.Error($"{DeviceId}: error en la sesión", ex);
                lock (_gate) _assembler.ForceKeyframe();
                ResetDecoder();
                Thread.Sleep(50);
            }
            _signal.WaitOne(Math.Clamp(wait, 1, 10));
        }
    }

    private int Step()
    {
        long now = Clock.NowMs;
        List<uint> nacks;
        List<AssembledFrame>? frames = null;
        bool needKf;
        string? rename;
        bool recreate;
        IPEndPoint? ep;
        uint sid;
        long? deadline;
        lock (_gate)
        {
            nacks = _assembler.CollectNacks(now);
            AssembledFrame? f;
            while ((f = _assembler.NextFrame(now)) != null)
            {
                (frames ??= new()).Add(f);
                if (frames.Count > 8) break; // nunca acumulamos: si hay muchos, se decodifican en la vuelta siguiente
            }
            needKf = _assembler.NeedKeyframe && now - _lastPacketMs < 2000 && !_standby;
            rename = _pendingRename; _pendingRename = null;
            recreate = _recreateDecoder; _recreateDecoder = false;
            ep = Endpoint;
            sid = SessionId;
            deadline = _assembler.NextDeadline();
        }

        if (recreate) ResetDecoder();
        if (rename != null) RecreateNdi(rename);

        if (ep != null)
        {
            for (int i = 0; i < nacks.Count; i += Protocol.MaxNackPerPacket)
            {
                var chunk = nacks.GetRange(i, Math.Min(Protocol.MaxNackPerPacket, nacks.Count - i)).ToArray();
                _send(ep, Protocol.BuildNack(sid, chunk));
            }
            if (needKf && now - _lastKfRequestMs >= 250)
            {
                _lastKfRequestMs = now;
                _send(ep, Protocol.BuildKeyframeRequest(sid, 1));
            }
        }

        if (frames != null)
        {
            foreach (var fr in frames) DecodeOne(fr);
        }

        Output(Clock.NowMs);

        if (now - _lastStatsMs >= 500) UpdateStats(now, ep, sid);

        int wait = 5;
        if (deadline is long d) wait = (int)Math.Min(wait, Math.Max(1, d - now));
        if (_playout.Count > 0) wait = 1;
        return wait;
    }

    private void DecodeOne(AssembledFrame fr)
    {
        // Jitter de llegada (RFC 3550, sobre frames).
        if (_lastFrameArrivalMs >= 0)
        {
            double d = (fr.CompletedMs - _lastFrameArrivalMs) - (fr.PtsUs - _lastFramePtsUs) / 1000.0;
            _jitterMs += (Math.Abs(d) - _jitterMs) / 16.0;
        }
        _lastFrameArrivalMs = fr.CompletedMs;
        _lastFramePtsUs = fr.PtsUs;

        if (_decoder == null || _decoder.Codec != fr.Codec)
        {
            if (!fr.Keyframe) return;
            ResetDecoder();
            try { _decoder = new VideoDecoder(fr.Codec, _settings.Decoder); }
            catch (Exception ex)
            {
                Log.Error($"{DeviceId}: no se pudo crear el decodificador", ex);
                return;
            }
        }

        double t0 = Clock.NowMsPrecise;
        var res = _decoder.Decode(fr.Data, fr.Length, fr.PtsUs, _pool.Rent, out var buf);
        _decodeSum += Clock.NowMsPrecise - t0;
        _decodeCount++;
        if (res == VideoDecoder.Result.Error)
        {
            lock (_gate) _assembler.ForceKeyframe();
            _pool.Return(buf);
            return;
        }
        if (buf == null) return;
        buf.FrameId = fr.FrameId;
        buf.PtsUs = fr.PtsUs;
        buf.FirstArrivalMs = fr.FirstArrivalMs;
        buf.DecodedMs = Clock.NowMs;

        // Relación reloj del celular -> reloj local (mínimo de la ventana = camino más rápido).
        _offsetWindow[_offsetIdx] = fr.CompletedMs - fr.PtsUs / 1000.0;
        _offsetIdx = (_offsetIdx + 1) % _offsetWindow.Length;
        if (_offsetCount < _offsetWindow.Length) _offsetCount++;

        _playout.Add(buf);
        // Nunca dejamos crecer la cola: máximo 3 imágenes, se tiran las más viejas.
        while (_playout.Count > 3)
        {
            _pool.Return(_playout[0]);
            _playout.RemoveAt(0);
            _lateSkipped++;
        }
    }

    private void Output(long now)
    {
        if (_playout.Count == 0) return;
        string mode;
        lock (_gate) mode = _hello.LatencyMode;
        int smoothing = ReceiverSettings.SmoothingForMode(mode);

        int lastDue = -1;
        if (smoothing <= 0 || _offsetCount < 10)
        {
            lastDue = _playout.Count - 1;
        }
        else
        {
            double baseOffset = double.MaxValue;
            for (int i = 0; i < _offsetCount; i++) baseOffset = Math.Min(baseOffset, _offsetWindow[i]);
            for (int i = 0; i < _playout.Count; i++)
            {
                double target = _playout[i].PtsUs / 1000.0 + baseOffset + smoothing;
                if (target <= now || now - _playout[i].DecodedMs > smoothing * 2) lastDue = i;
            }
        }
        if (lastDue < 0) return;

        // Si hay varios listos, se envía sólo el más nuevo (los viejos se descartan).
        for (int i = 0; i < lastDue; i++) { _pool.Return(_playout[i]); _lateSkipped++; }
        var buf = _playout[lastDue];
        _playout.RemoveRange(0, lastDue + 1);

        int fps;
        lock (_gate) fps = _hello.Fps > 0 ? _hello.Fps : 30;
        if (_ndi == null && !_ndiAttempted)
        {
            _ndiAttempted = true;
            _ndi = NdiSender.TryCreate(NdiName);
        }

        MaybeThumbnail(buf, now);

        VideoBuffer? released = _ndi != null ? _ndi.Send(buf, fps, 1) : buf;
        _framesOutput++;
        _latencySum += Clock.NowMs - buf.FirstArrivalMs;
        _latencyCount++;
        _pool.Return(released);
    }

    private bool _ndiAttempted;

    private void RecreateNdi(string name)
    {
        _ndi?.Dispose();
        _ndi = NdiSender.TryCreate(name);
        _ndiAttempted = true;
    }

    private void ResetDecoder()
    {
        _decoder?.Dispose();
        _decoder = null;
        foreach (var b in _playout) _pool.Return(b);
        _playout.Clear();
    }

    private void MaybeThumbnail(VideoBuffer b, long now)
    {
        if (now - _lastThumbMs < 400) return;
        _lastThumbMs = now;
        const int tw = 256;
        int th = Math.Max(2, (int)Math.Round(tw * (double)b.Height / b.Width));
        var bgra = new byte[tw * th * 4];
        Thumbnail.ToBgra(b, bgra, tw, th);
        lock (_gate)
        {
            _thumb = bgra; _thumbW = tw; _thumbH = th; _thumbVersion++;
        }
    }

    private void UpdateStats(long now, IPEndPoint? ep, uint sid)
    {
        double dt = _lastStatsMs == 0 ? 500 : now - _lastStatsMs;
        _lastStatsMs = now;
        ReportCounters cur;
        HelloMessage hello;
        long lastPacket, lastHello;
        bool standby;
        int jitterBuf;
        lock (_gate)
        {
            cur = new ReportCounters(_assembler.ExpectedPackets, _assembler.MissingPackets, _assembler.LostPackets,
                _assembler.FramesDropped + _lateSkipped, _framesOutput, _assembler.BytesReceived);
            hello = _hello; lastPacket = _lastPacketMs; lastHello = _lastHelloMs; standby = _standby;
            jitterBuf = _assembler.JitterMs;
        }
        var d = new ReportCounters(cur.Expected - _prev.Expected, cur.Missing - _prev.Missing, cur.Lost - _prev.Lost,
            cur.Dropped - _prev.Dropped, cur.Output - _prev.Output, cur.Bytes - _prev.Bytes);
        _prev = cur;

        _outputFps = d.Output * 1000.0 / dt;
        _rxMbps = d.Bytes * 8 / 1000.0 / dt;
        _lossPct = d.Expected > 0 ? 100.0 * d.Missing / d.Expected : 0;
        _lostPct = d.Expected > 0 ? 100.0 * d.Lost / d.Expected : 0;
        _avgLatency = _latencyCount > 0 ? _latencySum / _latencyCount : _avgLatency;
        double decodeAvg = _decodeCount > 0 ? _decodeSum / _decodeCount : 0;
        _latencySum = 0; _latencyCount = 0; _decodeSum = 0; _decodeCount = 0;
        _ndiConnections = _ndi?.Connections ?? 0;

        uint suggested = 0;
        double frameMs = 1000.0 / Math.Max(1, hello.Fps);
        int current = hello.BitrateKbps > 0 ? hello.BitrateKbps : (int)(_rxMbps * 1000);
        if (d.Expected > 20 && (d.Dropped > 0 || _lostPct > 2)) suggested = (uint)(current * 0.7);
        else if (decodeAvg > frameMs * 0.8) suggested = (uint)(current * 0.8);

        if (ep != null && now - lastPacket < 3000)
        {
            var r = new ReportData
            {
                IntervalMs = (uint)dt,
                ExpectedPackets = (uint)Math.Max(0, d.Expected),
                MissingPackets = (uint)Math.Max(0, d.Missing),
                LostPackets = (uint)Math.Max(0, d.Lost),
                FramesOutput = (uint)Math.Max(0, d.Output),
                FramesDropped = (uint)Math.Max(0, d.Dropped),
                JitterMs = _jitterMs,
                ReceiverLatencyMs = _avgLatency,
                SuggestedBitrateKbps = suggested,
                RxBitrateKbps = (uint)(_rxMbps * 1000),
            };
            _send(ep, Protocol.BuildReport(sid, r));
            _lastReportMs = now;
        }

        var state = standby ? SessionState.EnEspera
            : lastHello == 0 ? SessionState.Conectando
            : now - lastPacket > 30_000 ? SessionState.Desconectado
            : now - lastPacket > 3000 ? SessionState.SinSenal
            : _outputFps > 0 ? SessionState.EnVivo : SessionState.Conectando;

        var snap = BuildSnapshot(state, hello, jitterBuf);
        lock (_gate) _snapshot = snap;
    }

    private SessionSnapshot BuildSnapshot(SessionState state, HelloMessage? h = null, int jitterBuf = 0)
    {
        h ??= new HelloMessage();
        double total = h.EncodeMs + h.RttMs / 2 + _avgLatency + (h.Fps > 0 ? 1000.0 / h.Fps : 33) * 0.5;
        string name;
        if (_settings.Names.TryGetValue(DeviceId, out var custom) && !string.IsNullOrWhiteSpace(custom)) name = custom;
        else name = DefaultName(h);
        return new SessionSnapshot(
            DeviceId, name, h.Name, h.Model, state, h.Codec.ToUpperInvariant(), h.Width, h.Height, h.Fps,
            _outputFps, _rxMbps, h.BitrateKbps, _lossPct, _lostPct, _jitterMs, _avgLatency,
            state == SessionState.EnVivo ? total : 0, h.RttMs,
            _assembler.FramesDropped + _lateSkipped,
            _decoder?.Backend ?? "-", _ndiConnections, h.ThermalLevel, h.BatteryPct, h.BatteryTempC, h.Charging,
            h.LatencyMode, jitterBuf, Endpoint?.ToString() ?? "");
    }

    public void Dispose()
    {
        _running = false;
        _signal.Set();
        _thread.Join(1000);
        ResetDecoder();
        _ndi?.Dispose();
        _ndi = null;
    }
}
