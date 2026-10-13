#include "vcam_filter.h"

#include <dvdmedia.h>
#include <mmsystem.h>

#include <algorithm>
#include <cstring>
#include <new>

#include "vcam_convert.h"
#include "vcam_ids.h"
#include "vcam_placeholder.h"

namespace vcam {

namespace {

const DWORD kFourccNV12 = MAKEFOURCC('N', 'V', '1', '2');
const DWORD kFourccYUY2 = MAKEFOURCC('Y', 'U', 'Y', '2');

// El primero es el predeterminado ("Predeterminado del dispositivo" en OBS): 1080p30 NV12.
// Para 4K se elige 3840x2160 en OBS ("Resolución/FPS: Personalizado").
const VideoFormat kFormats[] = {
    {&MEDIASUBTYPE_NV12, kFourccNV12, 12, 1920, 1080, 30},
    {&MEDIASUBTYPE_NV12, kFourccNV12, 12, 3840, 2160, 30},
    {&MEDIASUBTYPE_NV12, kFourccNV12, 12, 2560, 1440, 30},
    {&MEDIASUBTYPE_NV12, kFourccNV12, 12, 1280, 720, 30},
    {&MEDIASUBTYPE_YUY2, kFourccYUY2, 16, 1920, 1080, 30},
    {&MEDIASUBTYPE_YUY2, kFourccYUY2, 16, 1280, 720, 30},
    {&MEDIASUBTYPE_RGB32, BI_RGB, 32, 1920, 1080, 30},
    {&MEDIASUBTYPE_RGB32, BI_RGB, 32, 1280, 720, 30},
};
const int kFormatCount = static_cast<int>(sizeof(kFormats) / sizeof(kFormats[0]));

// Cualquier ritmo entre 5 y 60 imágenes por segundo.
const REFERENCE_TIME kMinInterval = 10000000 / 60;
const REFERENCE_TIME kMaxInterval = 10000000 / 5;

REFERENCE_TIME DefaultInterval(const VideoFormat& f) { return 10000000 / f.fps; }

const BITMAPINFOHEADER* HeaderOf(const AM_MEDIA_TYPE* mt, const RECT** target, REFERENCE_TIME* interval) {
    if (mt->formattype == FORMAT_VideoInfo && mt->pbFormat && mt->cbFormat >= sizeof(VIDEOINFOHEADER)) {
        auto* v = reinterpret_cast<const VIDEOINFOHEADER*>(mt->pbFormat);
        *target = &v->rcTarget;
        *interval = v->AvgTimePerFrame;
        return &v->bmiHeader;
    }
    if (mt->formattype == FORMAT_VideoInfo2 && mt->pbFormat && mt->cbFormat >= sizeof(VIDEOINFOHEADER2)) {
        auto* v = reinterpret_cast<const VIDEOINFOHEADER2*>(mt->pbFormat);
        *target = &v->rcTarget;
        *interval = v->AvgTimePerFrame;
        return &v->bmiHeader;
    }
    return nullptr;
}

}  // namespace

const VideoFormat* Formats(int* count) {
    *count = kFormatCount;
    return kFormats;
}

size_t SampleBytes(const VideoFormat& f) {
    return static_cast<size_t>(f.width) * static_cast<size_t>(f.height) * f.bitCount / 8;
}

// ------------------------------------------------------------------ filtro

template <int N>
CUnknown* WINAPI CVCamFilter::CreateInstance(LPUNKNOWN unk, HRESULT* hr) {
    static_assert(N >= 1 && N <= static_cast<int>(VCAM_COUNT), "índice de cámara");
    auto* f = new (std::nothrow) CVCamFilter(unk, hr, N, CLSID_CamaraNdiVCam[N - 1]);
    if (!f && hr) *hr = E_OUTOFMEMORY;
    return f;
}

template CUnknown* WINAPI CVCamFilter::CreateInstance<1>(LPUNKNOWN, HRESULT*);
template CUnknown* WINAPI CVCamFilter::CreateInstance<2>(LPUNKNOWN, HRESULT*);
template CUnknown* WINAPI CVCamFilter::CreateInstance<3>(LPUNKNOWN, HRESULT*);
template CUnknown* WINAPI CVCamFilter::CreateInstance<4>(LPUNKNOWN, HRESULT*);

CVCamFilter::CVCamFilter(LPUNKNOWN unk, HRESULT* hr, int index, const CLSID& clsid)
    : CSource(NAME("Camara NDI"), unk, clsid), m_index(index) {
    // El constructor del pin se registra solo en el filtro (CSource::AddPin).
    new (std::nothrow) CVCamPin(hr, this, index);
}

CVCamPin* CVCamFilter::Pin() const { return m_iPins > 0 ? static_cast<CVCamPin*>(m_paStreams[0]) : nullptr; }

STDMETHODIMP CVCamFilter::NonDelegatingQueryInterface(REFIID riid, void** ppv) {
    CheckPointer(ppv, E_POINTER);
    if (riid == IID_IAMFilterMiscFlags) return GetInterface(static_cast<IAMFilterMiscFlags*>(this), ppv);
    if (riid == IID_IAMStreamConfig) {
        // Algunos programas lo piden al filtro y no al pin.
        CVCamPin* pin = Pin();
        if (!pin) return E_NOINTERFACE;
        return pin->NonDelegatingQueryInterface(riid, ppv);
    }
    return CSource::NonDelegatingQueryInterface(riid, ppv);
}

STDMETHODIMP CVCamFilter::GetState(DWORD ms, FILTER_STATE* state) {
    HRESULT hr = CSource::GetState(ms, state);
    // Fuente en vivo: en pausa no se adelantan imágenes.
    if (SUCCEEDED(hr) && state && *state == State_Paused) return VFW_S_CANT_CUE;
    return hr;
}

STDMETHODIMP CVCamFilter::Run(REFERENCE_TIME start) {
    HRESULT hr = CSource::Run(start);
    if (SUCCEEDED(hr)) m_running.store(true, std::memory_order_release);
    return hr;
}

STDMETHODIMP CVCamFilter::Pause() {
    m_running.store(false, std::memory_order_release);
    return CSource::Pause();
}

STDMETHODIMP CVCamFilter::Stop() {
    m_running.store(false, std::memory_order_release);
    return CSource::Stop();
}

// ------------------------------------------------------------------ pin

CVCamPin::CVCamPin(HRESULT* hr, CVCamFilter* filter, int index)
    : CSourceStream(NAME("Camara NDI salida"), hr, filter, L"Captura"), m_index(index), m_reader(index) {
    m_current.index = 0;
    m_current.interval = DefaultInterval(kFormats[0]);
    m_current.stride = kFormats[0].width;
}

CVCamPin::~CVCamPin() = default;

STDMETHODIMP CVCamPin::NonDelegatingQueryInterface(REFIID riid, void** ppv) {
    CheckPointer(ppv, E_POINTER);
    if (riid == IID_IAMStreamConfig) return GetInterface(static_cast<IAMStreamConfig*>(this), ppv);
    if (riid == IID_IKsPropertySet) return GetInterface(static_cast<IKsPropertySet*>(this), ppv);
    return CSourceStream::NonDelegatingQueryInterface(riid, ppv);
}

bool CVCamPin::MatchType(const AM_MEDIA_TYPE* mt, Current* out) const {
    if (!mt || mt->majortype != MEDIATYPE_Video) return false;
    const RECT* target = nullptr;
    REFERENCE_TIME interval = 0;
    const BITMAPINFOHEADER* bmi = HeaderOf(mt, &target, &interval);
    if (!bmi) return false;
    const int stride = bmi->biWidth;
    const int width = (target && target->right > target->left) ? target->right - target->left : bmi->biWidth;
    const bool negative = bmi->biHeight < 0;
    const int height = negative ? -bmi->biHeight : bmi->biHeight;
    for (int i = 0; i < kFormatCount; i++) {
        const VideoFormat& f = kFormats[i];
        if (mt->subtype != *f.subtype || bmi->biCompression != f.compression || bmi->biBitCount != f.bitCount) continue;
        if (width != f.width || height != f.height || stride < f.width) continue;
        // YUV con alto negativo no existe; RGB de arriba hacia abajo sí.
        if (negative && f.subtype != &MEDIASUBTYPE_RGB32) continue;
        if (out) {
            out->index = i;
            out->interval = interval <= 0 ? DefaultInterval(f) : std::min(std::max(interval, kMinInterval), kMaxInterval);
            out->topDown = negative;
            out->stride = stride;
        }
        return true;
    }
    return false;
}

void CVCamPin::BuildType(const Current& c, CMediaType* mt) const {
    const VideoFormat& f = kFormats[c.index];
    auto* vih = reinterpret_cast<VIDEOINFOHEADER*>(mt->AllocFormatBuffer(sizeof(VIDEOINFOHEADER)));
    if (!vih) return;
    ZeroMemory(vih, sizeof(VIDEOINFOHEADER));
    const int stride = c.stride > 0 ? c.stride : f.width;
    const DWORD bytes = static_cast<DWORD>(static_cast<size_t>(stride) * f.height * f.bitCount / 8);
    vih->AvgTimePerFrame = c.interval;
    vih->dwBitRate = static_cast<DWORD>(std::min<double>(static_cast<double>(bytes) * 8.0 * 10000000.0 / static_cast<double>(c.interval), 4294967295.0));
    vih->bmiHeader.biSize = sizeof(BITMAPINFOHEADER);
    vih->bmiHeader.biWidth = stride;
    vih->bmiHeader.biHeight = c.topDown ? -f.height : f.height;
    vih->bmiHeader.biPlanes = 1;
    vih->bmiHeader.biBitCount = f.bitCount;
    vih->bmiHeader.biCompression = f.compression;
    vih->bmiHeader.biSizeImage = bytes;
    if (stride != f.width) {
        SetRect(&vih->rcSource, 0, 0, f.width, f.height);
        SetRect(&vih->rcTarget, 0, 0, f.width, f.height);
    }
    mt->SetType(&MEDIATYPE_Video);
    mt->SetSubtype(f.subtype);
    mt->SetFormatType(&FORMAT_VideoInfo);
    mt->SetTemporalCompression(FALSE);
    mt->SetSampleSize(bytes);
}

HRESULT CVCamPin::GetMediaType(int position, CMediaType* mt) {
    CheckPointer(mt, E_POINTER);
    if (position < 0) return E_INVALIDARG;
    if (position > kFormatCount) return VFW_S_NO_MORE_ITEMS;
    Current c;
    if (position == 0) {
        // Primero el formato elegido (o el predeterminado).
        CAutoLock lock(&m_formatLock);
        c = m_current;
    } else {
        c.index = position - 1;
        c.interval = DefaultInterval(kFormats[c.index]);
        c.stride = kFormats[c.index].width;
    }
    BuildType(c, mt);
    return S_OK;
}

HRESULT CVCamPin::CheckMediaType(const CMediaType* mt) {
    Current c;
    if (!MatchType(mt, &c)) return VFW_E_TYPE_NOT_ACCEPTED;
    CAutoLock lock(&m_formatLock);
    // Si el programa eligió un formato con SetFormat, sólo se acepta ese (mismo tamaño y tipo).
    if (m_formatSet && c.index != m_current.index) return VFW_E_TYPE_NOT_ACCEPTED;
    return S_OK;
}

HRESULT CVCamPin::SetMediaType(const CMediaType* mt) {
    Current c;
    if (!MatchType(mt, &c)) return VFW_E_TYPE_NOT_ACCEPTED;
    HRESULT hr = CSourceStream::SetMediaType(mt);
    if (FAILED(hr)) return hr;
    CAutoLock lock(&m_formatLock);
    m_current = c;
    return S_OK;
}

HRESULT CVCamPin::DecideBufferSize(IMemAllocator* alloc, ALLOCATOR_PROPERTIES* props) {
    CheckPointer(alloc, E_POINTER);
    CheckPointer(props, E_POINTER);
    CAutoLock lock(m_pFilter->pStateLock());
    Current c;
    {
        CAutoLock fl(&m_formatLock);
        c = m_current;
    }
    const VideoFormat& f = kFormats[c.index];
    const long bytes = static_cast<long>(static_cast<size_t>(c.stride) * f.height * f.bitCount / 8);
    if (props->cBuffers < 2) props->cBuffers = 2;
    if (props->cbBuffer < bytes) props->cbBuffer = bytes;
    if (props->cbAlign < 1) props->cbAlign = 1;
    ALLOCATOR_PROPERTIES actual = {};
    HRESULT hr = alloc->SetProperties(props, &actual);
    if (FAILED(hr)) return hr;
    if (actual.cbBuffer < bytes) return E_FAIL;
    return S_OK;
}

HRESULT CVCamPin::OnThreadCreate() {
    QueryPerformanceFrequency(&m_freq);
    m_nextQpc = 0;
    m_frameCount = 0;
    m_timerPeriod = timeBeginPeriod(1) == TIMERR_NOERROR;
    return S_OK;
}

HRESULT CVCamPin::OnThreadDestroy() {
    m_reader.ClearWant();
    if (m_timerPeriod) timeEndPeriod(1);
    m_timerPeriod = false;
    return S_OK;
}

void CVCamPin::WaitForNextFrame(REFERENCE_TIME interval) {
    LARGE_INTEGER now;
    QueryPerformanceCounter(&now);
    const LONGLONG step = interval * m_freq.QuadPart / 10000000;
    if (m_nextQpc == 0) {
        m_nextQpc = now.QuadPart;  // la primera imagen, enseguida
        return;
    }
    m_nextQpc += step;
    // Si nos atrasamos mucho (PC ocupado) no se intenta recuperar: se sigue desde ahora.
    if (now.QuadPart > m_nextQpc + 2 * step) {
        m_nextQpc = now.QuadPart;
        return;
    }
    for (;;) {
        QueryPerformanceCounter(&now);
        const LONGLONG left = m_nextQpc - now.QuadPart;
        if (left <= 0) break;
        const LONGLONG ms = left * 1000 / m_freq.QuadPart;
        if (ms >= 2) Sleep(static_cast<DWORD>(ms - 1));
        else Sleep(0);
    }
}

const std::vector<uint8_t>& CVCamPin::Placeholder(int w, int h, const std::wstring& title, const std::wstring& subtitle) {
    std::wstring key = title + L"\n" + subtitle + L"\n" + std::to_wstring(w) + L"x" + std::to_wstring(h);
    if (key != m_placeholderKey) {
        m_placeholder = RenderPlaceholder(w, h, title, subtitle);
        m_placeholderKey = key;
    }
    return m_placeholder;
}

void CVCamPin::Produce(uint8_t* dst, long dstBytes, const Current& c) {
    const VideoFormat& f = kFormats[c.index];
    const uint64_t now = GetTickCount64();
    m_reader.Poll(now);
    const uint32_t fps100 = static_cast<uint32_t>((1000000000LL + c.interval / 2) / c.interval);
    m_reader.PublishWant(static_cast<uint32_t>(f.width), static_cast<uint32_t>(f.height), fps100, now);

    uint32_t w = 0, h = 0;
    if (m_reader.ReadLatest(m_frame, w, h, m_frameSeq)) {
        m_frameW = w;
        m_frameH = h;
        m_frameAtMs = now;
    }
    const SourceStatus st = m_reader.Status(now);
    // Un corte corto no se nota: se repite la última imagen hasta 1,5 s.
    const bool fresh = m_frameAtMs != 0 && now - m_frameAtMs < 1500 && st.receiverAlive && !m_frame.empty();

    const uint8_t* src;
    int sw, sh;
    if (fresh) {
        src = m_frame.data();
        sw = static_cast<int>(m_frameW);
        sh = static_cast<int>(m_frameH);
    } else {
        std::wstring title, sub;
        const std::wstring cam = L"Celular " + std::to_wstring(m_index);
        if (!st.receiverAlive) {
            title = L"Cámara NDI - " + cam;
            sub = L"Abre el Receptor Cámara NDI en este PC";
        } else if (st.state == VCAM_STATE_EMPTY) {
            title = cam;
            sub = L"Sin celular: abre la app Cámara NDI en el celular y pulsa Transmitir";
        } else {
            title = st.name.empty() ? cam : st.name;
            sub = L"Esperando video del celular…";
        }
        const std::vector<uint8_t>& ph = Placeholder(f.width, f.height, title, sub);
        src = ph.data();
        sw = f.width;
        sh = f.height;
    }

    const uint8_t* nv12 = src;
    if (sw != f.width || sh != f.height) {
        m_scaled.resize(static_cast<size_t>(f.width) * f.height * 3 / 2);
        ScaleNV12(src, sw, sh, m_scaled.data(), f.width, f.height);
        nv12 = m_scaled.data();
    }

    const size_t need = static_cast<size_t>(c.stride) * f.height * f.bitCount / 8;
    if (dstBytes < 0 || static_cast<size_t>(dstBytes) < need) return;
    if (f.subtype == &MEDIASUBTYPE_NV12) {
        CopyNV12(nv12, f.width, f.height, dst, c.stride);
    } else if (f.subtype == &MEDIASUBTYPE_YUY2) {
        NV12ToYUY2(nv12, f.width, f.height, dst, c.stride * 2);
    } else {
        NV12ToRGB32(nv12, f.width, f.height, dst, c.stride * 4, !c.topDown);
    }
}

HRESULT CVCamPin::FillBuffer(IMediaSample* sample) {
    CheckPointer(sample, E_POINTER);

    // El que recibe puede cambiar el formato (por ejemplo, filas más anchas): viene adjunto a la muestra.
    AM_MEDIA_TYPE* pmt = nullptr;
    if (sample->GetMediaType(&pmt) == S_OK && pmt) {
        Current nc;
        if (MatchType(pmt, &nc)) {
            CSourceStream::SetMediaType(static_cast<CMediaType*>(pmt));
            CAutoLock lock(&m_formatLock);
            m_current = nc;
        }
        DeleteMediaType(pmt);
    }

    Current c;
    {
        CAutoLock lock(&m_formatLock);
        c = m_current;
    }
    WaitForNextFrame(c.interval);

    BYTE* data = nullptr;
    HRESULT hr = sample->GetPointer(&data);
    if (FAILED(hr) || !data) return E_POINTER;
    const long size = sample->GetSize();
    Produce(data, size, c);
    const VideoFormat& f = kFormats[c.index];
    const long used = static_cast<long>(static_cast<size_t>(c.stride) * f.height * f.bitCount / 8);
    sample->SetActualDataLength(std::min(used, size));

    // Marcas de tiempo del reloj del grafo (así OBS no acumula retraso); sin reloj, sin marcas.
    CVCamFilter* filter = static_cast<CVCamFilter*>(m_pFilter);
    CRefTime streamTime;
    if (filter->Running() && SUCCEEDED(filter->StreamTime(streamTime)) && streamTime >= 0) {
        REFERENCE_TIME start = streamTime;
        REFERENCE_TIME stop = start + c.interval;
        sample->SetTime(&start, &stop);
    } else {
        sample->SetTime(nullptr, nullptr);
    }
    sample->SetSyncPoint(TRUE);
    sample->SetDiscontinuity(m_frameCount == 0);
    sample->SetPreroll(FALSE);
    m_frameCount++;
    return S_OK;
}

// ------------------------------------------------------------------ IAMStreamConfig

HRESULT STDMETHODCALLTYPE CVCamPin::SetFormat(AM_MEDIA_TYPE* mt) {
    CAutoLock stateLock(m_pFilter->pStateLock());
    if (!m_pFilter->IsStopped()) return VFW_E_NOT_STOPPED;
    Current c;
    if (!mt) {
        // NULL = volver al predeterminado.
        c.index = 0;
        c.interval = DefaultInterval(kFormats[0]);
        c.stride = kFormats[0].width;
    } else if (!MatchType(mt, &c)) {
        return VFW_E_INVALIDMEDIATYPE;
    }
    {
        CAutoLock lock(&m_formatLock);
        m_current = c;
        m_formatSet = mt != nullptr;
    }
    // Si ya estaba conectado con otro formato, pedir al grafo que reconecte.
    if (IsConnected()) {
        IFilterGraph* graph = m_pFilter->GetFilterGraph();
        if (graph) graph->Reconnect(this);
    }
    return S_OK;
}

HRESULT STDMETHODCALLTYPE CVCamPin::GetFormat(AM_MEDIA_TYPE** mt) {
    CheckPointer(mt, E_POINTER);
    CMediaType cmt;
    Current c;
    {
        CAutoLock lock(&m_formatLock);
        c = m_current;
    }
    BuildType(c, &cmt);
    *mt = CreateMediaType(&cmt);
    return *mt ? S_OK : E_OUTOFMEMORY;
}

HRESULT STDMETHODCALLTYPE CVCamPin::GetNumberOfCapabilities(int* count, int* size) {
    CheckPointer(count, E_POINTER);
    CheckPointer(size, E_POINTER);
    *count = kFormatCount;
    *size = sizeof(VIDEO_STREAM_CONFIG_CAPS);
    return S_OK;
}

HRESULT STDMETHODCALLTYPE CVCamPin::GetStreamCaps(int index, AM_MEDIA_TYPE** mt, BYTE* capsBytes) {
    CheckPointer(mt, E_POINTER);
    CheckPointer(capsBytes, E_POINTER);
    if (index < 0) return E_INVALIDARG;
    if (index >= kFormatCount) return S_FALSE;
    const VideoFormat& f = kFormats[index];
    Current c;
    c.index = index;
    c.interval = DefaultInterval(f);
    c.stride = f.width;
    CMediaType cmt;
    BuildType(c, &cmt);
    *mt = CreateMediaType(&cmt);
    if (!*mt) return E_OUTOFMEMORY;

    auto* caps = reinterpret_cast<VIDEO_STREAM_CONFIG_CAPS*>(capsBytes);
    ZeroMemory(caps, sizeof(VIDEO_STREAM_CONFIG_CAPS));
    const SIZE sz = {f.width, f.height};
    caps->guid = FORMAT_VideoInfo;
    caps->VideoStandard = 0;
    caps->InputSize = sz;
    caps->MinCroppingSize = sz;
    caps->MaxCroppingSize = sz;
    caps->CropGranularityX = 1;
    caps->CropGranularityY = 1;
    caps->CropAlignX = 1;
    caps->CropAlignY = 1;
    caps->MinOutputSize = sz;
    caps->MaxOutputSize = sz;
    caps->OutputGranularityX = 1;
    caps->OutputGranularityY = 1;
    caps->MinFrameInterval = kMinInterval;
    caps->MaxFrameInterval = kMaxInterval;
    const double bits = static_cast<double>(SampleBytes(f)) * 8.0;
    caps->MinBitsPerSecond = static_cast<LONG>(std::min(bits * 5.0, 2147483647.0));
    caps->MaxBitsPerSecond = static_cast<LONG>(std::min(bits * 60.0, 2147483647.0));
    return S_OK;
}

// ------------------------------------------------------------------ IKsPropertySet

HRESULT STDMETHODCALLTYPE CVCamPin::Get(REFGUID set, DWORD id, LPVOID, DWORD, LPVOID data, DWORD dataSize, DWORD* returned) {
    if (set != AMPROPSETID_Pin) return E_PROP_SET_UNSUPPORTED;
    if (id != AMPROPERTY_PIN_CATEGORY) return E_PROP_ID_UNSUPPORTED;
    if (!data && !returned) return E_POINTER;
    if (returned) *returned = sizeof(GUID);
    if (!data) return S_OK;
    if (dataSize < sizeof(GUID)) return E_UNEXPECTED;
    *static_cast<GUID*>(data) = PIN_CATEGORY_CAPTURE;
    return S_OK;
}

HRESULT STDMETHODCALLTYPE CVCamPin::QuerySupported(REFGUID set, DWORD id, DWORD* support) {
    if (set != AMPROPSETID_Pin) return E_PROP_SET_UNSUPPORTED;
    if (id != AMPROPERTY_PIN_CATEGORY) return E_PROP_ID_UNSUPPORTED;
    if (support) *support = KSPROPERTY_SUPPORT_GET;
    return S_OK;
}

}  // namespace vcam
