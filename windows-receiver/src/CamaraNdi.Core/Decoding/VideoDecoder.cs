using System.Runtime.InteropServices;
using FFmpeg.AutoGen;
using CamaraNdi.Core.Ndi;

namespace CamaraNdi.Core.Decoding;

/// <summary>
/// Decodificador H.264/HEVC con FFmpeg. Intenta GPU (D3D11VA, luego DXVA2) y si no, CPU.
/// Configurado para baja latencia: sin hilos por frame (que añaden 1 frame de retraso por hilo),
/// AV_CODEC_FLAG_LOW_DELAY, y salida inmediata de cada frame.
/// </summary>
public sealed unsafe class VideoDecoder : IDisposable
{
    private static readonly object InitGate = new();
    private static bool _ffmpegReady;
    private static readonly AVCodecContext_get_format GetFormatCallback = GetHwFormat;

    private AVCodecContext* _ctx;
    private AVPacket* _pkt;
    private AVFrame* _frame;
    private AVFrame* _swFrame;
    private AVBufferRef* _hwDevice;

    public VideoCodec Codec { get; }
    public string Backend { get; private set; } = "CPU";
    public bool IsHardware => _hwDevice != null;

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
                ffmpeg.av_log_set_level(ffmpeg.AV_LOG_ERROR);
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

    /// <param name="mode">"auto", "gpu" o "cpu"</param>
    public VideoDecoder(VideoCodec codec, string mode = "auto")
    {
        if (!EnsureFfmpeg()) throw new InvalidOperationException(FfmpegStatus);
        Codec = codec;
        var id = codec == VideoCodec.Hevc ? AVCodecID.AV_CODEC_ID_HEVC : AVCodecID.AV_CODEC_ID_H264;
        var dec = ffmpeg.avcodec_find_decoder(id);
        if (dec == null) throw new InvalidOperationException($"FFmpeg no tiene decodificador para {codec}");

        _ctx = ffmpeg.avcodec_alloc_context3(dec);
        _ctx->flags |= ffmpeg.AV_CODEC_FLAG_LOW_DELAY;

        if (mode != "cpu") TryHardware();

        if (_hwDevice == null)
        {
            // CPU: hilos por "slice" (no añaden retraso). Hilos por frame están prohibidos aquí.
            _ctx->thread_type = ffmpeg.FF_THREAD_SLICE;
            _ctx->thread_count = 0;
            Backend = "CPU";
        }
        else
        {
            _ctx->thread_count = 1;
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

    private void TryHardware()
    {
        var types = OperatingSystem.IsWindows()
            ? new[] { AVHWDeviceType.AV_HWDEVICE_TYPE_D3D11VA, AVHWDeviceType.AV_HWDEVICE_TYPE_DXVA2 }
            : OperatingSystem.IsLinux()
                ? new[] { AVHWDeviceType.AV_HWDEVICE_TYPE_VAAPI }
                : new[] { AVHWDeviceType.AV_HWDEVICE_TYPE_VIDEOTOOLBOX };
        foreach (var t in types)
        {
            AVBufferRef* dev = null;
            if (ffmpeg.av_hwdevice_ctx_create(&dev, t, null, null, 0) < 0 || dev == null) continue;
            _hwDevice = dev;
            _ctx->hw_device_ctx = ffmpeg.av_buffer_ref(dev);
            _ctx->get_format = (AVCodecContext_get_format_func)GetFormatCallback;
            Backend = t switch
            {
                AVHWDeviceType.AV_HWDEVICE_TYPE_D3D11VA => "GPU (D3D11VA)",
                AVHWDeviceType.AV_HWDEVICE_TYPE_DXVA2 => "GPU (DXVA2)",
                AVHWDeviceType.AV_HWDEVICE_TYPE_VAAPI => "GPU (VAAPI)",
                _ => "GPU",
            };
            return;
        }
    }

    private static AVPixelFormat HwPixFmt(AVHWDeviceType t) => t switch
    {
        AVHWDeviceType.AV_HWDEVICE_TYPE_D3D11VA => AVPixelFormat.AV_PIX_FMT_D3D11,
        AVHWDeviceType.AV_HWDEVICE_TYPE_DXVA2 => AVPixelFormat.AV_PIX_FMT_DXVA2_VLD,
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
        // La GPU no soporta este perfil/resolución: FFmpeg sigue en CPU.
        return firstSw;
    }

    public enum Result { Ok, NoPicture, Error }

    /// <summary>
    /// Decodifica un frame (Annex-B) y copia la imagen a <paramref name="output"/> (NV12 o I420).
    /// </summary>
    public Result Decode(byte[] data, int length, long ptsUs, Func<int, VideoBuffer> rent, out VideoBuffer? output)
    {
        output = null;
        fixed (byte* p = data)
        {
            _pkt->data = p;
            _pkt->size = length;
            _pkt->pts = ptsUs;
            _pkt->dts = ptsUs;
            int r = ffmpeg.avcodec_send_packet(_ctx, _pkt);
            _pkt->data = null;
            _pkt->size = 0;
            if (r < 0 && r != ffmpeg.AVERROR(ffmpeg.EAGAIN)) return Result.Error;
        }

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
            else if (_hwDevice != null && !Backend.StartsWith("CPU"))
            {
                Backend = "CPU (la GPU no soporta este perfil)";
                Log.Warn(Backend);
            }
            bool corrupt = (_frame->flags & ffmpeg.AV_FRAME_FLAG_CORRUPT) != 0 || _frame->decode_error_flags != 0;
            if (corrupt)
            {
                ffmpeg.av_frame_unref(_frame);
                return Result.Error;
            }
            // Si llegan varios (no debería con LOW_DELAY), nos quedamos con el último.
            var buf = Copy(src, rent);
            if (buf != null)
            {
                buf.SignaledRange = (int)_frame->color_range;
                if ((AVPixelFormat)_frame->format == AVPixelFormat.AV_PIX_FMT_YUVJ420P) buf.SignaledRange = (int)AVColorRange.AVCOL_RANGE_JPEG;
                buf.SignaledMatrix = (int)_frame->colorspace;
            }
            ffmpeg.av_frame_unref(_frame);
            if (buf == null) return Result.Error;
            output = buf;
            result = Result.Ok;
        }
        return result;
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
