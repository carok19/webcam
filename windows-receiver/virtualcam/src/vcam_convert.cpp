#include "vcam_convert.h"

#include <algorithm>
#include <cstring>

namespace vcam {

namespace {

inline uint8_t Clamp255(int v) { return static_cast<uint8_t>(v < 0 ? 0 : (v > 255 ? 255 : v)); }

// Reducción exacta a la mitad (p. ej. 3840x2160 -> 1920x1080): promedio de 2x2, la de mejor calidad.
void Half(const uint8_t* src, int sw, int sh, uint8_t* dst) {
    const int dw = sw / 2, dh = sh / 2;
    for (int y = 0; y < dh; y++) {
        const uint8_t* a = src + static_cast<size_t>(2 * y) * sw;
        const uint8_t* b = a + sw;
        uint8_t* d = dst + static_cast<size_t>(y) * dw;
        for (int x = 0; x < dw; x++) d[x] = static_cast<uint8_t>((a[2 * x] + a[2 * x + 1] + b[2 * x] + b[2 * x + 1] + 2) >> 2);
    }
    // Croma intercalado (U,V): promedio de 2x2 pares.
    const uint8_t* uv = src + static_cast<size_t>(sw) * sh;
    uint8_t* duv = dst + static_cast<size_t>(dw) * dh;
    const int cw = dw / 2, ch = dh / 2;
    for (int y = 0; y < ch; y++) {
        const uint8_t* a = uv + static_cast<size_t>(2 * y) * sw;
        const uint8_t* b = a + sw;
        uint8_t* d = duv + static_cast<size_t>(y) * dw;
        for (int x = 0; x < cw; x++) {
            const int i = 4 * x;
            d[2 * x] = static_cast<uint8_t>((a[i] + a[i + 2] + b[i] + b[i + 2] + 2) >> 2);
            d[2 * x + 1] = static_cast<uint8_t>((a[i + 1] + a[i + 3] + b[i + 1] + b[i + 3] + 2) >> 2);
        }
    }
}

// Bilineal en punto fijo para un plano de "comps" componentes intercalados (1 = luma, 2 = UV).
void Bilinear(const uint8_t* src, int sw, int sh, int sstride, uint8_t* dst, int dw, int dh, int dstride, int comps) {
    std::vector<int> x0(static_cast<size_t>(dw)), fx(static_cast<size_t>(dw));
    for (int x = 0; x < dw; x++) {
        // Centro de píxel: (x + 0.5) * sw / dw - 0.5, en 1/256.
        long long p = ((2LL * x + 1) * sw * 256) / (2LL * dw) - 128;
        if (p < 0) p = 0;
        int i = static_cast<int>(p >> 8);
        int f = static_cast<int>(p & 255);
        if (i >= sw - 1) { i = sw - 1; f = 0; }
        x0[static_cast<size_t>(x)] = i;
        fx[static_cast<size_t>(x)] = f;
    }
    for (int y = 0; y < dh; y++) {
        long long p = ((2LL * y + 1) * sh * 256) / (2LL * dh) - 128;
        if (p < 0) p = 0;
        int j = static_cast<int>(p >> 8);
        int fy = static_cast<int>(p & 255);
        if (j >= sh - 1) { j = sh - 1; fy = 0; }
        const uint8_t* r0 = src + static_cast<size_t>(j) * sstride;
        const uint8_t* r1 = j + 1 < sh ? r0 + sstride : r0;
        uint8_t* d = dst + static_cast<size_t>(y) * dstride;
        for (int x = 0; x < dw; x++) {
            const int i = x0[static_cast<size_t>(x)];
            const int f = fx[static_cast<size_t>(x)];
            const int i1 = i + 1 < sw ? i + 1 : i;
            for (int c = 0; c < comps; c++) {
                const int a = r0[i * comps + c], b = r0[i1 * comps + c];
                const int e = r1[i * comps + c], g = r1[i1 * comps + c];
                const int top = a * (256 - f) + b * f;
                const int bot = e * (256 - f) + g * f;
                d[x * comps + c] = static_cast<uint8_t>((top * (256 - fy) + bot * fy + (1 << 15)) >> 16);
            }
        }
    }
}

}  // namespace

void ScaleNV12(const uint8_t* src, int sw, int sh, uint8_t* dst, int dw, int dh) {
    if (sw == dw && sh == dh) {
        std::memcpy(dst, src, static_cast<size_t>(sw) * sh * 3 / 2);
        return;
    }
    if (sw == 2 * dw && sh == 2 * dh && (dw % 2) == 0 && (dh % 2) == 0) {
        Half(src, sw, sh, dst);
        return;
    }
    Bilinear(src, sw, sh, sw, dst, dw, dh, dw, 1);
    Bilinear(src + static_cast<size_t>(sw) * sh, sw / 2, sh / 2, sw, dst + static_cast<size_t>(dw) * dh, dw / 2, dh / 2, dw, 2);
}

void CopyNV12(const uint8_t* src, int w, int h, uint8_t* dst, int dstStride) {
    if (dstStride == w) {
        std::memcpy(dst, src, static_cast<size_t>(w) * h * 3 / 2);
        return;
    }
    for (int y = 0; y < h; y++) std::memcpy(dst + static_cast<size_t>(y) * dstStride, src + static_cast<size_t>(y) * w, static_cast<size_t>(w));
    const uint8_t* uv = src + static_cast<size_t>(w) * h;
    uint8_t* duv = dst + static_cast<size_t>(dstStride) * h;
    for (int y = 0; y < h / 2; y++)
        std::memcpy(duv + static_cast<size_t>(y) * dstStride, uv + static_cast<size_t>(y) * w, static_cast<size_t>(w));
}

void NV12ToYUY2(const uint8_t* src, int w, int h, uint8_t* dst, int dstStride) {
    const uint8_t* uvPlane = src + static_cast<size_t>(w) * h;
    for (int y = 0; y < h; y++) {
        const uint8_t* yr = src + static_cast<size_t>(y) * w;
        const uint8_t* uv = uvPlane + static_cast<size_t>(y / 2) * w;
        uint8_t* d = dst + static_cast<size_t>(y) * dstStride;
        for (int x = 0; x < w; x += 2) {
            d[0] = yr[x];
            d[1] = uv[x];
            d[2] = yr[x + 1];
            d[3] = uv[x + 1];
            d += 4;
        }
    }
}

void NV12ToRGB32(const uint8_t* src, int w, int h, uint8_t* dst, int dstStride, bool bottomUp) {
    // BT.709, rango limitado -> RGB completo. Coeficientes x 65536.
    const uint8_t* uvPlane = src + static_cast<size_t>(w) * h;
    for (int y = 0; y < h; y++) {
        const uint8_t* yr = src + static_cast<size_t>(y) * w;
        const uint8_t* uv = uvPlane + static_cast<size_t>(y / 2) * w;
        uint8_t* d = dst + static_cast<size_t>(bottomUp ? (h - 1 - y) : y) * dstStride;
        for (int x = 0; x < w; x++) {
            const int c = 76309 * (yr[x] - 16);
            const int u = uv[x & ~1] - 128;
            const int v = uv[(x & ~1) + 1] - 128;
            d[4 * x + 0] = Clamp255((c + 138438 * u + 32768) >> 16);                // B
            d[4 * x + 1] = Clamp255((c - 13975 * u - 34925 * v + 32768) >> 16);     // G
            d[4 * x + 2] = Clamp255((c + 117489 * v + 32768) >> 16);                // R
            d[4 * x + 3] = 255;
        }
    }
}

void BGRAToNV12(const uint8_t* bgra, int w, int h, int stride, uint8_t* dst) {
    for (int y = 0; y < h; y++) {
        const uint8_t* s = bgra + static_cast<size_t>(y) * stride;
        uint8_t* d = dst + static_cast<size_t>(y) * w;
        for (int x = 0; x < w; x++) {
            const int b = s[4 * x], g = s[4 * x + 1], r = s[4 * x + 2];
            d[x] = Clamp255(16 + ((11966 * r + 40254 * g + 4064 * b + 32768) >> 16));
        }
    }
    uint8_t* uvPlane = dst + static_cast<size_t>(w) * h;
    for (int y = 0; y < h / 2; y++) {
        const uint8_t* s0 = bgra + static_cast<size_t>(2 * y) * stride;
        const uint8_t* s1 = s0 + stride;
        uint8_t* d = uvPlane + static_cast<size_t>(y) * w;
        for (int x = 0; x < w / 2; x++) {
            int r = 0, g = 0, b = 0;
            for (int k = 0; k < 2; k++) {
                const uint8_t* p0 = s0 + 4 * (2 * x + k);
                const uint8_t* p1 = s1 + 4 * (2 * x + k);
                b += p0[0] + p1[0];
                g += p0[1] + p1[1];
                r += p0[2] + p1[2];
            }
            r = (r + 2) / 4;
            g = (g + 2) / 4;
            b = (b + 2) / 4;
            d[2 * x] = Clamp255(128 + ((-6597 * r - 22187 * g + 28784 * b + 32768) >> 16));
            d[2 * x + 1] = Clamp255(128 + ((28784 * r - 26148 * g - 2637 * b + 32768) >> 16));
        }
    }
}

}  // namespace vcam
