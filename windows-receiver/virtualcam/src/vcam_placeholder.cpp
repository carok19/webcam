#include "vcam_placeholder.h"

#include <windows.h>

#include <algorithm>

#include "vcam_convert.h"

namespace vcam {

namespace {

void DrawLine(HDC dc, const std::wstring& text, int height, COLORREF color, int weight, RECT rc) {
    HFONT font = CreateFontW(-height, 0, 0, 0, weight, FALSE, FALSE, FALSE, DEFAULT_CHARSET, OUT_DEFAULT_PRECIS,
                             CLIP_DEFAULT_PRECIS, ANTIALIASED_QUALITY, DEFAULT_PITCH | FF_SWISS, L"Segoe UI");
    HGDIOBJ old = SelectObject(dc, font);
    SetTextColor(dc, color);
    DrawTextW(dc, text.c_str(), static_cast<int>(text.size()), &rc, DT_CENTER | DT_VCENTER | DT_SINGLELINE | DT_NOPREFIX | DT_END_ELLIPSIS);
    SelectObject(dc, old);
    DeleteObject(font);
}

}  // namespace

std::vector<uint8_t> RenderPlaceholder(int width, int height, const std::wstring& title, const std::wstring& subtitle) {
    std::vector<uint8_t> nv12(static_cast<size_t>(width) * height * 3 / 2);
    // Gris muy oscuro (Y=32, croma neutro) por si GDI falla.
    std::fill(nv12.begin(), nv12.begin() + static_cast<ptrdiff_t>(width) * height, static_cast<uint8_t>(32));
    std::fill(nv12.begin() + static_cast<ptrdiff_t>(width) * height, nv12.end(), static_cast<uint8_t>(128));

    BITMAPINFO bi = {};
    bi.bmiHeader.biSize = sizeof(BITMAPINFOHEADER);
    bi.bmiHeader.biWidth = width;
    bi.bmiHeader.biHeight = -height;  // de arriba hacia abajo
    bi.bmiHeader.biPlanes = 1;
    bi.bmiHeader.biBitCount = 32;
    bi.bmiHeader.biCompression = BI_RGB;
    void* bits = nullptr;
    HDC dc = CreateCompatibleDC(nullptr);
    if (!dc) return nv12;
    HBITMAP bmp = CreateDIBSection(dc, &bi, DIB_RGB_COLORS, &bits, nullptr, 0);
    if (!bmp || !bits) {
        DeleteDC(dc);
        return nv12;
    }
    HGDIOBJ oldBmp = SelectObject(dc, bmp);

    RECT all = {0, 0, width, height};
    HBRUSH bg = CreateSolidBrush(RGB(22, 24, 28));
    FillRect(dc, &all, bg);
    DeleteObject(bg);

    // Un marco tenue, para que en OBS se note que es esta cámara aunque el texto quede chico.
    HBRUSH frame = CreateSolidBrush(RGB(60, 66, 76));
    const int border = (height / 180) > 1 ? height / 180 : 2;
    RECT r1 = {0, 0, width, border}, r2 = {0, height - border, width, height};
    RECT r3 = {0, 0, border, height}, r4 = {width - border, 0, width, height};
    FillRect(dc, &r1, frame);
    FillRect(dc, &r2, frame);
    FillRect(dc, &r3, frame);
    FillRect(dc, &r4, frame);
    DeleteObject(frame);

    SetBkMode(dc, TRANSPARENT);
    const int titleH = height / 14;
    const int subH = height / 30;
    RECT tr = {width / 20, height / 2 - titleH - subH / 2, width - width / 20, height / 2 - subH / 2};
    RECT sr = {width / 20, height / 2 + subH / 2, width - width / 20, height / 2 + subH / 2 + subH * 2};
    DrawLine(dc, title, titleH, RGB(232, 234, 237), FW_SEMIBOLD, tr);
    DrawLine(dc, subtitle, subH, RGB(154, 160, 166), FW_NORMAL, sr);
    GdiFlush();

    BGRAToNV12(static_cast<const uint8_t*>(bits), width, height, width * 4, nv12.data());

    SelectObject(dc, oldBmp);
    DeleteObject(bmp);
    DeleteDC(dc);
    return nv12;
}

}  // namespace vcam
