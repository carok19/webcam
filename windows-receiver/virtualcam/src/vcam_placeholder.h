// Cartel que muestra la cámara virtual cuando no hay video (receptor cerrado, sin celular, etc.).
#pragma once

#include <cstdint>
#include <string>
#include <vector>

namespace vcam {

// Fondo oscuro con un título y una línea de ayuda, en NV12 (BT.709 limitado) del tamaño pedido.
std::vector<uint8_t> RenderPlaceholder(int width, int height, const std::wstring& title, const std::wstring& subtitle);

}  // namespace vcam
