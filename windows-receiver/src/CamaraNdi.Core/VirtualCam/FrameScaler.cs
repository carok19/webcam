using CamaraNdi.Core.Ndi;
using FFmpeg.AutoGen;

namespace CamaraNdi.Core.VirtualCam;

/// <summary>
/// Lleva una imagen decodificada (NV12 o I420) al tamaño que pidió el programa (OBS) en NV12.
/// Caso típico: celular en 4K y OBS en 1080p = reducción exacta a la mitad (promedio 2x2, la mejor
/// calidad y la más barata). Otros tamaños: swscale de FFmpeg (área para reducir, Lanczos para
/// agrandar); si no está, un bilineal propio.
/// </summary>
public sealed unsafe class FrameScaler : IDisposable
{
    private SwsContext* _sws;
    private bool _swsFailed;
    private byte[]? _tmp;

    /// <summary>Para pruebas: no usar swscale (probar el respaldo).</summary>
    public bool DisableSwscale { get; set; }

    public void Convert(VideoBuffer src, byte* dst, int tw, int th)
    {
        fixed (byte* s = src.Data) Convert(s, src.Width, src.Height, src.FourCC == NdiLib.FourCC_NV12, dst, tw, th);
    }

    /// <summary>Imagen de origen tal como la deja el decodificador (planos contiguos, stride = ancho).</summary>
    public void Convert(byte* s, int sw, int sh, bool nv12, byte* dst, int tw, int th)
    {
        {
            if (sw == tw && sh == th)
            {
                CopySameSize(s, sw, sh, nv12, dst);
                return;
            }
            if (sw == 2 * tw && sh == 2 * th && tw % 2 == 0 && th % 2 == 0)
            {
                Halve(s, sw, sh, nv12, dst);
                return;
            }
            if (!_swsFailed && !DisableSwscale && TrySwscale(s, sw, sh, nv12, dst, tw, th)) return;
            byte* nv = s;
            if (!nv12)
            {
                // El bilineal trabaja en NV12: pasar I420 a NV12 primero.
                int need = sw * sh * 3 / 2;
                if (_tmp == null || _tmp.Length < need) _tmp = GC.AllocateUninitializedArray<byte>(need, pinned: true);
                fixed (byte* t = _tmp)
                {
                    CopySameSize(s, sw, sh, false, t);
                    Bilinear(t, sw, sh, sw, dst, tw, th, tw, 1);
                    Bilinear(t + (long)sw * sh, sw / 2, sh / 2, sw, dst + (long)tw * th, tw / 2, th / 2, tw, 2);
                }
                return;
            }
            Bilinear(nv, sw, sh, sw, dst, tw, th, tw, 1);
            Bilinear(nv + (long)sw * sh, sw / 2, sh / 2, sw, dst + (long)tw * th, tw / 2, th / 2, tw, 2);
        }
    }

    internal static void CopySameSize(byte* s, int w, int h, bool nv12, byte* dst)
    {
        long ySize = (long)w * h;
        if (nv12)
        {
            Buffer.MemoryCopy(s, dst, ySize * 3 / 2, ySize * 3 / 2);
            return;
        }
        Buffer.MemoryCopy(s, dst, ySize, ySize);
        // I420 -> NV12: intercalar U y V.
        int cw = w / 2, ch = h / 2;
        byte* u = s + ySize;
        byte* v = u + (long)cw * ch;
        byte* d = dst + ySize;
        for (int y = 0; y < ch; y++)
        {
            byte* ur = u + (long)y * cw;
            byte* vr = v + (long)y * cw;
            byte* dr = d + (long)y * w;
            for (int x = 0; x < cw; x++)
            {
                dr[2 * x] = ur[x];
                dr[2 * x + 1] = vr[x];
            }
        }
    }

    /// <summary>Mitad exacta: promedio de cada bloque de 2x2 (luma) y de 2x2 muestras de croma.</summary>
    private static void Halve(byte* s, int sw, int sh, bool nv12, byte* dst)
    {
        int dh = sh / 2;
        // En franjas, en paralelo (4K -> 1080p son 8 millones de píxeles por imagen).
        int bands = Math.Clamp(Environment.ProcessorCount / 2, 1, 4);
        IntPtr sp = (IntPtr)s, dp = (IntPtr)dst;
        Parallel.For(0, bands, band =>
        {
            int y0 = dh * band / bands, y1 = dh * (band + 1) / bands;
            HalveRows((byte*)sp, sw, sh, nv12, (byte*)dp, y0, y1);
        });
    }

    private static void HalveRows(byte* s, int sw, int sh, bool nv12, byte* dst, int y0, int y1)
    {
        int dw = sw / 2, dh = sh / 2;
        for (int y = y0; y < y1; y++)
        {
            byte* a = s + (long)(2 * y) * sw;
            byte* b = a + sw;
            byte* d = dst + (long)y * dw;
            for (int x = 0; x < dw; x++)
                d[x] = (byte)((a[2 * x] + a[2 * x + 1] + b[2 * x] + b[2 * x + 1] + 2) >> 2);
        }
        int cw = dw / 2;
        int c0 = (y0 + 1) / 2, c1 = (y1 + 1) / 2; // filas de croma de esta franja
        if (y1 == dh) c1 = dh / 2;
        byte* duv = dst + (long)dw * dh;
        if (nv12)
        {
            byte* uv = s + (long)sw * sh;
            for (int y = c0; y < c1; y++)
            {
                byte* a = uv + (long)(2 * y) * sw;
                byte* b = a + sw;
                byte* d = duv + (long)y * dw;
                for (int x = 0; x < cw; x++)
                {
                    int i = 4 * x;
                    d[2 * x] = (byte)((a[i] + a[i + 2] + b[i] + b[i + 2] + 2) >> 2);
                    d[2 * x + 1] = (byte)((a[i + 1] + a[i + 3] + b[i + 1] + b[i + 3] + 2) >> 2);
                }
            }
        }
        else
        {
            int scw = sw / 2, sch = sh / 2;
            byte* u = s + (long)sw * sh;
            byte* v = u + (long)scw * sch;
            for (int y = c0; y < c1; y++)
            {
                byte* ua = u + (long)(2 * y) * scw, ub = ua + scw;
                byte* va = v + (long)(2 * y) * scw, vb = va + scw;
                byte* d = duv + (long)y * dw;
                for (int x = 0; x < cw; x++)
                {
                    int i = 2 * x;
                    d[2 * x] = (byte)((ua[i] + ua[i + 1] + ub[i] + ub[i + 1] + 2) >> 2);
                    d[2 * x + 1] = (byte)((va[i] + va[i + 1] + vb[i] + vb[i + 1] + 2) >> 2);
                }
            }
        }
    }

    private bool TrySwscale(byte* s, int sw, int sh, bool nv12, byte* dst, int tw, int th)
    {
        try
        {
            if (!Decoding.VideoDecoder.EnsureFfmpeg()) { _swsFailed = true; return false; }
            var srcFmt = nv12 ? AVPixelFormat.AV_PIX_FMT_NV12 : AVPixelFormat.AV_PIX_FMT_YUV420P;
            int flags = (tw < sw || th < sh) ? ffmpeg.SWS_AREA : ffmpeg.SWS_LANCZOS;
            _sws = ffmpeg.sws_getCachedContext(_sws, sw, sh, srcFmt, tw, th, AVPixelFormat.AV_PIX_FMT_NV12, flags | ffmpeg.SWS_ACCURATE_RND, null, null, null);
            if (_sws == null) { _swsFailed = true; return false; }
            var srcData = new byte*[4];
            var srcStride = new int[4];
            srcData[0] = s;
            srcStride[0] = sw;
            if (nv12)
            {
                srcData[1] = s + (long)sw * sh;
                srcStride[1] = sw;
            }
            else
            {
                srcData[1] = s + (long)sw * sh;
                srcStride[1] = sw / 2;
                srcData[2] = srcData[1] + (long)(sw / 2) * (sh / 2);
                srcStride[2] = sw / 2;
            }
            var dstData = new byte*[4] { dst, dst + (long)tw * th, null, null };
            var dstStride = new int[4] { tw, tw, 0, 0 };
            ffmpeg.sws_scale(_sws, srcData, srcStride, 0, sh, dstData, dstStride);
            return true;
        }
        catch (Exception ex)
        {
            // Sin libswscale (p. ej. en un Linux sin el paquete): seguimos con el bilineal propio.
            _swsFailed = true;
            Log.Warn("Escalado de FFmpeg no disponible, se usa uno propio: " + ex.Message);
            return false;
        }
    }

    /// <summary>Bilineal en punto fijo para un plano de 1 (luma) o 2 (UV intercalado) componentes.</summary>
    private static void Bilinear(byte* src, int sw, int sh, int sstride, byte* dst, int dw, int dh, int dstride, int comps)
    {
        var x0 = new int[dw];
        var fx = new int[dw];
        for (int x = 0; x < dw; x++)
        {
            long p = ((2L * x + 1) * sw * 256) / (2L * dw) - 128;
            if (p < 0) p = 0;
            int i = (int)(p >> 8), f = (int)(p & 255);
            if (i >= sw - 1) { i = sw - 1; f = 0; }
            x0[x] = i;
            fx[x] = f;
        }
        for (int y = 0; y < dh; y++)
        {
            long p = ((2L * y + 1) * sh * 256) / (2L * dh) - 128;
            if (p < 0) p = 0;
            int j = (int)(p >> 8), fy = (int)(p & 255);
            if (j >= sh - 1) { j = sh - 1; fy = 0; }
            byte* r0 = src + (long)j * sstride;
            byte* r1 = j + 1 < sh ? r0 + sstride : r0;
            byte* d = dst + (long)y * dstride;
            for (int x = 0; x < dw; x++)
            {
                int i = x0[x], f = fx[x];
                int i1 = i + 1 < sw ? i + 1 : i;
                for (int c = 0; c < comps; c++)
                {
                    int a = r0[i * comps + c], b = r0[i1 * comps + c];
                    int e = r1[i * comps + c], g = r1[i1 * comps + c];
                    int top = a * (256 - f) + b * f;
                    int bot = e * (256 - f) + g * f;
                    d[x * comps + c] = (byte)((top * (256 - fy) + bot * fy + (1 << 15)) >> 16);
                }
            }
        }
    }

    public void Dispose()
    {
        if (_sws != null)
        {
            try { ffmpeg.sws_freeContext(_sws); } catch { }
            _sws = null;
        }
    }
}
