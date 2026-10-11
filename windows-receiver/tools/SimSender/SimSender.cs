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

    public long FramesSent { get; private set; }
    public long PacketsSent { get; private set; }
    public long PacketsDroppedOnPurpose { get; private set; }
    public long Retransmitted { get; private set; }
    public long KeyframeRequests { get; private set; }
    public long Reports { get; private set; }
    public ReportData LastReport { get; private set; }
    public double LastRttMs { get; private set; }
    public bool Disconnected { get; private set; }

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
        long i = 0;
        while (sw.Elapsed < duration && !ct.IsCancellationRequested && !Disconnected)
        {
            long nowMs = sw.ElapsedMilliseconds;
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
            long next = (i * 1000) / Fps;
            long wait = next - sw.ElapsedMilliseconds;
            if (wait > 0) Thread.Sleep((int)wait);
        }
        Send(Protocol.BuildJson(Protocol.Bye, _sessionId, new { }));
        _running = false;
        ffmpeg.av_packet_free(&pkt);
        ffmpeg.av_frame_free(&frame);
        ffmpeg.avcodec_free_context(&ctx);
        _sock.Close();
        rx.Join(500);
    }

    private static long NowUs() => (long)(Clock.NowMsPrecise * 1000);

    private void SendHello()
    {
        var h = new HelloMessage
        {
            DeviceId = DeviceId, Name = Name, Model = "Simulador", Codec = Codec == VideoCodec.Hevc ? "hevc" : "h264",
            Width = Width, Height = Height, Fps = Fps, BitrateKbps = BitrateKbps, MaxBitrateKbps = BitrateKbps,
            LatencyMode = "minima", Streaming = true, BatteryPct = 100, AppVersion = "sim", RttMs = LastRttMs,
        };
        Send(Protocol.BuildJson(Protocol.Hello, _sessionId, h));
    }

    private void SendFrame(ReadOnlySpan<byte> data, bool key, long ptsUs)
    {
        int count = (data.Length + Protocol.MaxPayload - 1) / Protocol.MaxPayload;
        uint fid = _frameId++;
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
            Send(buf);
            PacketsSent++;
        }
        FramesSent++;
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
                y[row * ys + col] = (byte)(((col + shift) * 255 / w) & 0xFF);
        for (int row = 0; row < h / 2; row++)
            for (int col = 0; col < w / 2; col++)
            {
                u[row * us + col] = (byte)(128 + 60 * Math.Sin((col + n) * 0.02));
                v[row * vs + col] = (byte)(128 + 60 * Math.Cos((row + n) * 0.03));
            }
        int bx = (int)(n * 12 % (w - 64));
        for (int row = h / 2 - 32; row < h / 2 + 32; row++)
            for (int col = bx; col < bx + 64; col++) y[row * ys + col] = 235;
    }

    public void Dispose() { _running = false; try { _sock?.Close(); } catch { } }
}
