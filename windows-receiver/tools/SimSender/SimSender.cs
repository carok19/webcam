using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using CamaraNdi.Core;
using FFmpeg.AutoGen;

namespace CamaraNdi.Sim;

/// <summary>
/// Emisor simulado: hace de "celular" usando FFmpeg (libx264/libx265) con una imagen de prueba.
/// Implementa la misma lógica que la app Android (HELLO, PING, VIDEO, NACK, keyframes),
/// y permite simular pérdida de paquetes. Sirve para probar el receptor sin celular.
/// </summary>
public sealed unsafe class SimSender : IDisposable
{
    public string Host { get; init; } = "127.0.0.1";
    public int Port { get; init; } = Protocol.DefaultMediaPort;
    public int Width { get; init; } = 1280;
    public int Height { get; init; } = 720;
    public int Fps { get; init; } = 30;
    public int BitrateKbps { get; init; } = 4000;
    public VideoCodec Codec { get; init; } = VideoCodec.H264;
    public double LossRate { get; init; }
    public string Name { get; init; } = "Simulador";
    public string DeviceId { get; init; } = "sim-" + Guid.NewGuid().ToString("N")[..8];
    public int Seed { get; init; } = 1234;
    /// <summary>Codificar en rango completo 0–255 (como muchos celulares Android).</summary>
    public bool FullRange { get; init; }
    /// <summary>Si es false, el video NO dice qué rango usa (algunos celulares hacen eso).</summary>
    public bool SignalRange { get; init; } = true;
    /// <summary>Modo que anuncia el "celular": minima, equilibrada, calidad o maxima.</summary>
    public string LatencyMode { get; init; } = "minima";
    /// <summary>Retraso de red al azar por frame, entre 0 y este valor (sin desordenar los frames).</summary>
    public int NetJitterMs { get; init; }
    /// <summary>Simula "Detener" y volver a "Transmitir" en el celular: pausa desde este segundo…</summary>
    public double PauseAtSec { get; init; } = -1;
    /// <summary>…durante estos segundos (manda BYE al pausar; al volver, HELLO y un keyframe).</summary>
    public double PauseForSec { get; init; }

    public long FramesSent { get; private set; }
    public long PacketsSent { get; private set; }
    public long PacketsDroppedOnPurpose { get; private set; }
    public long Retransmitted { get; private set; }
    public long KeyframeRequests { get; private set; }
    public long Reports { get; private set; }
    public ReportData LastReport { get; private set; }
    public double LastRttMs { get; private set; }
    public bool Disconnected { get; private set; }
    /// <summary>Último pedido de límite de resolución del receptor (PC que no alcanza).</summary>
    public LimitMessage? LastLimit { get; private set; }
    /// <summary>Capacidades de decodificación que anunció el receptor en HELLO_ACK.</summary>
    public DecodeCapsMessage? Caps { get; private set; }

    private readonly uint _sessionId = (uint)Random.Shared.Next(1, int.MaxValue);
    private Socket? _sock;
    private IPEndPoint? _target;
    private readonly byte[]?[] _ring = new byte[4096][];
    private readonly uint[] _ringSeq = new uint[4096];
    private readonly object _ringGate = new();
    private uint _seq;
    private uint _frameId;
    private volatile bool _forceKey = true;
    private volatile bool _running;
    private Random _rnd = new(1);
    private readonly System.Collections.Concurrent.BlockingCollection<(long DueMs, List<byte[]> Packets)> _delayed = new();
    private long _lastDueMs;
    private System.Diagnostics.Stopwatch _clock = new();

    public void Run(TimeSpan duration, CancellationToken ct = default)
    {
        _rnd = new Random(Seed);
        if (!CamaraNdi.Core.Decoding.VideoDecoder.EnsureFfmpeg()) throw new InvalidOperationException("FFmpeg no disponible");
        _sock = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        _sock.Bind(new IPEndPoint(IPAddress.Any, 0));
        _target = new IPEndPoint(IPAddress.Parse(Host), Port);
        _running = true;
        var rx = new Thread(ReceiveLoop) { IsBackground = true };
        rx.Start();
        _clock = System.Diagnostics.Stopwatch.StartNew();
        Thread? delayer = null;
        if (NetJitterMs > 0)
        {
            delayer = new Thread(DelayLoop) { IsBackground = true };
            delayer.Start();
        }

        var encName = Codec == VideoCodec.Hevc ? "libx265" : "libx264";
        var enc = ffmpeg.avcodec_find_encoder_by_name(encName);
        if (enc == null) throw new InvalidOperationException($"No hay {encName}");
        var ctx = ffmpeg.avcodec_alloc_context3(enc);
        ctx->width = Width;
        ctx->height = Height;
        ctx->time_base = new AVRational { num = 1, den = Fps };
        ctx->framerate = new AVRational { num = Fps, den = 1 };
        ctx->pix_fmt = AVPixelFormat.AV_PIX_FMT_YUV420P;
        ctx->color_range = FullRange && SignalRange ? AVColorRange.AVCOL_RANGE_JPEG
            : SignalRange ? AVColorRange.AVCOL_RANGE_MPEG : AVColorRange.AVCOL_RANGE_UNSPECIFIED;
        if (SignalRange) ctx->colorspace = AVColorSpace.AVCOL_SPC_BT709;
        ctx->bit_rate = BitrateKbps * 1000L;
        ctx->gop_size = Fps; // keyframe cada 1 s
        ctx->max_b_frames = 0;
        ffmpeg.av_opt_set(ctx->priv_data, "preset", "ultrafast", 0);
        ffmpeg.av_opt_set(ctx->priv_data, "tune", "zerolatency", 0);
        if (Codec == VideoCodec.Hevc) ffmpeg.av_opt_set(ctx->priv_data, "x265-params", "log-level=error:repeat-headers=1", 0);
        else ffmpeg.av_opt_set(ctx->priv_data, "x264-params", "repeat-headers=1", 0);
        if (ffmpeg.avcodec_open2(ctx, enc, null) < 0) throw new InvalidOperationException("No se pudo abrir el codificador");

        var frame = ffmpeg.av_frame_alloc();
        frame->format = (int)ctx->pix_fmt;
        frame->width = Width;
        frame->height = Height;
        ffmpeg.av_frame_get_buffer(frame, 32);
        var pkt = ffmpeg.av_packet_alloc();

        var sw = System.Diagnostics.Stopwatch.StartNew();
        long lastHello = -10_000, lastPing = 0;
        uint pingId = 0;
        long i = 0, frameBaseI = 0, frameBaseMs = 0;
        bool paused = false;
        while (sw.Elapsed < duration && !ct.IsCancellationRequested && !Disconnected)
        {
            long nowMs = sw.ElapsedMilliseconds;
            double sec = nowMs / 1000.0;
            bool wantPause = PauseAtSec >= 0 && sec >= PauseAtSec && sec < PauseAtSec + PauseForSec;
            if (wantPause)
            {
                if (!paused)
                {
                    paused = true;
                    Send(Protocol.BuildJson(Protocol.Bye, _sessionId, new { }));
                }
                Thread.Sleep(5);
                continue;
            }
            if (paused)
            {
                // Volver a transmitir: misma sesión, HELLO enseguida y el codificador empieza con un keyframe.
                paused = false;
                _forceKey = true;
                lastHello = -10_000;
                frameBaseI = i;
                frameBaseMs = nowMs;
            }
            if (nowMs - lastHello >= 1000) { lastHello = nowMs; SendHello(); }
            if (nowMs - lastPing >= 250) { lastPing = nowMs; Send(Protocol.BuildPing(_sessionId, pingId++, NowUs())); }

            ffmpeg.av_frame_make_writable(frame);
            Draw(frame, i);
            frame->pts = i;
            frame->pict_type = AVPictureType.AV_PICTURE_TYPE_NONE;
            if (_forceKey)
            {
                _forceKey = false;
                frame->pict_type = AVPictureType.AV_PICTURE_TYPE_I;
            }
            ffmpeg.avcodec_send_frame(ctx, frame);
            while (ffmpeg.avcodec_receive_packet(ctx, pkt) == 0)
            {
                bool key = (pkt->flags & ffmpeg.AV_PKT_FLAG_KEY) != 0;
                SendFrame(new ReadOnlySpan<byte>(pkt->data, pkt->size), key, NowUs());
                ffmpeg.av_packet_unref(pkt);
            }
            i++;
            long next = frameBaseMs + ((i - frameBaseI) * 1000) / Fps;
            long wait = next - sw.ElapsedMilliseconds;
            if (wait > 0) Thread.Sleep((int)wait);
        }
        _delayed.CompleteAdding();
        delayer?.Join(1000);
        Send(Protocol.BuildJson(Protocol.Bye, _sessionId, new { }));
        _running = false;
        ffmpeg.av_packet_free(&pkt);
        ffmpeg.av_frame_free(&frame);
        ffmpeg.avcodec_free_context(&ctx);
        _sock.Close();
        rx.Join(500);
    }

    /// <summary>
    /// Codifica <paramref name="frames"/> imágenes de prueba (el primero es IDR, sin B-frames) y devuelve
    /// cada frame comprimido. Sirve para medir decodificadores sin depender del programa ffmpeg.
    /// </summary>
    public List<byte[]> EncodeClip(int frames)
    {
        if (!CamaraNdi.Core.Decoding.VideoDecoder.EnsureFfmpeg()) throw new InvalidOperationException("FFmpeg no disponible");
        var encName = Codec == VideoCodec.Hevc ? "libx265" : "libx264";
        var enc = ffmpeg.avcodec_find_encoder_by_name(encName);
        if (enc == null) throw new InvalidOperationException($"No hay {encName}");
        var ctx = ffmpeg.avcodec_alloc_context3(enc);
        ctx->width = Width;
        ctx->height = Height;
        ctx->time_base = new AVRational { num = 1, den = Fps };
        ctx->framerate = new AVRational { num = Fps, den = 1 };
        ctx->pix_fmt = AVPixelFormat.AV_PIX_FMT_YUV420P;
        ctx->bit_rate = BitrateKbps * 1000L;
        ctx->gop_size = 250;
        ctx->max_b_frames = 0;
        ffmpeg.av_opt_set(ctx->priv_data, "preset", "ultrafast", 0);
        if (Codec == VideoCodec.Hevc) ffmpeg.av_opt_set(ctx->priv_data, "x265-params", "log-level=error:bframes=0", 0);
        if (ffmpeg.avcodec_open2(ctx, enc, null) < 0) throw new InvalidOperationException("No se pudo abrir el codificador");
        var frame = ffmpeg.av_frame_alloc();
        frame->format = (int)ctx->pix_fmt;
        frame->width = Width;
        frame->height = Height;
        ffmpeg.av_frame_get_buffer(frame, 32);
        var pkt = ffmpeg.av_packet_alloc();
        var list = new List<byte[]>();
        for (int i = 0; i < frames; i++)
        {
            ffmpeg.av_frame_make_writable(frame);
            Draw(frame, i);
            frame->pts = i;
            ffmpeg.avcodec_send_frame(ctx, frame);
            DrainPackets(ctx, pkt, list);
        }
        ffmpeg.avcodec_send_frame(ctx, null);
        DrainPackets(ctx, pkt, list);
        ffmpeg.av_packet_free(&pkt);
        ffmpeg.av_frame_free(&frame);
        ffmpeg.avcodec_free_context(&ctx);
        return list;
    }

    private static void DrainPackets(AVCodecContext* ctx, AVPacket* pkt, List<byte[]> list)
    {
        while (ffmpeg.avcodec_receive_packet(ctx, pkt) == 0)
        {
            list.Add(new ReadOnlySpan<byte>(pkt->data, pkt->size).ToArray());
            ffmpeg.av_packet_unref(pkt);
        }
    }

    private static long NowUs() => (long)(Clock.NowMsPrecise * 1000);

    private void SendHello()
    {
        var h = new HelloMessage
        {
            DeviceId = DeviceId, Name = Name, Model = "Simulador", Codec = Codec == VideoCodec.Hevc ? "hevc" : "h264",
            Width = Width, Height = Height, Fps = Fps, BitrateKbps = BitrateKbps, MaxBitrateKbps = BitrateKbps,
            LatencyMode = LatencyMode, Streaming = true, BatteryPct = 100, AppVersion = "sim", RttMs = LastRttMs,
        };
        Send(Protocol.BuildJson(Protocol.Hello, _sessionId, h));
    }

    private void SendFrame(ReadOnlySpan<byte> data, bool key, long ptsUs)
    {
        int count = (data.Length + Protocol.MaxPayload - 1) / Protocol.MaxPayload;
        uint fid = _frameId++;
        List<byte[]>? later = NetJitterMs > 0 ? new List<byte[]>(count) : null;
        for (int k = 0; k < count; k++)
        {
            var chunk = data.Slice(k * Protocol.MaxPayload, Math.Min(Protocol.MaxPayload, data.Length - k * Protocol.MaxPayload));
            var buf = new byte[Protocol.VideoHeaderSize + chunk.Length];
            uint seq = _seq++;
            Protocol.WriteVideo(buf, _sessionId, new VideoPacketHeader(seq, fid, (ushort)k, (ushort)count,
                key ? Protocol.FlagKeyframe : (byte)0, Codec, ptsUs), chunk);
            lock (_ringGate)
            {
                _ring[seq % _ring.Length] = buf;
                _ringSeq[seq % _ring.Length] = seq;
            }
            if (LossRate > 0 && _rnd.NextDouble() < LossRate) { PacketsDroppedOnPurpose++; continue; }
            if (later != null) later.Add(buf);
            else Send(buf);
            PacketsSent++;
        }
        if (later != null)
        {
            // Retraso al azar, pero nunca antes que el frame anterior (la red no desordena frames enteros).
            long due = Math.Max(_lastDueMs, _clock.ElapsedMilliseconds + _rnd.Next(0, NetJitterMs + 1));
            _lastDueMs = due;
            _delayed.Add((due, later));
        }
        FramesSent++;
    }

    private void DelayLoop()
    {
        foreach (var (due, packets) in _delayed.GetConsumingEnumerable())
        {
            long wait = due - _clock.ElapsedMilliseconds;
            if (wait > 0) Thread.Sleep((int)wait);
            foreach (var p in packets) Send(p);
        }
    }

    private void Send(byte[] b)
    {
        try { _sock!.SendTo(b, _target!); } catch (Exception) { }
    }

    private void ReceiveLoop()
    {
        var buf = new byte[65536];
        while (_running)
        {
            int n;
            try { n = _sock!.Receive(buf); }
            catch { if (!_running) break; continue; }
            if (!Protocol.TryReadHeader(buf.AsSpan(0, n), out var type, out _)) continue;
            switch (type)
            {
                case Protocol.Nack:
                    foreach (var s in Protocol.ReadNack(buf.AsSpan(0, n)))
                    {
                        byte[]? p;
                        lock (_ringGate) p = _ringSeq[s % _ring.Length] == s ? _ring[s % _ring.Length] : null;
                        if (p == null) continue;
                        var copy = (byte[])p.Clone();
                        copy[20] |= Protocol.FlagRetransmit;
                        // Las retransmisiones también pueden perderse.
                        if (LossRate > 0 && _rnd.NextDouble() < LossRate) continue;
                        Send(copy);
                        Retransmitted++;
                    }
                    break;
                case Protocol.KeyframeRequest:
                    KeyframeRequests++;
                    _forceKey = true;
                    break;
                case Protocol.Report:
                    LastReport = Protocol.ReadReport(buf.AsSpan(0, n));
                    Reports++;
                    break;
                case Protocol.Pong:
                    long t = BinaryPrimitives.ReadInt64LittleEndian(buf.AsSpan(12));
                    LastRttMs = (NowUs() - t) / 1000.0;
                    break;
                case Protocol.Disconnect:
                    Disconnected = true;
                    break;
                case Protocol.HelloAck:
                    Caps = Protocol.ParseJson<HelloAckMessage>(buf.AsSpan(0, n))?.Decode ?? Caps;
                    break;
                case Protocol.Limit:
                    LastLimit = Protocol.ParseJson<LimitMessage>(buf.AsSpan(0, n));
                    break;
            }
        }
    }

    /// <summary>Barras de color que se mueven + un bloque blanco que avanza (para ver fluidez).</summary>
    private void Draw(AVFrame* f, long n)
    {
        int w = Width, h = Height;
        byte* y = f->data[0]; int ys = f->linesize[0];
        byte* u = f->data[1]; int us = f->linesize[1];
        byte* v = f->data[2]; int vs = f->linesize[2];
        int shift = (int)(n * 8 % w);
        for (int row = 0; row < h; row++)
            for (int col = 0; col < w; col++)
            {
                int g = ((col + shift) * 255 / w) % 256;
                // En rango limitado la imagen va de 16 (negro) a 235 (blanco).
                y[row * ys + col] = (byte)(FullRange ? g : 16 + g * 219 / 255);
            }
        for (int row = 0; row < h / 2; row++)
            for (int col = 0; col < w / 2; col++)
            {
                u[row * us + col] = (byte)(128 + 60 * Math.Sin((col + n) * 0.02));
                v[row * vs + col] = (byte)(128 + 60 * Math.Cos((row + n) * 0.03));
            }
        int bx = (int)(n * 12 % (w - 64));
        for (int row = h / 2 - 32; row < h / 2 + 32; row++)
            for (int col = bx; col < bx + 64; col++) y[row * ys + col] = (byte)(FullRange ? 255 : 235);
    }

    public void Dispose() { _running = false; try { _sock?.Close(); } catch { } }
}
