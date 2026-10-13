#include "vcam_shm.h"

#include <sddl.h>
#include <cstring>
#include <utility>

namespace vcam {

namespace {
constexpr uint64_t kRetryMs = 1000;
constexpr uint64_t kAliveMs = 3000;
}  // namespace

std::wstring ControlName() { return L"Local\\CamaraNDI_VCam_Control"; }

std::wstring FrameName(int cameraIndex) { return L"Local\\CamaraNDI_VCam_" + std::to_wstring(cameraIndex); }

bool MakeSharedSecurity(SECURITY_ATTRIBUTES& sa, PSECURITY_DESCRIPTOR& sd) {
    // Igual que el receptor (SharedRegion.cs): sólo este usuario (con o sin administrador), el sistema y los
    // administradores; integridad media.
    std::wstring user = L"OW";
    HANDLE token = nullptr;
    if (OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, &token)) {
        BYTE buf[256];
        DWORD len = 0;
        if (GetTokenInformation(token, TokenUser, buf, sizeof(buf), &len)) {
            LPWSTR s = nullptr;
            if (ConvertSidToStringSidW(reinterpret_cast<TOKEN_USER*>(buf)->User.Sid, &s)) {
                user = s;
                LocalFree(s);
            }
        }
        CloseHandle(token);
    }
    const std::wstring sddl = L"D:(A;;GA;;;" + user + L")(A;;GA;;;SY)(A;;GA;;;BA)S:(ML;;NW;;;ME)";
    sd = nullptr;
    if (!ConvertStringSecurityDescriptorToSecurityDescriptorW(sddl.c_str(), SDDL_REVISION_1, &sd, nullptr)) return false;
    sa.nLength = sizeof(sa);
    sa.lpSecurityDescriptor = sd;
    sa.bInheritHandle = FALSE;
    return true;
}

namespace {
volatile LONG g_readerCounter = 0;
}

SharedReader::SharedReader(int cameraIndex) : m_index(cameraIndex) {
    m_token = (GetCurrentProcessId() << 8) ^ static_cast<uint32_t>(InterlockedIncrement(&g_readerCounter));
    if (m_token == 0) m_token = 1;
}

SharedReader::~SharedReader() {
    ClearWant();
    if (m_header) UnmapViewOfFile(m_header);
    if (m_frameMap) CloseHandle(m_frameMap);
    if (m_control) UnmapViewOfFile(const_cast<VCamControl*>(m_control));
    if (m_controlMap) CloseHandle(m_controlMap);
}

void SharedReader::Poll(uint64_t nowMs) {
    if ((m_control && m_header) || nowMs - m_lastOpenTryMs < kRetryMs) return;
    m_lastOpenTryMs = nowMs;
    if (!m_control) {
        HANDLE h = OpenFileMappingW(FILE_MAP_READ, FALSE, ControlName().c_str());
        if (h) {
            void* p = MapViewOfFile(h, FILE_MAP_READ, 0, 0, VCAM_CONTROL_BYTES);
            if (p) {
                m_controlMap = h;
                m_control = static_cast<const VCamControl*>(p);
            } else {
                CloseHandle(h);
            }
        }
    }
    if (!m_header) {
        // Con escritura para avisar el formato pedido; si no se puede, sólo lectura.
        bool writable = true;
        HANDLE h = OpenFileMappingW(FILE_MAP_READ | FILE_MAP_WRITE, FALSE, FrameName(m_index).c_str());
        if (!h) {
            writable = false;
            h = OpenFileMappingW(FILE_MAP_READ, FALSE, FrameName(m_index).c_str());
        }
        if (h) {
            void* p = MapViewOfFile(h, writable ? (FILE_MAP_READ | FILE_MAP_WRITE) : FILE_MAP_READ, 0, 0, VCAM_TOTAL_BYTES);
            if (p) {
                m_frameMap = h;
                m_header = static_cast<VCamHeader*>(p);
                m_data = static_cast<uint8_t*>(p) + VCAM_HEADER_BYTES;
                m_writable = writable;  // si es de sólo lectura no se avisa el formato: la cámara escala sola
            } else {
                CloseHandle(h);
            }
        }
    }
}

SourceStatus SharedReader::Status(uint64_t nowMs) const {
    SourceStatus s;
    if (!m_control || m_control->magic != VCAM_CONTROL_MAGIC || m_control->version != VCAM_VERSION) return s;
    MemoryBarrier();
    const int64_t hb = m_control->receiverHeartbeatMs;
    s.receiverAlive = hb > 0 && nowMs >= static_cast<uint64_t>(hb) && nowMs - static_cast<uint64_t>(hb) < kAliveMs;
    if (m_index >= 1 && static_cast<uint32_t>(m_index) <= m_control->cameraCount && m_index <= static_cast<int>(VCAM_COUNT)) {
        const VCamControlCamera& c = m_control->cameras[m_index - 1];
        s.state = c.state;
        wchar_t buf[VCAM_NAME_CHARS + 1];
        for (uint32_t i = 0; i < VCAM_NAME_CHARS; i++) buf[i] = static_cast<wchar_t>(c.name[i]);
        buf[VCAM_NAME_CHARS] = 0;
        s.name = buf;
    }
    return s;
}

bool SharedReader::ReadLatest(std::vector<uint8_t>& nv12, uint32_t& width, uint32_t& height, uint64_t& lastSeq) {
    if (!m_header || m_header->magic != VCAM_FRAME_MAGIC || m_header->version != VCAM_VERSION) return false;
    MemoryBarrier();
    const uint64_t seq = m_header->latestSeq;
    MemoryBarrier();
    if (seq == 0 || seq == lastSeq) return false;
    const uint32_t slotIndex = m_header->latestSlot;
    if (slotIndex >= VCAM_SLOTS) return false;
    const VCamSlot& slot = m_header->slots[slotIndex];
    const uint64_t s1 = slot.seq;
    MemoryBarrier();
    if (s1 != seq) return false;  // el receptor ya escribe otra: la tomamos en la próxima vuelta
    const uint32_t w = slot.width, h = slot.height;
    if (w == 0 || h == 0 || w > VCAM_MAX_WIDTH || h > VCAM_MAX_HEIGHT || (w & 1u) || (h & 1u) || slot.format != VCAM_FORMAT_NV12)
        return false;
    const size_t bytes = static_cast<size_t>(w) * h * 3 / 2;
    static thread_local std::vector<uint8_t> scratch;
    scratch.resize(bytes);
    std::memcpy(scratch.data(), m_data + static_cast<size_t>(slotIndex) * VCAM_SLOT_BYTES, bytes);
    MemoryBarrier();
    if (slot.seq != s1) return false;  // la pisaron mientras copiábamos: se descarta
    std::swap(nv12, scratch);
    width = w;
    height = h;
    lastSeq = seq;
    return true;
}

void SharedReader::PublishWant(uint32_t width, uint32_t height, uint32_t fps100, uint64_t nowMs) {
    if (!m_header || !m_writable) return;
    // Si otro programa (u otra fuente de OBS) mira la misma cámara y pidió un tamaño MÁS GRANDE, se le
    // respeta: el receptor manda ese y este lector lo reduce por su cuenta (reducir es barato y no pierde
    // nitidez; si se alternaran los pedidos, la imagen cambiaría de tamaño en cada cuadro).
    const uint32_t owner = m_header->readerPid;
    const int64_t hb = m_header->readerHeartbeatMs;
    const bool otherFresh = owner != m_token && hb > 0 && static_cast<uint64_t>(hb) <= nowMs + 100 &&
                            nowMs - static_cast<uint64_t>(hb) < 1500;
    if (otherFresh) {
        const uint64_t cur = static_cast<uint64_t>(m_header->wantWidth) * m_header->wantHeight;
        if (cur > static_cast<uint64_t>(width) * height) return;
    }
    // Ancho y alto juntos en una sola escritura de 64 bits: el receptor nunca ve una mezcla de dos pedidos.
    const uint64_t packed = static_cast<uint64_t>(width) | (static_cast<uint64_t>(height) << 32);
    InterlockedExchange64(reinterpret_cast<volatile LONG64*>(&m_header->wantWidth), static_cast<LONG64>(packed));
    m_header->wantFps100 = fps100;
    m_header->readerPid = m_token;
    MemoryBarrier();
    m_header->readerHeartbeatMs = static_cast<int64_t>(nowMs);
}

void SharedReader::ClearWant() {
    if (!m_header || !m_writable) return;
    if (m_header->readerPid == m_token) m_header->readerHeartbeatMs = 0;
}

}  // namespace vcam
