// Filtro DirectShow "Cámara NDI - Celular N": una cámara virtual que muestra lo que el receptor
// escribe en la memoria compartida. OBS (Dispositivo de captura de video), Zoom, Meet, etc. la ven como
// una webcam más.
#pragma once

#include <streams.h>

#include <atomic>
#include <cstdint>
#include <memory>
#include <string>
#include <vector>

#include "vcam_shm.h"

namespace vcam {

struct VideoFormat {
    const GUID* subtype;
    DWORD compression;  // FOURCC o BI_RGB
    WORD bitCount;
    int width;
    int height;
    int fps;
};

// Formatos que ofrece la cámara, el primero es el predeterminado.
const VideoFormat* Formats(int* count);
size_t SampleBytes(const VideoFormat& f);

class CVCamPin;

class CVCamFilter : public CSource, public IAMFilterMiscFlags {
public:
    template <int N>
    static CUnknown* WINAPI CreateInstance(LPUNKNOWN unk, HRESULT* hr);

    DECLARE_IUNKNOWN
    STDMETHODIMP NonDelegatingQueryInterface(REFIID riid, void** ppv) override;

    // IAMFilterMiscFlags: le dice al grafo que somos una fuente "en vivo".
    ULONG STDMETHODCALLTYPE GetMiscFlags() override { return AM_FILTER_MISC_FLAGS_IS_SOURCE; }

    // En pausa, una fuente en vivo no adelanta imágenes (VFW_S_CANT_CUE).
    STDMETHODIMP GetState(DWORD ms, FILTER_STATE* state) override;
    STDMETHODIMP Run(REFERENCE_TIME start) override;
    STDMETHODIMP Pause() override;
    STDMETHODIMP Stop() override;

    int CameraIndex() const { return m_index; }
    CVCamPin* Pin() const;
    // Sin candados: lo consulta el hilo de las imágenes, y Stop() espera a ese hilo con el candado del
    // filtro tomado (si el hilo también lo pidiera, OBS se colgaría al detener la cámara).
    bool Running() const { return m_running.load(std::memory_order_acquire); }

private:
    CVCamFilter(LPUNKNOWN unk, HRESULT* hr, int index, const CLSID& clsid);
    int m_index;
    std::atomic<bool> m_running{false};
};

class CVCamPin : public CSourceStream, public IAMStreamConfig, public IKsPropertySet {
public:
    CVCamPin(HRESULT* hr, CVCamFilter* filter, int index);
    ~CVCamPin() override;

    DECLARE_IUNKNOWN
    STDMETHODIMP NonDelegatingQueryInterface(REFIID riid, void** ppv) override;

    // IQualityControl: no ajustamos nada por calidad (ritmo fijo).
    STDMETHODIMP Notify(IBaseFilter*, Quality) override { return S_OK; }

    // CBasePin / CBaseOutputPin / CSourceStream
    HRESULT GetMediaType(int position, CMediaType* mt) override;
    HRESULT CheckMediaType(const CMediaType* mt) override;
    HRESULT SetMediaType(const CMediaType* mt) override;
    HRESULT DecideBufferSize(IMemAllocator* alloc, ALLOCATOR_PROPERTIES* props) override;
    HRESULT FillBuffer(IMediaSample* sample) override;
    HRESULT OnThreadCreate() override;
    HRESULT OnThreadDestroy() override;

    // IAMStreamConfig
    HRESULT STDMETHODCALLTYPE SetFormat(AM_MEDIA_TYPE* mt) override;
    HRESULT STDMETHODCALLTYPE GetFormat(AM_MEDIA_TYPE** mt) override;
    HRESULT STDMETHODCALLTYPE GetNumberOfCapabilities(int* count, int* size) override;
    HRESULT STDMETHODCALLTYPE GetStreamCaps(int index, AM_MEDIA_TYPE** mt, BYTE* caps) override;

    // IKsPropertySet (categoría del pin = captura)
    HRESULT STDMETHODCALLTYPE Set(REFGUID, DWORD, LPVOID, DWORD, LPVOID, DWORD) override { return E_NOTIMPL; }
    HRESULT STDMETHODCALLTYPE Get(REFGUID set, DWORD id, LPVOID instance, DWORD instanceSize, LPVOID data,
                                  DWORD dataSize, DWORD* returned) override;
    HRESULT STDMETHODCALLTYPE QuerySupported(REFGUID set, DWORD id, DWORD* support) override;

private:
    // Formato elegido: índice en Formats() y duración de cada imagen (100 ns).
    struct Current {
        int index = 0;
        REFERENCE_TIME interval = 333333;
        bool topDown = false;  // sólo RGB: si el que recibe pidió filas de arriba hacia abajo
        int stride = 0;        // en píxeles (puede ser mayor que el ancho)
    };

    bool MatchType(const AM_MEDIA_TYPE* mt, Current* out) const;
    void BuildType(const Current& c, CMediaType* mt) const;
    void WaitForNextFrame(REFERENCE_TIME interval);
    void Produce(uint8_t* dst, long dstBytes, const Current& c);
    const std::vector<uint8_t>& Placeholder(int w, int h, const std::wstring& title, const std::wstring& subtitle);

    int m_index;
    CCritSec m_formatLock;
    Current m_current;
    bool m_formatSet = false;

    SharedReader m_reader;
    std::vector<uint8_t> m_frame;      // última imagen buena del receptor (NV12, su tamaño)
    uint32_t m_frameW = 0, m_frameH = 0;
    uint64_t m_frameSeq = 0;
    uint64_t m_frameAtMs = 0;
    std::vector<uint8_t> m_scaled;     // NV12 al tamaño pedido
    std::vector<uint8_t> m_placeholder;
    std::wstring m_placeholderKey;

    LARGE_INTEGER m_freq = {};
    LONGLONG m_nextQpc = 0;
    LONGLONG m_frameCount = 0;
    bool m_timerPeriod = false;
};

}  // namespace vcam
