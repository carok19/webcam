using System.Runtime.InteropServices;
using FFmpeg.AutoGen;
using CamaraNdi.Core.Ndi;

namespace CamaraNdi.Core.Decoding;

/// <summary>
/// Decodificador H.264/HEVC con FFmpeg.
/// GPU: usa el método que la prueba de arranque (<see cref="DecoderProbe"/>) encontró que funciona
/// (D3D11VA, NVIDIA CUDA o DXVA2). Si la GPU no puede con el video, NO se queda en CPU con un solo
/// hilo (eso daba ~1 fps en 4K): avisa con <see cref="HwFellBack"/> y la sesión lo recrea en CPU
/// con un hilo por núcleo.
/// </summary>
public sealed unsafe class VideoDecoder : IDisposable
{
    private static readonly object InitGate = new();
    private static bool _ffmpegReady;
    private static readonly AVCodecContext_get_format GetFormatCallback = GetHwFormat;
    private static readonly av_log_set_callback_callback LogCallback = OnFfmpegLog;

    private AVCodecContext* _ctx;
    private AVPacket* _pkt;
    private AVFrame* _frame;
    private AVFrame* _swFrame;
    private AVBufferRef* _hwDevice;

    public VideoCodec Codec { get; }
    public string Backend { get; private set; } = "CPU";
    /// <summary>Se pidió GPU pero FFmpeg tuvo que seguir en CPU (la GPU no soporta este video).</summary>
    public bool HwFellBack { get; private set; }
    /// <summary>Altura del video con el que la GPU no pudo (0 si no pasó).</summary>
    public int FellBackHeight { get; private set; }
    public bool IsHardware => _hwDevice != null && !HwFellBack;
    public int Threads { get; private set; } = 1;

    public static string FfmpegStatus { get; private set; } = "FFmpeg no cargado";

    /// <summary>Carga las DLL de FFmpeg (carpeta "ffmpeg" junto al .exe, o la del sistema en Linux).</summary>
    public static bool EnsureFfmpeg(string? rootPath = null)
    {
        lock (InitGate)
        {
            if (_ffmpegReady) return true;
            try
            {
                ffmpeg.RootPath = rootPath ?? DefaultRoot();
                DynamicallyLoadedBindings.Initialize();
                var ver = ffmpeg.av_version_info();
                ffmpeg.av_log_set_level(ffmpeg.AV_LOG_WARNING);
                ffmpeg.av_log_set_callback(LogCallback);
                FfmpegStatus = $"FFmpeg {ver}";
                Log.Info($"{FfmpegStatus} cargado desde {ffmpeg.RootPath}");
                _ffmpegReady = true;
            }
            catch (Exception ex)
            {
                FfmpegStatus = "No se pudo cargar FFmpeg: " + ex.Message;
                Log.Error(FfmpegStatus, ex);
            }
            return _ffmpegReady;
        }
    }

    private static string DefaultRoot()
    {
        var local = Path.Combine(AppContext.BaseDirectory, "ffmpeg");
        if (System.IO.Directory.Exists(local)) return local;
        if (OperatingSystem.IsLinux())
        {
            foreach (var d in new[] { "/usr/lib/x86_64-linux-gnu", "/usr/lib/aarch64-linux-gnu", "/usr/lib", "/usr/local/lib" })
                if (File.Exists(Path.Combine(d, "libavcodec.so.60"))) return d;
        }
        return AppContext.BaseDirectory;
    }

    // ------------------------------------------------------------------ registro de FFmpeg

    /// <summary>Durante la prueba de GPU se guardan también los mensajes "verbose" (nombre de la GPU).</summary>
    internal static volatile Action<string>? VerboseSink;
    private static long _logWindowStart;
    private static int _logCount;
    private static string? _lastLogLine;

    private static void OnFfmpegLog(void* avcl, int level, string format, byte* vl)
    {
        var sink = VerboseSink;
        if (level > ffmpeg.AV_LOG_WARNING && (sink == null || level > ffmpeg.AV_LOG_VERBOSE)) return;
        try
        {
            const int size = 1024;
            var buf = stackalloc byte[size];
            int prefix = 1;
            ffmpeg.av_log_format_line2(avcl, level, format, vl, buf, size, &prefix);
            var line = Marshal.PtrToStringUTF8((IntPtr)buf)?.Trim();
            if (string.IsNullOrEmpty(line)) return;
            if (sink != null && level > ffmpeg.AV_LOG_WARNING) { sink(line); return; }
            // Sin inundar el registro: máximo 30 líneas cada 10 s y sin repetidas.
            long now = Clock.NowMs;
            lock (InitGate)
            {
                if (now - _logWindowStart > 10_000) { _logWindowStart = now; _logCount = 0; }
                if (line == _lastLogLine || ++_logCount > 30) return;
                _lastLogLine = line;
            }
            sink?.Invoke(line);
            if (level <= ffmpeg.AV_LOG_ERROR) Log.Warn("FFmpeg: " + line);
            else Log.Info("FFmpeg: " + line);
        }
        catch
        {
            // Nunca dejar que un error de registro tumbe el decodificador.
        }
    }

    // ------------------------------------------------------------------ creación

    /// <summary>Métodos de GPU a probar en esta plataforma, en orden de preferencia.</summary>
    public static IReadOnlyList<string> PlatformHwTypes() =>
        OperatingSystem.IsWindows() ? new[] { "d3d11va", "cuda", "dxva2" }
        : OperatingSystem.IsLinux() ? new[] { "vaapi", "cuda" }
        : new[] { "videotoolbox" };

    private static AVHWDeviceType ToDeviceType(string name) => name switch
    {
        "d3d11va" => AVHWDeviceType.AV_HWDEVICE_TYPE_D3D11VA,
        "dxva2" => AVHWDeviceType.AV_HWDEVICE_TYPE_DXVA2,
        "cuda" => AVHWDeviceType.AV_HWDEVICE_TYPE_CUDA,
        "vaapi" => AVHWDeviceType.AV_HWDEVICE_TYPE_VAAPI,
        "videotoolbox" => AVHWDeviceType.AV_HWDEVICE_TYPE_VIDEOTOOLBOX,
        _ => AVHWDeviceType.AV_HWDEVICE_TYPE_NONE,
    };

    private static string Label(string hw) => hw switch
    {
        "d3d11va" => "GPU (D3D11VA)",
        "dxva2" => "GPU (DXVA2)",
        "cuda" => "GPU (NVIDIA)",
        "vaapi" => "GPU (VAAPI)",
        _ => "GPU",
    };

    /// <param name="hwTypes">Métodos de GPU a intentar (vacío = sólo CPU).</param>
    /// <param name="lowLatency">En los modos rápidos se usan menos hilos de CPU (cada hilo por frame suma un frame de retraso).</param>
    public VideoDecoder(VideoCodec codec, IEnumerable<string> hwTypes, bool lowLatency) : this(codec, hwTypes, lowLatency, null) { }

    /// <param name="forceThreads">Sólo para pruebas: fijar el número de hilos de CPU.</param>
    internal VideoDecoder(VideoCodec codec, IEnumerable<string> hwTypes, bool lowLatency, int? forceThreads)
    {
        if (!EnsureFfmpeg()) throw new InvalidOperationException(FfmpegStatus);
        Codec = codec;
        var id = codec == VideoCodec.Hevc ? AVCodecID.AV_CODEC_ID_HEVC : AVCodecID.AV_CODEC_ID_H264;
        var dec = ffmpeg.avcodec_find_decoder(id);
        if (dec == null) throw new InvalidOperationException($"FFmpeg no tiene decodificador para {codec}");

        _ctx = ffmpeg.avcodec_alloc_context3(dec);

        foreach (var hw in hwTypes)
        {
            var t = ToDeviceType(hw);
            if (t == AVHWDeviceType.AV_HWDEVICE_TYPE_NONE) continue;
            AVBufferRef* dev = null;
            // D3D11VA: pedir el adaptador 0 (el mismo que usa por defecto) hace que FFmpeg registre
            // "Using device …(nombre)", y así se puede mostrar qué GPU tiene el PC.
            string? device = t == AVHWDeviceType.AV_HWDEVICE_TYPE_D3D11VA ? "0" : null;
            if (ffmpeg.av_hwdevice_ctx_create(&dev, t, device, null, 0) < 0 || dev == null) continue;
            _hwDevice = dev;
            _ctx->hw_device_ctx = ffmpeg.av_buffer_ref(dev);
            _ctx->get_format = (AVCodecContext_get_format_func)GetFormatCallback;
            // Algunos codificadores de celular marcan un nivel/perfil más alto del que usan de verdad:
            // no rechazar la GPU por eso (si de verdad no puede, igual cae a CPU y se recrea).
            _ctx->hwaccel_flags |= ffmpeg.AV_HWACCEL_FLAG_IGNORE_LEVEL | ffmpeg.AV_HWACCEL_FLAG_ALLOW_PROFILE_MISMATCH;
            Backend = Label(hw);
            break;
        }

        if (_hwDevice != null)
        {
            // GPU: un hilo y salida inmediata.
            _ctx->flags |= ffmpeg.AV_CODEC_FLAG_LOW_DELAY;
            _ctx->thread_count = 1;
            Threads = 1;
        }
        else
        {
            // CPU: los celulares mandan 1 "slice" por imagen, así que sólo los hilos por frame
            // reparten el trabajo entre núcleos. Cada hilo suma ~1 frame de retraso.
            int cores = Math.Max(1, Environment.ProcessorCount);
            Threads = forceThreads ?? Math.Clamp(lowLatency ? Math.Min(cores, 4) : cores, 1, 16);
            _ctx->thread_count = Threads;
            _ctx->thread_type = Threads > 1 ? ffmpeg.FF_THREAD_FRAME | ffmpeg.FF_THREAD_SLICE : ffmpeg.FF_THREAD_SLICE;
            if (Threads == 1) _ctx->flags |= ffmpeg.AV_CODEC_FLAG_LOW_DELAY;
            Backend = Threads > 1 ? $"CPU ({Threads} hilos)" : "CPU";
        }

        int r = ffmpeg.avcodec_open2(_ctx, dec, null);
        if (r < 0)
        {
            Dispose();
            throw new InvalidOperationException("avcodec_open2 falló: " + Err(r));
        }
        _pkt = ffmpeg.av_packet_alloc();
        _frame = ffmpeg.av_frame_alloc();
        _swFrame = ffmpeg.av_frame_alloc();
        Log.Info($"Decodificador {codec} abierto ({Backend})");
    }

    private static AVPixelFormat HwPixFmt(AVHWDeviceType t) => t switch
    {
        AVHWDeviceType.AV_HWDEVICE_TYPE_D3D11VA => AVPixelFormat.AV_PIX_FMT_D3D11,
        AVHWDeviceType.AV_HWDEVICE_TYPE_DXVA2 => AVPixelFormat.AV_PIX_FMT_DXVA2_VLD,
        AVHWDeviceType.AV_HWDEVICE_TYPE_CUDA => AVPixelFormat.AV_PIX_FMT_CUDA,
        AVHWDeviceType.AV_HWDEVICE_TYPE_VAAPI => AVPixelFormat.AV_PIX_FMT_VAAPI,
        AVHWDeviceType.AV_HWDEVICE_TYPE_VIDEOTOOLBOX => AVPixelFormat.AV_PIX_FMT_VIDEOTOOLBOX,
        _ => AVPixelFormat.AV_PIX_FMT_NONE,
    };

    private static AVPixelFormat GetHwFormat(AVCodecContext* ctx, AVPixelFormat* fmts)
    {
        var want = AVPixelFormat.AV_PIX_FMT_NONE;
        if (ctx->hw_device_ctx != null)
            want = HwPixFmt(((AVHWDeviceContext*)ctx->hw_device_ctx->data)->type);
        AVPixelFormat firstSw = AVPixelFormat.AV_PIX_FMT_NONE;
        for (var p = fmts; *p != AVPixelFormat.AV_PIX_FMT_NONE; p++)
        {
            if (*p == want) return *p;
            if (firstSw == AVPixelFormat.AV_PIX_FMT_NONE)
            {
                var desc = ffmpeg.av_pix_fmt_desc_get(*p);
                if (desc != null && (desc->flags & ffmpeg.AV_PIX_FMT_FLAG_HWACCEL) == 0) firstSw = *p;
            }
        }
        // La GPU no soporta este perfil/resolución: FFmpeg sigue en CPU (y avisamos con HwFellBack).
        return firstSw;
    }

    // ------------------------------------------------------------------ decodificación

    public enum Result { Ok, NoPicture, Error }

    /// <summary>
    /// Decodifica un frame (Annex-B). Las imágenes que salgan (con hilos por frame salen con unos
    /// frames de retraso, y a veces dos juntas) se agregan a <paramref name="outputs"/> en NV12 o I420.
    /// </summary>
    public Result Decode(byte[] data, int length, long ptsUs, Func<int, VideoBuffer> rent, List<VideoBuffer> outputs)
    {
        var result = Result.NoPicture;
        fixed (byte* p = data)
        {
            _pkt->data = p;
            _pkt->size = length;
            _pkt->pts = ptsUs;
            _pkt->dts = ptsUs;
            int r = ffmpeg.avcodec_send_packet(_ctx, _pkt);
            // Si el decodificador está lleno, primero hay que sacarle imágenes y luego reenviar:
            // nunca se descarta un paquete (eso rompería las referencias del video).
            int guard = 0;
            while (r == ffmpeg.AVERROR(ffmpeg.EAGAIN) && guard++ < 64)
            {
                var rr = Receive(rent, outputs);
                if (rr == Result.Error) { result = Result.Error; break; }
                if (rr == Result.Ok) result = Result.Ok;
                r = ffmpeg.avcodec_send_packet(_ctx, _pkt);
            }
            _pkt->data = null;
            _pkt->size = 0;
            if (r < 0 && r != ffmpeg.AVERROR(ffmpeg.EAGAIN)) return Result.Error;
        }
        var res = Receive(rent, outputs);
        if (res == Result.Error) return Result.Error;
        return res == Result.Ok || result == Result.Ok ? Result.Ok : Result.NoPicture;
    }

    private Result Receive(Func<int, VideoBuffer> rent, List<VideoBuffer> outputs)
    {
        var result = Result.NoPicture;
        while (true)
        {
            int r = ffmpeg.avcodec_receive_frame(_ctx, _frame);
            if (r == ffmpeg.AVERROR(ffmpeg.EAGAIN) || r == ffmpeg.AVERROR_EOF) break;
            if (r < 0) return Result.Error;

            AVFrame* src = _frame;
            if (_frame->hw_frames_ctx != null)
            {
                ffmpeg.av_frame_unref(_swFrame);
                _swFrame->format = (int)AVPixelFormat.AV_PIX_FMT_NV12;
                if (ffmpeg.av_hwframe_transfer_data(_swFrame, _frame, 0) < 0)
                {
                    ffmpeg.av_frame_unref(_frame);
                    return Result.Error;
                }
                src = _swFrame;
            }
            else if (_hwDevice != null && !HwFellBack)
            {
                HwFellBack = true;
                FellBackHeight = _frame->height;
                Backend = "CPU (la GPU no soporta este video)";
                Log.Warn($"{Codec} {_frame->width}x{_frame->height}: la GPU no lo pudo decodificar");
            }
            bool corrupt = (_frame->flags & ffmpeg.AV_FRAME_FLAG_CORRUPT) != 0 || _frame->decode_error_flags != 0;
            if (corrupt)
            {
                ffmpeg.av_frame_unref(_frame);
                return Result.Error;
            }
            var buf = Copy(src, rent);
            if (buf != null)
            {
                buf.SignaledRange = (int)_frame->color_range;
                if ((AVPixelFormat)_frame->format == AVPixelFormat.AV_PIX_FMT_YUVJ420P) buf.SignaledRange = (int)AVColorRange.AVCOL_RANGE_JPEG;
                buf.SignaledMatrix = (int)_frame->colorspace;
                buf.PtsUs = _frame->pts;
            }
            ffmpeg.av_frame_unref(_frame);
            if (buf == null) return Result.Error;
            outputs.Add(buf);
            result = Result.Ok;
        }
        return result;
    }

    /// <summary>
    /// Prueba de arranque: ¿este método de GPU decodifica esta muestra? Devuelve true sólo si salió
    /// una imagen decodificada de verdad por la GPU.
    /// </summary>
    public static bool ProbeHardware(VideoCodec codec, string hwType, byte[] sample)
    {
        VideoDecoder? d = null;
        try
        {
            d = new VideoDecoder(codec, new[] { hwType }, lowLatency: true);
            if (d._hwDevice == null) return false;
            var outputs = new List<VideoBuffer>();
            d.Decode(sample, sample.Length, 0, size => new VideoBuffer(size), outputs);
            // vaciar lo que quede dentro del decodificador
            ffmpeg.avcodec_send_packet(d._ctx, null);
            d.Receive(size => new VideoBuffer(size), outputs);
            return outputs.Count > 0 && !d.HwFellBack;
        }
        catch (Exception ex)
        {
            Log.Info($"Prueba {codec} con {hwType}: {ex.Message}");
            return false;
        }
        finally
        {
            d?.Dispose();
        }
    }

    private static VideoBuffer? Copy(AVFrame* f, Func<int, VideoBuffer> rent)
    {
        int w = f->width & ~1, h = f->height & ~1;
        var fmt = (AVPixelFormat)f->format;
        bool nv12 = fmt == AVPixelFormat.AV_PIX_FMT_NV12;
        bool i420 = fmt is AVPixelFormat.AV_PIX_FMT_YUV420P or AVPixelFormat.AV_PIX_FMT_YUVJ420P;
        if (!nv12 && !i420)
        {
            Log.Error($"Formato de píxel no soportado: {fmt}");
            return null;
        }
        var b = rent(VideoBuffer.SizeFor(w, h));
        b.Width = w;
        b.Height = h;
        b.Stride = w;
        b.FourCC = nv12 ? NdiLib.FourCC_NV12 : NdiLib.FourCC_I420;
        b.Length = VideoBuffer.SizeFor(w, h);
        fixed (byte* dst = b.Data)
        {
            CopyPlane(f->data[0], f->linesize[0], dst, w, w, h);
            if (nv12)
            {
                CopyPlane(f->data[1], f->linesize[1], dst + w * h, w, w, h / 2);
            }
            else
            {
                int cw = w / 2, ch = h / 2;
                CopyPlane(f->data[1], f->linesize[1], dst + w * h, cw, cw, ch);
                CopyPlane(f->data[2], f->linesize[2], dst + w * h + cw * ch, cw, cw, ch);
            }
        }
        return b;
    }

    private static void CopyPlane(byte* src, int srcStride, byte* dst, int dstStride, int rowBytes, int rows)
    {
        if (srcStride == dstStride && srcStride == rowBytes)
        {
            Buffer.MemoryCopy(src, dst, (long)rowBytes * rows, (long)rowBytes * rows);
            return;
        }
        for (int y = 0; y < rows; y++)
            Buffer.MemoryCopy(src + (long)y * srcStride, dst + (long)y * dstStride, rowBytes, rowBytes);
    }

    private static string Err(int code)
    {
        var buf = stackalloc byte[256];
        ffmpeg.av_strerror(code, buf, 256);
        return Marshal.PtrToStringUTF8((IntPtr)buf) ?? code.ToString();
    }

    public void Dispose()
    {
        if (_pkt != null) { var p = _pkt; ffmpeg.av_packet_free(&p); _pkt = null; }
        if (_frame != null) { var f = _frame; ffmpeg.av_frame_free(&f); _frame = null; }
        if (_swFrame != null) { var f = _swFrame; ffmpeg.av_frame_free(&f); _swFrame = null; }
        if (_ctx != null) { var c = _ctx; ffmpeg.avcodec_free_context(&c); _ctx = null; }
        if (_hwDevice != null) { var d = _hwDevice; ffmpeg.av_buffer_unref(&d); _hwDevice = null; }
    }
}
