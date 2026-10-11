using CamaraNdi.Core.Ndi;

namespace CamaraNdi.Core;

/// <summary>Convierte un frame NV12/I420 a una miniatura BGRA pequeña (muestreo simple, BT.709).</summary>
public static class Thumbnail
{
    public static void ToBgra(VideoBuffer b, byte[] dst, int tw, int th)
    {
        int w = b.Width, h = b.Height;
        var src = b.Data;
        int ySize = w * h;
        bool nv12 = b.FourCC == NdiLib.FourCC_NV12;
        int cw = w / 2, ch = h / 2;
        for (int ty = 0; ty < th; ty++)
        {
            int sy = (int)((long)ty * h / th);
            int cy = sy / 2;
            for (int tx = 0; tx < tw; tx++)
            {
                int sx = (int)((long)tx * w / tw);
                int cx = sx / 2;
                int Y = src[sy * w + sx];
                int U, V;
                if (nv12)
                {
                    int o = ySize + cy * w + cx * 2;
                    U = src[o]; V = src[o + 1];
                }
                else
                {
                    U = src[ySize + cy * cw + cx];
                    V = src[ySize + cw * ch + cy * cw + cx];
                }
                int c = (Y - 16) * 298;
                int d = U - 128, e = V - 128;
                int r = (c + 459 * e + 128) >> 8;
                int g = (c - 55 * d - 136 * e + 128) >> 8;
                int bl = (c + 541 * d + 128) >> 8;
                int o2 = (ty * tw + tx) * 4;
                dst[o2] = Clamp(bl);
                dst[o2 + 1] = Clamp(g);
                dst[o2 + 2] = Clamp(r);
                dst[o2 + 3] = 255;
            }
        }
    }

    private static byte Clamp(int v) => (byte)(v < 0 ? 0 : v > 255 ? 255 : v);
}
