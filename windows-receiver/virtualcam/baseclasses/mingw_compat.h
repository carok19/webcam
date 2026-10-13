// Anotaciones SAL antiguas que usan las BaseClasses de DirectShow y que mingw-w64 no define.
#pragma once
#if defined(__MINGW32__) && defined(__cplusplus)
// Primero la biblioteca estándar: usa nombres como __in/__out que las macros de abajo romperían.
#include <algorithm>
#include <atomic>
#include <chrono>
#include <cmath>
#include <cstdint>
#include <cstdio>
#include <cstring>
#include <functional>
#include <limits>
#include <memory>
#include <mutex>
#include <new>
#include <string>
#include <thread>
#include <type_traits>
#include <utility>
#include <vector>
#endif
#if defined(__MINGW32__)
#include <sal.h>
#define SALC_EMPTY(name) 
#ifndef __in
#define __in
#endif
#ifndef __in_opt
#define __in_opt
#endif
#ifndef __inout
#define __inout
#endif
#ifndef __inout_opt
#define __inout_opt
#endif
#ifndef __out
#define __out
#endif
#ifndef __out_opt
#define __out_opt
#endif
#ifndef __deref_out
#define __deref_out
#endif
#ifndef __deref_out_opt
#define __deref_out_opt
#endif
#ifndef __deref_in
#define __deref_in
#endif
#ifndef __deref_inout_opt
#define __deref_inout_opt
#endif
#ifndef __format_string
#define __format_string
#endif
#ifndef __control_entrypoint
#define __control_entrypoint(...)
#endif
#ifndef __in_bcount
#define __in_bcount(...)
#endif
#ifndef __in_bcount_opt
#define __in_bcount_opt(...)
#endif
#ifndef __out_bcount
#define __out_bcount(...)
#endif
#ifndef __in_ecount
#define __in_ecount(...)
#endif
#ifndef __out_ecount
#define __out_ecount(...)
#endif
#ifndef __out_ecount_part
#define __out_ecount_part(...)
#endif
#ifndef __inout_ecount_full
#define __inout_ecount_full(...)
#endif
#ifndef __field_ecount_opt
#define __field_ecount_opt(...)
#endif
#ifndef __out_range
#define __out_range(...)
#endif
#ifndef __deref_out_range
#define __deref_out_range(...)
#endif
#ifndef __success
#define __success(...)
#endif
#endif
#if defined(__MINGW32__)
#ifndef AM_NOVTABLE
#define AM_NOVTABLE
#endif
#endif
#if defined(__MINGW32__) && defined(__cplusplus)
#include <algorithm>
using std::min;
using std::max;
#include <windows.h>
#include <strmif.h>
#include <amvideo.h>
// Macros de amvideo.h del SDK de Windows que mingw-w64 no trae.
#ifndef WIDTHBYTES
#define WIDTHBYTES(bits) ((DWORD)(((bits)+31) & (~31)) / 8)
#endif
#ifndef DIBWIDTHBYTES
#define DIBWIDTHBYTES(bi) (DWORD)WIDTHBYTES((DWORD)(bi).biWidth * (DWORD)(bi).biBitCount)
#endif
#ifndef _DIBSIZE
#define _DIBSIZE(bi) (DIBWIDTHBYTES(bi) * (DWORD)(bi).biHeight)
#endif
#ifndef DIBSIZE
#define DIBSIZE(bi) ((bi).biHeight < 0 ? (-1)*(_DIBSIZE(bi)) : _DIBSIZE(bi))
#endif
#ifndef BIT_MASKS_MATCH
#define BIT_MASKS_MATCH(pbmi1,pbmi2) (((pbmi1)->dwBitMasks[iRED] == (pbmi2)->dwBitMasks[iRED]) && ((pbmi1)->dwBitMasks[iGREEN] == (pbmi2)->dwBitMasks[iGREEN]) && ((pbmi1)->dwBitMasks[iBLUE] == (pbmi2)->dwBitMasks[iBLUE]))
#endif
#ifndef RESET_MASKS
#define RESET_MASKS(pbmi) (ZeroMemory((PVOID)(pbmi)->dwBitFields,SIZE_MASKS))
#endif
#ifndef RESET_HEADER
#define RESET_HEADER(pbmi) (ZeroMemory((PVOID)(pbmi),SIZE_VIDEOHEADER))
#endif
#ifndef RESET_PALETTE
#define RESET_PALETTE(pbmi) (ZeroMemory((PVOID)(pbmi)->bmiColors,SIZE_PALETTE));
#endif
#ifndef PALETTISED
#define PALETTISED(pbmi) ((pbmi)->bmiHeader.biBitCount <= iPALETTE)
#endif
#ifndef PALETTE_ENTRIES
#define PALETTE_ENTRIES(pbmi) ((DWORD) 1 << (pbmi)->bmiHeader.biBitCount)
#endif
#ifndef HEADER
#define HEADER(pVideoInfo) (&(((VIDEOINFOHEADER *) (pVideoInfo))->bmiHeader))
#endif
#ifndef BITMASKS
#define BITMASKS(pbmi) ((DWORD *)((LPBYTE)(pbmi) + (pbmi)->bmiHeader.biSize))
#endif
#ifndef COLORS
#define COLORS(pbmi) ((RGBQUAD *)((LPBYTE)(pbmi) + (pbmi)->bmiHeader.biSize))
#endif
#ifndef TRUECOLOR
#define TRUECOLOR(pbmi) ((TRUECOLORINFO *)((LPBYTE)(pbmi) + (pbmi)->bmiHeader.biSize))
#endif
#ifndef SIZE_EGA_PALETTE
#define SIZE_EGA_PALETTE (iEGA_COLORS * sizeof(RGBQUAD))
#endif
#ifndef SIZE_PALETTE
#define SIZE_PALETTE (iPALETTE_COLORS * sizeof(RGBQUAD))
#endif
#ifndef SIZE_MASKS
#define SIZE_MASKS (iMASK_COLORS * sizeof(DWORD))
#endif
#ifndef SIZE_PREHEADER
#define SIZE_PREHEADER (FIELD_OFFSET(VIDEOINFOHEADER,bmiHeader))
#endif
#ifndef SIZE_VIDEOHEADER
#define SIZE_VIDEOHEADER (sizeof(BITMAPINFOHEADER) + SIZE_PREHEADER)
#endif
#endif
