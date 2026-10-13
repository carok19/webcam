// Acceso a la memoria compartida desde la cámara virtual (lado lector).
#pragma once

#include <windows.h>
#include <cstdint>
#include <string>
#include <vector>
#include "vcam_shared.h"

namespace vcam {

// Lo que la cámara sabe del receptor para decidir qué mostrar.
struct SourceStatus {
    bool receiverAlive = false;   // el receptor está abierto
    uint32_t state = VCAM_STATE_EMPTY;
    std::wstring name;            // nombre del celular asignado a esta cámara
};

class SharedReader {
public:
    explicit SharedReader(int cameraIndex);  // 1..VCAM_COUNT
    ~SharedReader();
    SharedReader(const SharedReader&) = delete;
    SharedReader& operator=(const SharedReader&) = delete;

    // Intenta (re)abrir lo que falte; barato si ya está abierto. Llamar en cada imagen.
    void Poll(uint64_t nowMs);

    SourceStatus Status(uint64_t nowMs) const;

    // Copia la imagen más nueva si hay una distinta a lastSeq. Devuelve true y actualiza
    // nv12/width/height/lastSeq si la copia salió entera (sin que el receptor la pisara a la mitad).
    bool ReadLatest(std::vector<uint8_t>& nv12, uint32_t& width, uint32_t& height, uint64_t& lastSeq);

    // Avisa al receptor qué formato pidió el programa (para que escale con buena calidad) y que alguien mira.
    void PublishWant(uint32_t width, uint32_t height, uint32_t fps100, uint64_t nowMs);
    void ClearWant();

    bool HasFrames() const { return m_header != nullptr; }

private:
    int m_index;
    HANDLE m_controlMap = nullptr;
    const VCamControl* m_control = nullptr;
    HANDLE m_frameMap = nullptr;
    VCamHeader* m_header = nullptr;
    uint8_t* m_data = nullptr;
    bool m_writable = false;
    uint64_t m_lastOpenTryMs = 0;
};

// Crea el descriptor de seguridad permisivo que usan los dos lados (así un OBS abierto como
// administrador y un receptor normal pueden compartir la memoria).
bool MakeSharedSecurity(SECURITY_ATTRIBUTES& sa, PSECURITY_DESCRIPTOR& sd);

std::wstring ControlName();
std::wstring FrameName(int cameraIndex);

}  // namespace vcam
