using CamaraNdi.Core.Ndi;

namespace CamaraNdi.Core;

/// <summary>
/// NDI y OBS esperan YUV "de video": rango limitado (Y 16–235, Cb/Cr 16–240) y matriz BT.709 en HD.
/// Muchos celulares Android codifican en rango completo (0–255) o con la matriz BT.601.
/// Si eso se pasa tal cual, todo lo que está por debajo de 16 se vuelve negro puro
/// (ropa negra, alfombra oscura → una mancha) y lo que pasa de 235 se quema.
/// Esta clase convierte en el lugar al formato correcto. No es un filtro: es la misma imagen
/// expresada en la escala que el resto de la cadena entiende.
/// </summary>
public static class ColorConverter
{
    public sealed class Tables
    {
        internal readonly int[] YY = new int[256];
        internal readonly int[] YCb = new int[256];
        internal readonly int[] YCr = new int[256];
        internal readonly int[] CbCb = new int[256];
        internal readonly int[] CbCr = new int[256];
        internal readonly int[] CrCb = new int[256];
        internal readonly int[] CrCr = new int[256];
        /// <summary>Atajo para BT.709: el cambio de rango es una tabla byte→byte por plano.</summary>
        internal readonly byte[] LumaLut = new byte[256];
        internal readonly byte[] ChromaLut = new byte[256];
        public bool FullRangeInput { get; }
        public bool Bt601Input { get; }

        // BT.601: tablas por par (Cb,Cr) → corrección de luma y croma nueva; y un recorte sin saltos.
        private int[]? _pairDy;
        private byte[]? _pairCb, _pairCr;
        internal readonly byte[] ClampLut = new byte[1024];

        internal void EnsurePairs()
        {
            if (_pairDy != null) return;
            var dy = new int[65536];
            var cbo = new byte[65536];
            var cro = new byte[65536];
            for (int cb = 0; cb < 256; cb++)
                for (int cr = 0; cr < 256; cr++)
                {
                    int i = (cb << 8) | cr;
                    dy[i] = YCb[cb] + YCr[cr] + 2048;
                    cbo[i] = (byte)Math.Clamp((CbCb[cb] + CbCr[cr] + 2048) >> 12, 0, 255);
                    cro[i] = (byte)Math.Clamp((CrCb[cb] + CrCr[cr] + 2048) >> 12, 0, 255);
                }
            _pairCb = cbo; _pairCr = cro; _pairDy = dy;
        }

        internal int[] PairDy => _pairDy!;
        internal byte[] PairCb => _pairCb!;
        internal byte[] PairCr => _pairCr!;

        internal Tables(bool fullRange, bool bt601)
        {
            FullRangeInput = fullRange;
            Bt601Input = bt601;
            // T = M709 · M_entrada⁻¹ (en el dominio Y'CbCr normalizado).
            var t = Multiply(Rgb2Yuv(0.2126, 0.0722), Yuv2Rgb(bt601 ? 0.299 : 0.2126, bt601 ? 0.114 : 0.0722));
            double yOff = fullRange ? 0 : 16, yScale = fullRange ? 255 : 219, cScale = fullRange ? 255 : 224;
            const double F = 4096;
            for (int v = 0; v < 256; v++)
            {
                double yn = (v - yOff) / yScale;
                double cn = (v - 128) / cScale;
                YY[v] = (int)Math.Round((16 + 219 * t[0, 0] * yn) * F);
                YCb[v] = (int)Math.Round(219 * t[0, 1] * cn * F);
                YCr[v] = (int)Math.Round(219 * t[0, 2] * cn * F);
                CbCb[v] = (int)Math.Round((128 + 224 * t[1, 1] * cn) * F);
                CbCr[v] = (int)Math.Round(224 * t[1, 2] * cn * F);
                CrCb[v] = (int)Math.Round(224 * t[2, 1] * cn * F);
                CrCr[v] = (int)Math.Round((128 + 224 * t[2, 2] * cn) * F);
                LumaLut[v] = (byte)Math.Clamp(Math.Round(16 + 219 * yn), 0, 255);
                ChromaLut[v] = (byte)Math.Clamp(Math.Round(128 + 224 * cn), 0, 255);
            }
            for (int i = 0; i < ClampLut.Length; i++) ClampLut[i] = (byte)Math.Clamp(i - 384, 0, 255);
        }
    }

    private static readonly Tables[] Cache =
    {
        new(false, false), new(false, true), new(true, false), new(true, true),
    };

    public static Tables For(bool fullRange, bool bt601) => Cache[(fullRange ? 2 : 0) + (bt601 ? 1 : 0)];

    /// <summary>¿Hace falta convertir? (Limitado + BT.709 ya es lo correcto.)</summary>
    public static bool Needed(bool fullRange, bool bt601) => fullRange || bt601;

    /// <summary>Convierte en el lugar un frame NV12 o I420 a rango limitado BT.709.</summary>
    public static unsafe void ToLimited709(VideoBuffer b, bool fullRange, bool bt601)
    {
        if (!Needed(fullRange, bt601)) return;
        var t = For(fullRange, bt601);
        int w = b.Width, h = b.Height;
        bool nv12 = b.FourCC == NdiLib.FourCC_NV12;
        int rows = h / 2;
        // El buffer está fijado en memoria (pinned), así que su dirección no cambia.
        IntPtr addr = System.Runtime.InteropServices.Marshal.UnsafeAddrOfPinnedArrayElement(b.Data, 0);
        int chunks = w * h >= 1280 * 720 ? Math.Clamp(Environment.ProcessorCount / 2, 1, 6) : 1;

        if (!bt601)
        {
            // Sólo rango (lo más común): tabla byte→byte sobre cada plano.
            int ySize = w * h, cSize = b.Length - ySize;
            ParallelChunks(chunks, ySize, (from, to) => MapBytes((byte*)addr + from, to - from, t.LumaLut));
            ParallelChunks(chunks, cSize, (from, to) => MapBytes((byte*)addr + ySize + from, to - from, t.ChromaLut));
            return;
        }

        t.EnsurePairs();
        ParallelChunks(chunks, rows, (from, to) =>
        {
            byte* p = (byte*)addr;
            fixed (int* yy = t.YY, dy = t.PairDy)
            fixed (byte* pcb = t.PairCb, pcr = t.PairCr, clamp = t.ClampLut)
            {
                var k = new Kernel { YY = yy, PairDy = dy, PairCb = pcb, PairCr = pcr, Clamp = clamp + 384 };
                for (int j = from; j < to; j++) ConvertChromaRow(p, k, w, h, j, nv12);
            }
        });
    }

    private unsafe struct Kernel
    {
        public int* YY, PairDy;
        public byte* PairCb, PairCr;
        /// <summary>Apunta al centro de la tabla: Clamp[v] = v recortado a 0..255 para v en -384..639.</summary>
        public byte* Clamp;
    }

    private static void ParallelChunks(int chunks, int count, Action<int, int> body)
    {
        if (chunks <= 1) { body(0, count); return; }
        int per = (count + chunks - 1) / chunks;
        Parallel.For(0, chunks, c =>
        {
            int from = c * per, to = Math.Min(count, from + per);
            if (from < to) body(from, to);
        });
    }

    private static unsafe void MapBytes(byte* p, int n, byte[] lut)
    {
        fixed (byte* l = lut)
        {
            byte* end = p + n;
            while (p + 4 <= end)
            {
                p[0] = l[p[0]]; p[1] = l[p[1]]; p[2] = l[p[2]]; p[3] = l[p[3]];
                p += 4;
            }
            while (p < end) { *p = l[*p]; p++; }
        }
    }

    private static unsafe void ConvertChromaRow(byte* p, Kernel t, int w, int h, int j, bool nv12)
    {
        byte* y0 = p + (long)(2 * j) * w;
        byte* y1 = y0 + w;
        int cw = w / 2;
        byte* cbp, crp;
        int step;
        if (nv12)
        {
            cbp = p + (long)w * h + (long)j * w;
            crp = cbp + 1;
            step = 2;
        }
        else
        {
            int ch = h / 2;
            cbp = p + (long)w * h + (long)j * cw;
            crp = p + (long)w * h + (long)cw * ch + (long)j * cw;
            step = 1;
        }
        for (int i = 0; i < cw; i++)
        {
            int idx = (cbp[i * step] << 8) | crp[i * step];
            int dy = t.PairDy[idx];
            cbp[i * step] = t.PairCb[idx];
            crp[i * step] = t.PairCr[idx];
            int x = 2 * i;
            y0[x] = t.Clamp[(t.YY[y0[x]] + dy) >> 12];
            y0[x + 1] = t.Clamp[(t.YY[y0[x + 1]] + dy) >> 12];
            y1[x] = t.Clamp[(t.YY[y1[x]] + dy) >> 12];
            y1[x + 1] = t.Clamp[(t.YY[y1[x + 1]] + dy) >> 12];
        }
    }

    private static byte Clamp(int v) => (byte)(v < 0 ? 0 : v > 255 ? 255 : v);

    // Matrices Y'CbCr normalizado (y 0..1, cb/cr -0.5..0.5) <-> R'G'B'.
    private static double[,] Rgb2Yuv(double kr, double kb)
    {
        double kg = 1 - kr - kb;
        return new[,]
        {
            { kr, kg, kb },
            { -kr / (2 * (1 - kb)), -kg / (2 * (1 - kb)), 0.5 },
            { 0.5, -kg / (2 * (1 - kr)), -kb / (2 * (1 - kr)) },
        };
    }

    private static double[,] Yuv2Rgb(double kr, double kb)
    {
        double kg = 1 - kr - kb;
        return new[,]
        {
            { 1, 0, 2 * (1 - kr) },
            { 1, -2 * kb * (1 - kb) / kg, -2 * kr * (1 - kr) / kg },
            { 1, 2 * (1 - kb), 0 },
        };
    }

    private static double[,] Multiply(double[,] a, double[,] b)
    {
        var r = new double[3, 3];
        for (int i = 0; i < 3; i++)
            for (int j = 0; j < 3; j++)
                for (int k = 0; k < 3; k++)
                    r[i, j] += a[i, k] * b[k, j];
        return r;
    }
}

/// <summary>
/// Algunos celulares escriben rango completo (0–255) sin decirlo en el video.
/// Si aparecen bastantes valores por debajo de 8 o por encima de 247 (imposibles en rango
/// limitado bien hecho), el video es de rango completo. La decisión es "pegajosa".
/// </summary>
public sealed class RangeDetector
{
    private readonly bool[] _window = new bool[8];
    private int _idx;
    private int _frames;

    public bool LooksFull { get; private set; }

    public void Reset()
    {
        Array.Clear(_window);
        _idx = 0;
        _frames = 0;
        LooksFull = false;
    }

    public void Feed(VideoBuffer b)
    {
        if (LooksFull) return;
        if (_frames++ % 10 != 0) return; // basta con mirar 3 frames por segundo
        int n = b.Width * b.Height;
        long total = 0, outside = 0;
        var d = b.Data;
        for (int i = 0; i < n; i += 7)
        {
            int v = d[i];
            if (v < 8 || v > 247) outside++;
            total++;
        }
        _window[_idx] = total > 0 && outside * 1000 >= total; // ≥ 0,1 %
        _idx = (_idx + 1) % _window.Length;
        int hits = 0;
        foreach (var w in _window) if (w) hits++;
        if (hits >= 3)
        {
            LooksFull = true;
            Log.Info("Se detectó video en rango completo (0–255) sin señalizar: se convierte a 16–235");
        }
    }
}
