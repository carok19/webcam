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
    string Endpoint,
    double CpuPct,
    double LiveSeconds,
    double AvgFpsLive,
    double MinFpsLive,
    double MaxTempC,
    string ColorInfo,
    string RangeMode,
    string Warning,
    string LimitInfo);

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
    private readonly RangeDetector _rangeDetector = new();
    private string _colorInfo = "-";

    /// <summary>Sólo para pruebas: ve cada imagen justo antes de enviarla a NDI.</summary>
    public Action<VideoBuffer>? FrameTap { get; set; }
    /// <summary>Sólo para pruebas: retraso artificial por frame decodificado (simula un PC lento).</summary>
    public int TestDecodeDelayMs { get; set; }

    // Decodificación: frames enviados al decodificador que todavía no salieron (con hilos por frame
    // salen unos frames después), para saber a qué frame pertenece cada imagen.
    private readonly Dictionary<long, (uint FrameId, long FirstArrivalMs)> _inFlight = new();
    private readonly List<VideoBuffer> _decoded = new();
    /// <summary>Por códec: la altura con la que la GPU de verdad no pudo (bajo _gate).</summary>
    private readonly Dictionary<VideoCodec, int> _gpuFailedHeight = new();
    private long _decoderCreatedMs;
    /// <summary>El decodificador actual va por CPU porque la GPU falló con una resolución mayor.</summary>
    private bool _decoderForcedCpu;
    /// <summary>El video bajó de resolución y la GPU sí puede con la nueva: cambiar en el próximo keyframe.</summary>
    private bool _retryGpuAtKeyframe;
    private int _lastDecodedHeight;
    private int _hwErrorStreak;

    // ¿El PC alcanza? (frames que llegan vs. frames que se decodifican)
    private long _decodedTotal;
    private long _backlogDrops;
    private long _prevStarted, _prevDecoded, _prevBacklogDrops;
    private long _lastBacklogLogMs = -60_000;
    // Lo siguiente va bajo _gate (lo tocan el hilo de red, la interfaz y el hilo de trabajo).
    private int _overloadTicks;
    private long _lastLimitMs = -60_000;
    private string _overloadWarning = "";
    /// <summary>Límite pedido al celular porque este PC no alcanzó (0 = sin límite).</summary>
    private int _limitHeight;
    private long _limitPixelRate;
    /// <summary>Lo que no se alcanzó a decodificar (p. ej. "4K30"), para mostrarlo.</summary>
    private string _limitFrom = "";

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

    // Resumen para la tabla de pruebas (desde que empezó a estar en vivo)
    private long _liveSinceMs = -1;
    private long _liveFrames;
    private long _notLiveSinceMs = -1;
    private double _minFpsLive = double.MaxValue;
    private double _maxTempC;

    // Miniatura
    private byte[]? _thumb;
    private int _thumbW, _thumbH;
    private long _thumbVersion;
    private long _lastThumbMs;

    private record struct ReportCounters(long Expected, long Missing, long Lost, long NetDropped, long Late, long Output, long Bytes);
    private long _staleSkipped;

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
        DecodeCapsMessage decode;
        int jitter;
        lock (_gate)
        {
            if (sessionId != SessionId)
            {
                if (SessionId != 0)
                {
                    Log.Info($"{DeviceId}: nueva sesión {sessionId:X8} (antes {SessionId:X8}), reinicio del flujo");
                    // La app del celular se reinició: se vuelve a intentar con lo que elija (el límite era de antes).
                    if (_limitPixelRate > 0) Log.Info($"{DeviceId}: se quita el límite de resolución de la sesión anterior");
                    ClearLimitLocked();
                }
                SessionId = sessionId;
                _assembler.Reset();
                _recreateDecoder = true;
                _rangeDetector.Reset();
            }
            if (!_settings.Names.ContainsKey(DeviceId) && DefaultName(hello) != DefaultName(_hello) && _ndi != null) rename = true;
            _hello = hello;
            Endpoint = ep;
            _lastHelloMs = now;
            _lastPacketMs = now;
            if (!hello.Streaming && !_standby) EnterStandbyLocked();
            _standby = !hello.Streaming;
            jitter = _settings.UsePhoneLatencyMode ? ReceiverSettings.JitterForMode(hello.LatencyMode, _settings.JitterMs) : _settings.JitterMs;
            _assembler.JitterMs = jitter;
            decode = BuildDecodeCapsLocked();
        }
        if (rename) RequestRename(null);
        var ack = new HelloAckMessage
        {
            Receiver = Environment.MachineName,
            NdiName = NdiName,
            JitterMs = jitter,
            Accepted = true,
            Decode = decode,
        };
        _send(ep, Protocol.BuildJson(Protocol.HelloAck, sessionId, ack));
        _signal.Set();
    }

    private static readonly int[] StandardHeights = { 2160, 1440, 1080, 720 };

    /// <summary>Qué decodifica este PC por GPU, descontando lo que ya falló de verdad con este celular.</summary>
    private DecodeCapsMessage BuildDecodeCapsLocked()
    {
        var caps = DecoderProbe.Current;
        // Con "Decodificar con: CPU" no sirve que el celular elija el códec pensando en la GPU:
        // sin GPU conviene H.264 (la CPU lo decodifica bastante más rápido que H.265).
        bool cpuOnly = _settings.Decoder == "cpu";
        bool limit = _settings.AutoLowerResolution;
        return new DecodeCapsMessage
        {
            Probed = caps.Probed,
            HevcGpuMaxHeight = cpuOnly ? 0 : GpuMaxHeightLocked(VideoCodec.Hevc, caps),
            H264GpuMaxHeight = cpuOnly ? 0 : GpuMaxHeightLocked(VideoCodec.H264, caps),
            MaxHeight = limit ? _limitHeight : 0,
            MaxPixelRate = limit ? _limitPixelRate : 0,
            Gpu = caps.GpuName,
        };
    }

    private int GpuMaxHeightLocked(VideoCodec codec, DecoderCaps caps)
    {
        int h = caps.GpuMaxHeight(codec);
        if (_gpuFailedHeight.TryGetValue(codec, out int failed))
        {
            // La GPU no pudo con "failed": se anuncia la altura estándar anterior (si la prueba la cubría).
            int below = 0;
            foreach (var x in StandardHeights)
                if (x < failed && x <= h) { below = x; break; }
            h = below;
        }
        return h;
    }

    /// <summary>¿Se puede intentar la GPU para este códec a esta altura? (bajo _gate)</summary>
    private bool GpuAllowedLocked(VideoCodec codec, int height)
    {
        if (_settings.Decoder == "cpu") return false;
        return !_gpuFailedHeight.TryGetValue(codec, out int failed) || (height > 0 && height < failed);
    }

    private void MarkGpuFailed(VideoCodec codec, int height)
    {
        if (height <= 0) height = 1;
        lock (_gate)
            _gpuFailedHeight[codec] = _gpuFailedHeight.TryGetValue(codec, out int old) ? Math.Min(old, height) : height;
    }

    public void OnBye(long now)
    {
        lock (_gate)
        {
            if (!_standby) EnterStandbyLocked();
            _standby = true;
            _lastPacketMs = now;
        }
        Log.Info($"{NdiName}: el celular dejó de transmitir");
    }

    /// <summary>
    /// El celular dejó de transmitir. Con hilos por frame quedan imágenes dentro del decodificador:
    /// se descarta todo para que al volver no salgan primero imágenes de antes de la pausa.
    /// </summary>
    private void EnterStandbyLocked()
    {
        _recreateDecoder = true;
        _assembler.ForceKeyframe();
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

    /// <summary>Cambió "Decodificar con": se usa el decodificador nuevo desde el próximo keyframe.</summary>
    public void ApplyDecoderSetting()
    {
        lock (_gate)
        {
            _recreateDecoder = true;
            _assembler.ForceKeyframe();
        }
        _signal.Set();
    }

    /// <summary>¿Este PC le pidió al celular menos resolución?</summary>
    public bool HasLimit { get { lock (_gate) return _limitPixelRate > 0 || _limitHeight > 0; } }

    /// <summary>
    /// Quita el límite de resolución pedido al celular (p. ej. el PC estuvo ocupado un momento, o el
    /// usuario desmarcó "Bajar resolución si este PC no alcanza"): el celular vuelve a su máxima.
    /// </summary>
    public void ClearLimit()
    {
        IPEndPoint? ep;
        uint sid;
        bool had;
        lock (_gate)
        {
            had = _limitPixelRate > 0 || _limitHeight > 0;
            ClearLimitLocked();
            ep = Endpoint;
            sid = SessionId;
        }
        if (!had) return;
        Log.Info($"{DeviceId}: se quitó el límite de resolución; el celular vuelve a su máxima");
        if (ep == null) return;
        var msg = Protocol.BuildJson(Protocol.Limit, sid, new LimitMessage { MaxHeight = 0, MaxPixelRate = 0, Reason = "" });
        for (int i = 0; i < 2; i++) _send(ep, msg);
    }

    private void ClearLimitLocked()
    {
        _limitHeight = 0;
        _limitPixelRate = 0;
        _limitFrom = "";
        _overloadWarning = "";
        _overloadTicks = 0;
        _lastLimitMs = -60_000;
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
                // Lotes cortos: si decodificar es lento, la salida, la miniatura y las estadísticas
                // no se congelan esperando al lote entero. Lo que quede, en la vuelta siguiente.
                if (frames.Count >= 3) break;
            }
            // Si el decodificador no da abasto, los frames se acumulan: nunca dejamos que eso
            // se convierta en segundos de retraso. Se salta al keyframe más nuevo.
            int fpsIn = _hello.Fps > 0 ? _hello.Fps : 30;
            int backlogLimit = Math.Max(12, fpsIn * (_assembler.JitterMs + 300) / 1000);
            if (_assembler.PendingFrames > backlogLimit)
            {
                int dropped = _assembler.DropBacklog();
                _backlogDrops++;
                if (now - _lastBacklogLogMs > 10_000)
                {
                    _lastBacklogLogMs = now;
                    Log.Warn($"{DeviceId}: el PC no alcanza a decodificar; se saltaron {dropped} frames para no acumular retraso");
                }
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
            // Sacar imagen después de cada frame: si decodificar es lento, la salida no espera al lote entero.
            foreach (var fr in frames) { DecodeOne(fr); Output(Clock.NowMs); }
        }
        else
        {
            Output(Clock.NowMs);
        }

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

        if (_retryGpuAtKeyframe && fr.Keyframe && _decoder != null && _decoder.Codec == fr.Codec)
        {
            // El video bajó a una resolución que la GPU sí decodifica: volver a la GPU.
            Log.Info($"{DeviceId}: el video bajó a {_lastDecodedHeight}p; se vuelve a decodificar con la GPU");
            if (!CreateDecoder(fr.Codec, _lastDecodedHeight)) return;
        }
        if (_decoder == null || _decoder.Codec != fr.Codec)
        {
            if (!fr.Keyframe) return;
            int h;
            lock (_gate) h = _hello.Height;
            if (!CreateDecoder(fr.Codec, h)) return;
        }

        _inFlight[fr.PtsUs] = (fr.FrameId, fr.FirstArrivalMs);
        if (_inFlight.Count > 128) _inFlight.Clear(); // algo raro pasó: no dejar crecer

        double t0 = Clock.NowMsPrecise;
        _decoded.Clear();
        var res = _decoder!.Decode(fr.Data, fr.Length, fr.PtsUs, _pool.Rent, _decoded);
        if (TestDecodeDelayMs > 0) Thread.Sleep(TestDecodeDelayMs);
        _decodeSum += Clock.NowMsPrecise - t0;
        _decodeCount++;

        if (_decoder.HwFellBack)
        {
            // La GPU no pudo con este video: FFmpeg siguió en CPU con un solo hilo (lento).
            // Recrear el decodificador en CPU con todos los núcleos y empezar en el próximo keyframe.
            int h = _decoder.FellBackHeight;
            if (h <= 0) lock (_gate) h = _hello.Height;
            MarkGpuFailed(fr.Codec, h);
            Log.Warn($"{DeviceId}: la GPU no decodifica {fr.Codec} de {h}p; se usa la CPU con varios hilos");
            foreach (var b in _decoded) _pool.Return(b);
            _decoded.Clear();
            ResetDecoder();
            lock (_gate) _assembler.ForceKeyframe();
            return;
        }
        if (_decoded.Count > 0) _hwErrorStreak = 0;
        if (res == VideoDecoder.Result.Error)
        {
            if (_decoder.IsHardware && _decoded.Count == 0 && ++_hwErrorStreak >= 3)
            {
                // La GPU arrancó bien pero ahora falla en cada imagen (driver reiniciado, memoria de video…):
                // sin esto la imagen quedaría congelada pidiendo keyframes para siempre.
                int h = _lastDecodedHeight;
                if (h <= 0) lock (_gate) h = _hello.Height;
                MarkGpuFailed(fr.Codec, h);
                Log.Warn($"{DeviceId}: la GPU falla al decodificar {fr.Codec}; se pasa a la CPU");
                ResetDecoder();
            }
            lock (_gate) _assembler.ForceKeyframe();
        }

        long now = Clock.NowMs;
        foreach (var buf in _decoded)
        {
            _decodedTotal++;
            if (_inFlight.Remove(buf.PtsUs, out var info))
            {
                buf.FrameId = info.FrameId;
                buf.FirstArrivalMs = info.FirstArrivalMs;
            }
            else
            {
                buf.FirstArrivalMs = now;
            }
            buf.DecodedMs = now;
            // Relación reloj del celular -> reloj local, medida cuando la imagen SALE del decodificador
            // (con hilos por frame sale unos frames después de llegar: eso también entra en la cuenta).
            // El mínimo de la ventana es el camino más rápido; el suavizado se mide desde ahí.
            _offsetWindow[_offsetIdx] = now - buf.PtsUs / 1000.0;
            _offsetIdx = (_offsetIdx + 1) % _offsetWindow.Length;
            if (_offsetCount < _offsetWindow.Length) _offsetCount++;
            _lastDecodedHeight = buf.Height;
            FixColor(buf);
            _playout.Add(buf);
        }
        _decoded.Clear();
        if (_decoderForcedCpu && !_retryGpuAtKeyframe && _lastDecodedHeight > 0)
        {
            bool ok;
            lock (_gate) ok = GpuAllowedLocked(_decoder!.Codec, _lastDecodedHeight);
            var caps = DecoderProbe.Current;
            if (ok && (!caps.Probed || caps.GpuMaxHeight(_decoder!.Codec) >= _lastDecodedHeight)) _retryGpuAtKeyframe = true;
        }
        // Nunca dejamos crecer la cola más de lo que pide el suavizado: se tiran las más viejas.
        while (_playout.Count > MaxPlayoutFrames())
        {
            _pool.Return(_playout[0]);
            _playout.RemoveAt(0);
            _lateSkipped++;
        }
    }

    /// <param name="height">Altura del video que viene (para no repetir la GPU donde ya falló).</param>
    private bool CreateDecoder(VideoCodec codec, int height)
    {
        ResetDecoder();
        IEnumerable<string> hw;
        var caps = DecoderProbe.Current;
        string mode;
        bool gpuAllowed;
        lock (_gate)
        {
            mode = _hello.LatencyMode;
            gpuAllowed = GpuAllowedLocked(codec, height);
        }
        if (!gpuAllowed) hw = Array.Empty<string>();
        else if (caps.Probed && caps.HwFor(codec) is string ok) hw = new[] { ok };
        else if (caps.Probed && _settings.Decoder != "gpu") hw = Array.Empty<string>(); // la prueba dijo que la GPU no puede
        else hw = VideoDecoder.PlatformHwTypes();
        try
        {
            _decoder = new VideoDecoder(codec, hw, lowLatency: !ReceiverSettings.IsQualityMode(mode));
            _decoderCreatedMs = Clock.NowMs;
            _decoderForcedCpu = !gpuAllowed && _settings.Decoder != "cpu";
            lock (_gate) _overloadTicks = 0;
            return true;
        }
        catch (Exception ex)
        {
            Log.Error($"{DeviceId}: no se pudo crear el decodificador", ex);
            return false;
        }
    }

    private int MaxPlayoutFrames()
    {
        string mode;
        int fps;
        lock (_gate) { mode = _hello.LatencyMode; fps = _hello.Fps > 0 ? _hello.Fps : 30; }
        int smoothing = ReceiverSettings.SmoothingForMode(mode);
        return Math.Clamp((int)Math.Ceiling((smoothing + 60) * fps / 1000.0) + 2, 3, 24);
    }

    /// <summary>
    /// Deja la imagen en rango limitado BT.709, que es lo que NDI y OBS esperan.
    /// Si el celular manda rango completo y no se convierte, los negros se aplastan.
    /// </summary>
    private void FixColor(VideoBuffer buf)
    {
        string mode;
        lock (_gate) mode = _settings.RangeOverrides.TryGetValue(DeviceId, out var m) ? m : "auto";
        bool signaledFull = buf.SignaledRange == 2;
        bool full;
        string why;
        switch (mode)
        {
            case "full": full = true; why = "completo (manual)"; break;
            case "limited": full = false; why = "limitado (manual)"; break;
            default:
                if (signaledFull) { full = true; why = "completo (indicado por el celular)"; }
                else
                {
                    _rangeDetector.Feed(buf);
                    full = _rangeDetector.LooksFull;
                    why = full ? "completo (detectado)" : buf.SignaledRange == 1 ? "limitado (indicado por el celular)" : "limitado (supuesto)";
                }
                break;
        }
        bool bt601 = buf.SignaledMatrix is 5 or 6 || (buf.SignaledMatrix is 0 or 2 && buf.Height < 720);
        ColorConverter.ToLimited709(buf, full, bt601);
        _colorInfo = $"{(bt601 ? "BT.601→709" : "BT.709")} · rango {why}" + (full ? " → convertido a 16-235" : "");
    }

    /// <summary>Rango de color de entrada para este celular: "auto", "full" o "limited".</summary>
    public void SetRangeOverride(string mode)
    {
        lock (_gate)
        {
            if (mode == "auto") _settings.RangeOverrides.Remove(DeviceId);
            else _settings.RangeOverrides[DeviceId] = mode;
        }
        _rangeDetector.Reset();
    }

    public string RangeOverride
    {
        get { lock (_gate) return _settings.RangeOverrides.TryGetValue(DeviceId, out var m) ? m : "auto"; }
    }

    private void Output(long now)
    {
        if (_playout.Count == 0) return;
        string mode;
        int jitter;
        lock (_gate) { mode = _hello.LatencyMode; jitter = _assembler.JitterMs; }
        int smoothing = ReceiverSettings.SmoothingForMode(mode);

        // Imágenes de hace segundos (p. ej. las que quedaron dentro del decodificador antes de una
        // pausa o de un corte del WiFi) nunca salen al aire.
        int staleMs = jitter + smoothing + 1500;
        while (_playout.Count > 0 && now - _playout[0].FirstArrivalMs > staleMs)
        {
            _pool.Return(_playout[0]);
            _playout.RemoveAt(0);
            _staleSkipped++;
        }
        if (_playout.Count == 0) return;

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
        if (_ndi == null && (!_ndiAttempted || (NdiLib.IsLoaded && !_ndiWasLoadedAtAttempt)))
        {
            // También se reintenta si NDI se instaló/cargó después de conectarse el celular.
            _ndiAttempted = true;
            _ndiWasLoadedAtAttempt = NdiLib.IsLoaded;
            _ndi = NdiSender.TryCreate(NdiName);
        }

        MaybeThumbnail(buf, now);
        FrameTap?.Invoke(buf);

        VideoBuffer? released = _ndi != null ? _ndi.Send(buf, fps, 1) : buf;
        _framesOutput++;
        _latencySum += Clock.NowMs - buf.FirstArrivalMs;
        _latencyCount++;
        _pool.Return(released);
    }

    private bool _ndiAttempted;
    private bool _ndiWasLoadedAtAttempt;

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
        _inFlight.Clear();
        foreach (var b in _playout) _pool.Return(b);
        _playout.Clear();
        // El retraso del decodificador nuevo puede ser otro (GPU vs. CPU con hilos): medir de nuevo.
        _offsetCount = 0;
        _offsetIdx = 0;
        _retryGpuAtKeyframe = false;
        _decoderForcedCpu = false;
        _hwErrorStreak = 0;
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
        long started;
        lock (_gate)
        {
            cur = new ReportCounters(_assembler.ExpectedPackets, _assembler.MissingPackets, _assembler.LostPackets,
                _assembler.FramesDropped, _lateSkipped, _framesOutput, _assembler.BytesReceived);
            hello = _hello; lastPacket = _lastPacketMs; lastHello = _lastHelloMs; standby = _standby;
            jitterBuf = _assembler.JitterMs;
            started = _assembler.FramesStarted;
        }
        var d = new ReportCounters(cur.Expected - _prev.Expected, cur.Missing - _prev.Missing, cur.Lost - _prev.Lost,
            cur.NetDropped - _prev.NetDropped, cur.Late - _prev.Late, cur.Output - _prev.Output, cur.Bytes - _prev.Bytes);
        _prev = cur;

        _outputFps = d.Output * 1000.0 / dt;
        _rxMbps = d.Bytes * 8 / 1000.0 / dt;
        _lossPct = d.Expected > 0 ? 100.0 * d.Missing / d.Expected : 0;
        _lostPct = d.Expected > 0 ? 100.0 * d.Lost / d.Expected : 0;
        _avgLatency = _latencyCount > 0 ? _latencySum / _latencyCount : _avgLatency;
        _latencySum = 0; _latencyCount = 0; _decodeSum = 0; _decodeCount = 0;
        _ndiConnections = _ndi?.Connections ?? 0;

        uint suggested = 0;
        int current = hello.BitrateKbps > 0 ? hello.BitrateKbps : (int)(_rxMbps * 1000);
        // ¿El PC alcanza a decodificar lo que llega? (si no, bajar bitrate NO sirve: es la resolución)
        // Lo que llega = frames que empezaron a llegar menos los que perdió la red (esos nunca llegan
        // al decodificador y no son culpa del PC).
        long inFrames = (started - _prevStarted) - d.NetDropped, decFrames = _decodedTotal - _prevDecoded;
        long backlogDrops = _backlogDrops - _prevBacklogDrops;
        _prevStarted = started; _prevDecoded = _decodedTotal; _prevBacklogDrops = _backlogDrops;
        bool overloaded = CheckOverload(now, dt, inFrames, decFrames, backlogDrops, hello, ep, sid);
        // Al celular sólo se le cuenta lo que es culpa de la red: si el PC no alcanza, los frames que
        // salen tarde o se saltan no se arreglan bajando el bitrate (eso sólo quitaría nitidez).
        long dropped = d.NetDropped + (overloaded ? 0 : d.Late);

        if (!overloaded)
        {
            if (ReceiverSettings.IsQualityMode(hello.LatencyMode))
            {
                // En calidad el buffer grande absorbe las pérdidas: sólo pedimos bajar si aun así se perdieron frames de la red.
                if (d.Expected > 20 && dropped > 1 && _lostPct > 0) suggested = (uint)(current * 0.85);
            }
            else if (d.Expected > 20 && (dropped > 0 || _lostPct > 2)) suggested = (uint)(current * 0.7);
        }

        if (ep != null && now - lastPacket < 3000)
        {
            var r = new ReportData
            {
                IntervalMs = (uint)dt,
                ExpectedPackets = (uint)Math.Max(0, d.Expected),
                MissingPackets = (uint)Math.Max(0, d.Missing),
                LostPackets = (uint)Math.Max(0, d.Lost),
                FramesOutput = (uint)Math.Max(0, d.Output),
                FramesDropped = (uint)Math.Max(0, dropped),
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

        // Resumen de la prueba: se reinicia si deja de estar en vivo más de 5 s.
        if (state == SessionState.EnVivo)
        {
            _notLiveSinceMs = -1;
            if (_liveSinceMs < 0) { _liveSinceMs = now; _liveFrames = 0; _minFpsLive = double.MaxValue; _maxTempC = 0; }
            else
            {
                _liveFrames += d.Output;
                if (now - _liveSinceMs > 5000) _minFpsLive = Math.Min(_minFpsLive, _outputFps);
            }
            _maxTempC = Math.Max(_maxTempC, hello.BatteryTempC);
        }
        else
        {
            if (_notLiveSinceMs < 0) _notLiveSinceMs = now;
            else if (now - _notLiveSinceMs > 5000) _liveSinceMs = -1;
        }

        var snap = BuildSnapshot(state, hello, jitterBuf);
        lock (_gate) _snapshot = snap;
    }

    /// <summary>Por debajo de esto (720p30) ya no tiene sentido pedir menos: sólo se avisa.</summary>
    private const long MinLimitPixelRate = 1280L * 720 * 30;

    /// <summary>
    /// Si durante ~2 s se decodifican bastante menos frames de los que llegan (o hubo que saltar
    /// frames acumulados), el PC no alcanza con esta resolución. Se avisa y, si el usuario lo permite,
    /// se le pide al celular menos píxeles por segundo (mensaje LIMIT). Devuelve true si hay sobrecarga.
    /// </summary>
    private bool CheckOverload(long now, double dt, long inFrames, long decFrames, long backlogDrops, HelloMessage hello, IPEndPoint? ep, uint sid)
    {
        byte[]? limitMsg = null;
        bool overloaded;
        lock (_gate)
        {
            if (_decoder == null || now - _decoderCreatedMs < 3000 || _standby)
            {
                _overloadTicks = 0;
                _overloadWarning = "";
                return false;
            }
            double inFps = inFrames * 1000.0 / dt, decFps = decFrames * 1000.0 / dt;
            bool slow = backlogDrops > 0 || (inFps > 5 && decFps < inFps * 0.85);
            if (slow) _overloadTicks = Math.Min(_overloadTicks + 1, 6);
            else if (decFps >= inFps * 0.95) _overloadTicks = Math.Max(0, _overloadTicks - 1);
            if (_overloadTicks == 0) _overloadWarning = ""; // volvió a ir a la par
            overloaded = _overloadTicks >= 2;

            // Sólo se actúa si la sobrecarga es sostenida Y este intervalo también fue lento.
            if (slow && _overloadTicks >= 4)
            {
                string res = (hello.Height >= 2160 ? "4K" : $"{hello.Height}p") + hello.Fps;
                long rate = (long)hello.Width * hello.Height * Math.Max(1, hello.Fps);
                if (_settings.AutoLowerResolution && rate > MinLimitPixelRate && ep != null && now - _lastLimitMs > 15_000)
                {
                    _lastLimitMs = now;
                    _limitHeight = hello.Height;
                    _limitPixelRate = (long)(rate * 0.8);
                    _limitFrom = res;
                    _overloadTicks = 0;
                    _overloadWarning = "";
                    string reason = $"Este PC no alcanzó a decodificar {res} ({_decoder.Backend}); se pidió menos resolución al celular.";
                    Log.Warn($"{DeviceId}: {reason}");
                    limitMsg = Protocol.BuildJson(Protocol.Limit, sid,
                        new LimitMessage { MaxHeight = _limitHeight, MaxPixelRate = _limitPixelRate, Reason = reason });
                }
                else if (!_settings.AutoLowerResolution || rate <= MinLimitPixelRate)
                {
                    _overloadWarning = $"Este PC no alcanza a decodificar {res} ({_decoder.Backend}): la imagen va a saltos." +
                        (_settings.AutoLowerResolution ? " Cierra otros programas pesados o usa menos celulares."
                            : " Activa \"Bajar resolución si este PC no alcanza\" o elige menos resolución en el celular.");
                }
            }
        }
        if (limitMsg != null && ep != null)
            for (int i = 0; i < 2; i++) _send(ep, limitMsg);
        return overloaded;
    }

    private SessionSnapshot BuildSnapshot(SessionState state, HelloMessage? h = null, int jitterBuf = 0)
    {
        h ??= new HelloMessage();
        double total = h.EncodeMs + h.RttMs / 2 + _avgLatency + (h.Fps > 0 ? 1000.0 / h.Fps : 33) * 0.5;
        string name;
        if (_settings.Names.TryGetValue(DeviceId, out var custom) && !string.IsNullOrWhiteSpace(custom)) name = custom;
        else name = DefaultName(h);
        string warning, limitInfo;
        lock (_gate)
        {
            bool limited = (_limitPixelRate > 0 || _limitHeight > 0) && _settings.AutoLowerResolution;
            limitInfo = limited ? $"Este PC no alcanzaba {(_limitFrom.Length > 0 ? _limitFrom : "la resolución anterior")}: se pidió menos resolución al celular." : "";
            warning = _overloadWarning.Length > 0 ? _overloadWarning : limitInfo;
        }
        return new SessionSnapshot(
            DeviceId, name, h.Name, h.Model, state, h.Codec.ToUpperInvariant(), h.Width, h.Height, h.Fps,
            _outputFps, _rxMbps, h.BitrateKbps, _lossPct, _lostPct, _jitterMs, _avgLatency,
            state == SessionState.EnVivo ? total : 0, h.RttMs,
            _assembler.FramesDropped + _assembler.FramesSkippedSlowPc + _lateSkipped + _staleSkipped,
            _decoder?.Backend ?? "-", _ndiConnections, h.ThermalLevel, h.BatteryPct, h.BatteryTempC, h.Charging,
            h.LatencyMode, jitterBuf, Endpoint?.ToString() ?? "",
            h.CpuPct,
            _liveSinceMs >= 0 ? (Clock.NowMs - _liveSinceMs) / 1000.0 : 0,
            _liveSinceMs >= 0 && Clock.NowMs - _liveSinceMs > 1000 ? _liveFrames * 1000.0 / (Clock.NowMs - _liveSinceMs) : 0,
            _minFpsLive == double.MaxValue ? 0 : _minFpsLive,
            _maxTempC,
            _colorInfo,
            RangeOverride,
            warning,
            limitInfo);
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
