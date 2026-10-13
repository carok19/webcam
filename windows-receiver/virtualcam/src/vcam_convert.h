// Conversiones de imagen dentro de la cámara virtual. Todo en NV12 de rango limitado BT.709 (como lo
// manda el receptor); sólo al final se pasa al formato que pidió el programa (NV12, YUY2 o RGB32).
#pragma once

#include <cstdint>
#include <vector>

namespace vcam {

// Escala NV12 (planos contiguos, stride = ancho). Si el receptor ya mandó el tamaño pedido no se usa:
// es el respaldo mientras el receptor se entera del formato elegido.
void ScaleNV12(const uint8_t* src, int sw, int sh, uint8_t* dst, int dw, int dh);

// Copia NV12 a un búfer con otro stride (DirectShow puede pedir filas más anchas).
void CopyNV12(const uint8_t* src, int w, int h, uint8_t* dst, int dstStride);

// NV12 -> YUY2 (4:2:2 empaquetado). dstStride en bytes.
void NV12ToYUY2(const uint8_t* src, int w, int h, uint8_t* dst, int dstStride);

// NV12 (BT.709 limitado) -> BGRA de 32 bits. bottomUp: el RGB de DirectShow va de abajo hacia arriba.
void NV12ToRGB32(const uint8_t* src, int w, int h, uint8_t* dst, int dstStride, bool bottomUp);

// BGRA (de arriba hacia abajo) -> NV12 BT.709 limitado. Para el cartel de espera.
void BGRAToNV12(const uint8_t* bgra, int w, int h, int stride, uint8_t* dst);

}  // namespace vcam
