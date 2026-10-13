// Herramienta de prueba de la cámara virtual (no se distribuye):
//   vcamtest list                         -> lista las cámaras que ve DirectShow
//   vcamtest feed <n> <seg> [w h fps]     -> hace de receptor: escribe imágenes de prueba en la cámara n
//   vcamtest grab <n> <imagenes> [w h]    -> hace de OBS: abre la cámara n y captura imágenes
// "grab" imprime una línea "RESULTADO ..." con lo medido; el script de pruebas la revisa.
#include <windows.h>
#include <dshow.h>
#include <initguid.h>

#if defined(__MINGW32__)
#include <qedit.h>
#else
// El SDK actual de Windows ya no trae qedit.h: declaraciones mínimas del Sample Grabber (qedit.dll).
DEFINE_GUID(IID_ISampleGrabberCB, 0x0579154A, 0x2B53, 0x4994, 0xB0, 0xD0, 0xE7, 0x73, 0x14, 0x8E, 0xFF, 0x85);
DEFINE_GUID(IID_ISampleGrabber, 0x6B652FFF, 0x11FE, 0x4fce, 0x92, 0xAD, 0x02, 0x66, 0xB5, 0xD7, 0xC7, 0x8F);
struct ISampleGrabberCB : public IUnknown {
    virtual HRESULT STDMETHODCALLTYPE SampleCB(double sampleTime, IMediaSample* sample) = 0;
    virtual HRESULT STDMETHODCALLTYPE BufferCB(double sampleTime, BYTE* buffer, long length) = 0;
};
struct ISampleGrabber : public IUnknown {
    virtual HRESULT STDMETHODCALLTYPE SetOneShot(BOOL oneShot) = 0;
    virtual HRESULT STDMETHODCALLTYPE SetMediaType(const AM_MEDIA_TYPE* type) = 0;
    virtual HRESULT STDMETHODCALLTYPE GetConnectedMediaType(AM_MEDIA_TYPE* type) = 0;
    virtual HRESULT STDMETHODCALLTYPE SetBufferSamples(BOOL bufferThem) = 0;
    virtual HRESULT STDMETHODCALLTYPE GetCurrentBuffer(long* size, long* buffer) = 0;
    virtual HRESULT STDMETHODCALLTYPE GetCurrentSample(IMediaSample** sample) = 0;
    virtual HRESULT STDMETHODCALLTYPE SetCallback(ISampleGrabberCB* callback, long whichMethod) = 0;
};
#endif

#include <algorithm>
#include <atomic>
#include <cmath>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <string>
#include <vector>

#include "vcam_shared.h"
#include "vcam_shm.h"

DEFINE_GUID(kClsidSampleGrabber, 0xC1F400A0, 0x3F08, 0x11d3, 0x9F, 0x0B, 0x00, 0x60, 0x08, 0x03, 0x9E, 0x37);
DEFINE_GUID(kClsidNullRenderer, 0xC1F400A4, 0x3F08, 0x11d3, 0x9F, 0x0B, 0x00, 0x60, 0x08, 0x03, 0x9E, 0x37);
DEFINE_GUID(kVCam1, 0xc719e32f, 0x5265, 0x4841, 0x8a, 0x31, 0x3c, 0x94, 0xb3, 0x2b, 0xa3, 0x21);
DEFINE_GUID(kVCam2, 0x01255299, 0x1f34, 0x4daa, 0xb1, 0xb7, 0x73, 0xe7, 0x7b, 0xb3, 0x93, 0x3c);
DEFINE_GUID(kVCam3, 0x0fe49c3a, 0x1e99, 0x4f95, 0x90, 0xc3, 0xed, 0xa9, 0x87, 0xb7, 0xcc, 0xde);
DEFINE_GUID(kVCam4, 0xb553d60d, 0xf093, 0x4e01, 0x9a, 0x17, 0x69, 0x36, 0x7a, 0x8f, 0xd2, 0x3f);

namespace {

const GUID* const kCams[4] = {&kVCam1, &kVCam2, &kVCam3, &kVCam4};

std::string Narrow(const wchar_t* w) {
    int n = WideCharToMultiByte(CP_UTF8, 0, w, -1, nullptr, 0, nullptr, nullptr);
    std::string s(static_cast<size_t>(n > 0 ? n - 1 : 0), '\0');
    if (n > 1) WideCharToMultiByte(CP_UTF8, 0, w, -1, &s[0], n, nullptr, nullptr);
    return s;
}

int List() {
    ICreateDevEnum* de = nullptr;
    if (FAILED(CoCreateInstance(CLSID_SystemDeviceEnum, nullptr, CLSCTX_INPROC_SERVER, IID_ICreateDevEnum, (void**)&de))) {
        std::printf("ERROR no se pudo crear el enumerador de dispositivos\n");
        return 2;
    }
    IEnumMoniker* em = nullptr;
    int count = 0;
    if (de->CreateClassEnumerator(CLSID_VideoInputDeviceCategory, &em, 0) == S_OK && em) {
        IMoniker* m = nullptr;
        while (em->Next(1, &m, nullptr) == S_OK) {
            IPropertyBag* bag = nullptr;
            if (SUCCEEDED(m->BindToStorage(nullptr, nullptr, IID_IPropertyBag, (void**)&bag))) {
                VARIANT name, clsid;
                VariantInit(&name);
                VariantInit(&clsid);
                bag->Read(L"FriendlyName", &name, nullptr);
                bag->Read(L"CLSID", &clsid, nullptr);
                std::printf("DISPOSITIVO \"%s\" %s\n", name.vt == VT_BSTR ? Narrow(name.bstrVal).c_str() : "?",
                            clsid.vt == VT_BSTR ? Narrow(clsid.bstrVal).c_str() : "-");
                VariantClear(&name);
                VariantClear(&clsid);
                bag->Release();
                count++;
            }
            m->Release();
        }
        em->Release();
    }
    de->Release();
    std::printf("TOTAL %d\n", count);
    return 0;
}

// ---------------------------------------------------------------- feed (hace de receptor)

int Feed(int cam, int seconds, int w, int h, int fps) {
    SECURITY_ATTRIBUTES sa;
    PSECURITY_DESCRIPTOR sd = nullptr;
    vcam::MakeSharedSecurity(sa, sd);
    HANDLE ctl = CreateFileMappingW(INVALID_HANDLE_VALUE, &sa, PAGE_READWRITE, 0, VCAM_CONTROL_BYTES, vcam::ControlName().c_str());
    HANDLE frm = CreateFileMappingW(INVALID_HANDLE_VALUE, &sa, PAGE_READWRITE, 0, VCAM_TOTAL_BYTES, vcam::FrameName(cam).c_str());
    if (!ctl || !frm) {
        std::printf("ERROR CreateFileMapping %lu\n", GetLastError());
        return 2;
    }
    auto* control = static_cast<VCamControl*>(MapViewOfFile(ctl, FILE_MAP_ALL_ACCESS, 0, 0, VCAM_CONTROL_BYTES));
    auto* hdr = static_cast<VCamHeader*>(MapViewOfFile(frm, FILE_MAP_ALL_ACCESS, 0, 0, VCAM_TOTAL_BYTES));
    if (!control || !hdr) {
        std::printf("ERROR MapViewOfFile %lu\n", GetLastError());
        return 2;
    }
    uint8_t* data = reinterpret_cast<uint8_t*>(hdr) + VCAM_HEADER_BYTES;
    control->version = VCAM_VERSION;
    control->cameraCount = VCAM_COUNT;
    control->receiverPid = GetCurrentProcessId();
    const wchar_t* name = L"Celular de prueba";
    for (int i = 0; name[i] && i < 63; i++) control->cameras[cam - 1].name[i] = static_cast<uint16_t>(name[i]);
    control->cameras[cam - 1].state = VCAM_STATE_LIVE;
    MemoryBarrier();
    control->magic = VCAM_CONTROL_MAGIC;
    hdr->version = VCAM_VERSION;
    hdr->headerBytes = VCAM_HEADER_BYTES;
    hdr->slotCount = VCAM_SLOTS;
    hdr->slotBytes = VCAM_SLOT_BYTES;
    hdr->maxWidth = VCAM_MAX_WIDTH;
    hdr->maxHeight = VCAM_MAX_HEIGHT;
    hdr->writerPid = GetCurrentProcessId();
    MemoryBarrier();
    hdr->magic = VCAM_FRAME_MAGIC;

    const DWORD start = GetTickCount();
    uint64_t seq = 0;
    uint32_t slot = 0;
    while (GetTickCount() - start < static_cast<DWORD>(seconds) * 1000) {
        const uint64_t now = GetTickCount64();
        control->receiverHeartbeatMs = static_cast<int64_t>(now);
        hdr->writerHeartbeatMs = static_cast<int64_t>(now);
        // Si el consumidor pidió otro tamaño, se le manda ese (como hace el receptor).
        int fw = w, fh = h;
        if (hdr->readerHeartbeatMs != 0 && hdr->wantWidth >= 16 && hdr->wantHeight >= 16) {
            fw = static_cast<int>(hdr->wantWidth);
            fh = static_cast<int>(hdr->wantHeight);
        }
        seq++;
        slot = (slot + 1) % VCAM_SLOTS;
        VCamSlot& s = hdr->slots[slot];
        s.seq = 0;
        MemoryBarrier();
        uint8_t* p = data + static_cast<size_t>(slot) * VCAM_SLOT_BYTES;
        // Patrón: luma = 16 + (seq*5 % 200) en toda la mitad izquierda, 128 en la derecha; croma neutro.
        const uint8_t level = static_cast<uint8_t>(16 + (seq * 5) % 200);
        for (int y = 0; y < fh; y++) {
            std::memset(p + static_cast<size_t>(y) * fw, level, static_cast<size_t>(fw / 2));
            std::memset(p + static_cast<size_t>(y) * fw + fw / 2, 128, static_cast<size_t>(fw - fw / 2));
        }
        std::memset(p + static_cast<size_t>(fw) * fh, 128, static_cast<size_t>(fw) * fh / 2);
        s.width = static_cast<uint32_t>(fw);
        s.height = static_cast<uint32_t>(fh);
        s.format = VCAM_FORMAT_NV12;
        s.timestampMs = static_cast<int64_t>(now);
        MemoryBarrier();
        s.seq = seq;
        hdr->latestSlot = slot;
        MemoryBarrier();
        hdr->latestSeq = seq;
        Sleep(static_cast<DWORD>(1000 / (fps > 0 ? fps : 30)));
    }
    control->cameras[cam - 1].state = VCAM_STATE_EMPTY;
    control->receiverHeartbeatMs = 0;
    std::printf("FEED fin: %llu imagenes\n", static_cast<unsigned long long>(seq));
    return 0;
}

// ---------------------------------------------------------------- grab (hace de OBS)

class Grabber : public ISampleGrabberCB {
public:
    std::atomic<int> frames{0};
    std::atomic<long> bytes{0};
    std::vector<double> leftLuma;
    std::vector<DWORD> times;
    int width = 0, height = 0;
    bool nv12 = true;
    CRITICAL_SECTION cs;
    Grabber() { InitializeCriticalSection(&cs); }
    ~Grabber() { DeleteCriticalSection(&cs); }
    STDMETHODIMP QueryInterface(REFIID riid, void** ppv) override {
        if (riid == IID_IUnknown || riid == IID_ISampleGrabberCB) {
            *ppv = static_cast<ISampleGrabberCB*>(this);
            return S_OK;
        }
        *ppv = nullptr;
        return E_NOINTERFACE;
    }
    STDMETHODIMP_(ULONG) AddRef() override { return 2; }
    STDMETHODIMP_(ULONG) Release() override { return 1; }
    STDMETHODIMP SampleCB(double, IMediaSample*) override { return S_OK; }
    STDMETHODIMP BufferCB(double, BYTE* buf, long len) override {
        double sum = 0;
        int n = 0;
        if (nv12 && width > 0 && height > 0 && len >= width * height) {
            // Promedio de luma en la mitad izquierda (donde "feed" pone el nivel que cambia).
            for (int y = height / 4; y < height * 3 / 4; y += 8)
                for (int x = 0; x < width / 2 - 8; x += 8) {
                    sum += buf[static_cast<size_t>(y) * width + x];
                    n++;
                }
        }
        EnterCriticalSection(&cs);
        leftLuma.push_back(n ? sum / n : -1);
        times.push_back(GetTickCount());
        LeaveCriticalSection(&cs);
        bytes = len;
        frames++;
        return S_OK;
    }
};

IBaseFilter* OpenCamera(int cam) {
    ICreateDevEnum* de = nullptr;
    if (FAILED(CoCreateInstance(CLSID_SystemDeviceEnum, nullptr, CLSCTX_INPROC_SERVER, IID_ICreateDevEnum, (void**)&de))) return nullptr;
    IEnumMoniker* em = nullptr;
    IBaseFilter* found = nullptr;
    if (de->CreateClassEnumerator(CLSID_VideoInputDeviceCategory, &em, 0) == S_OK && em) {
        IMoniker* m = nullptr;
        while (!found && em->Next(1, &m, nullptr) == S_OK) {
            IPropertyBag* bag = nullptr;
            if (SUCCEEDED(m->BindToStorage(nullptr, nullptr, IID_IPropertyBag, (void**)&bag))) {
                VARIANT clsid;
                VariantInit(&clsid);
                CLSID id;
                if (SUCCEEDED(bag->Read(L"CLSID", &clsid, nullptr)) && clsid.vt == VT_BSTR &&
                    SUCCEEDED(CLSIDFromString(clsid.bstrVal, &id)) && id == *kCams[cam - 1]) {
                    m->BindToObject(nullptr, nullptr, IID_IBaseFilter, (void**)&found);
                }
                VariantClear(&clsid);
                bag->Release();
            }
            m->Release();
        }
        em->Release();
    }
    de->Release();
    return found;
}

#define TRACE(...) do { std::fprintf(stderr, __VA_ARGS__); std::fflush(stderr); } while (0)

int Grab(int cam, int want, int w, int h) {
    TRACE("abrir camara %d\n", cam);
    IBaseFilter* src = OpenCamera(cam);
    if (!src) {
        std::printf("RESULTADO error=no-encontrada\n");
        return 3;
    }
    IGraphBuilder* graph = nullptr;
    CoCreateInstance(CLSID_FilterGraph, nullptr, CLSCTX_INPROC_SERVER, IID_IGraphBuilder, (void**)&graph);
    IBaseFilter* grabF = nullptr;
    CoCreateInstance(kClsidSampleGrabber, nullptr, CLSCTX_INPROC_SERVER, IID_IBaseFilter, (void**)&grabF);
    IBaseFilter* nullF = nullptr;
    CoCreateInstance(kClsidNullRenderer, nullptr, CLSCTX_INPROC_SERVER, IID_IBaseFilter, (void**)&nullF);
    if (!graph || !grabF || !nullF) {
        std::printf("RESULTADO error=sin-grafo graph=%p grabber=%p null=%p\n", (void*)graph, (void*)grabF, (void*)nullF);
        return 4;
    }
    graph->AddFilter(src, L"camara");
    graph->AddFilter(grabF, L"grabber");
    graph->AddFilter(nullF, L"null");

    // Pin de salida de la cámara y formato pedido (como hace OBS con "Personalizado").
    IEnumPins* pins = nullptr;
    IPin* out = nullptr;
    src->EnumPins(&pins);
    if (pins) {
        pins->Next(1, &out, nullptr);
        pins->Release();
    }
    if (!out) {
        std::printf("RESULTADO error=sin-pin\n");
        return 5;
    }
    IAMStreamConfig* cfg = nullptr;
    int capCount = 0, capSize = 0;
    if (SUCCEEDED(out->QueryInterface(IID_IAMStreamConfig, (void**)&cfg))) {
        cfg->GetNumberOfCapabilities(&capCount, &capSize);
        if (w > 0 && h > 0) {
            std::vector<BYTE> caps(static_cast<size_t>(capSize));
            for (int i = 0; i < capCount; i++) {
                AM_MEDIA_TYPE* mt = nullptr;
                if (cfg->GetStreamCaps(i, &mt, caps.data()) == S_OK && mt) {
                    auto* vih = reinterpret_cast<VIDEOINFOHEADER*>(mt->pbFormat);
                    const bool match = mt->subtype == MEDIASUBTYPE_NV12 && vih->bmiHeader.biWidth == w &&
                                       std::abs(vih->bmiHeader.biHeight) == h;
                    if (match) std::printf("SetFormat %dx%d -> 0x%08lx\n", w, h, cfg->SetFormat(mt));
                    if (mt->pbFormat) CoTaskMemFree(mt->pbFormat);
                    CoTaskMemFree(mt);
                    if (match) break;
                }
            }
        }
    }

    ISampleGrabber* sg = nullptr;
    grabF->QueryInterface(IID_ISampleGrabber, (void**)&sg);
    AM_MEDIA_TYPE want_mt = {};
    want_mt.majortype = MEDIATYPE_Video;
    want_mt.subtype = MEDIASUBTYPE_NV12;
    sg->SetMediaType(&want_mt);
    Grabber cb;
    sg->SetBufferSamples(FALSE);
    sg->SetOneShot(FALSE);
    sg->SetCallback(&cb, 1);

    IEnumPins* gp = nullptr;
    IPin* gin = nullptr;
    IPin* gout = nullptr;
    grabF->EnumPins(&gp);
    IPin* p = nullptr;
    while (gp && gp->Next(1, &p, nullptr) == S_OK) {
        PIN_DIRECTION d;
        p->QueryDirection(&d);
        if (d == PINDIR_INPUT && !gin) gin = p;
        else if (d == PINDIR_OUTPUT && !gout) gout = p;
        else p->Release();
    }
    if (gp) gp->Release();
    IEnumPins* np = nullptr;
    IPin* nin = nullptr;
    nullF->EnumPins(&np);
    if (np) {
        np->Next(1, &nin, nullptr);
        np->Release();
    }
    TRACE("conectar\n");
    HRESULT hr = graph->ConnectDirect(out, gin, nullptr);
    if (FAILED(hr)) hr = graph->Connect(out, gin);
    if (FAILED(hr)) {
        std::printf("RESULTADO error=conectar hr=0x%08lx\n", hr);
        return 6;
    }
    hr = graph->Connect(gout, nin);
    if (FAILED(hr)) {
        std::printf("RESULTADO error=conectar-null hr=0x%08lx\n", hr);
        return 7;
    }
    AM_MEDIA_TYPE cmt = {};
    sg->GetConnectedMediaType(&cmt);
    if (cmt.formattype == FORMAT_VideoInfo && cmt.pbFormat) {
        auto* vih = reinterpret_cast<VIDEOINFOHEADER*>(cmt.pbFormat);
        cb.width = vih->bmiHeader.biWidth;
        cb.height = std::abs(vih->bmiHeader.biHeight);
        cb.nv12 = cmt.subtype == MEDIASUBTYPE_NV12;
    }
    if (cmt.pbFormat) CoTaskMemFree(cmt.pbFormat);

    IMediaControl* mc = nullptr;
    graph->QueryInterface(IID_IMediaControl, (void**)&mc);
    TRACE("conectado %dx%d nv12=%d; run\n", cb.width, cb.height, (int)cb.nv12);
    hr = mc->Run();
    TRACE("run hr=0x%08lx\n", hr);
    const DWORD t0 = GetTickCount();
    while (cb.frames < want && GetTickCount() - t0 < static_cast<DWORD>(want) * 200 + 5000) Sleep(10);
    TRACE("stop (imagenes=%d)\n", (int)cb.frames);
    mc->Stop();
    TRACE("detenido\n");
    const int got = cb.frames;
    double fps = 0;
    if (cb.times.size() >= 2) fps = (cb.times.size() - 1) * 1000.0 / std::max<DWORD>(1, cb.times.back() - cb.times.front());
    int changes = 0;
    for (size_t i = 1; i < cb.leftLuma.size(); i++)
        if (std::fabs(cb.leftLuma[i] - cb.leftLuma[i - 1]) > 0.5) changes++;
    std::printf("RESULTADO imagenes=%d fps=%.1f ancho=%d alto=%d bytes=%ld cambios=%d luma0=%.1f lumaN=%.1f caps=%d run=0x%08lx\n",
                got, fps, cb.width, cb.height, static_cast<long>(cb.bytes), changes,
                cb.leftLuma.empty() ? -1.0 : cb.leftLuma.front(), cb.leftLuma.empty() ? -1.0 : cb.leftLuma.back(), capCount, hr);
    return got >= want ? 0 : 8;
}

}  // namespace

int wmain(int argc, wchar_t** argv) {
    CoInitializeEx(nullptr, COINIT_MULTITHREADED);
    if (argc < 2) {
        std::printf("uso: vcamtest list | feed <n> <seg> [w h fps] | grab <n> <imagenes> [w h]\n");
        return 1;
    }
    std::wstring cmd = argv[1];
    auto arg = [&](int i, int def) { return argc > i ? _wtoi(argv[i]) : def; };
    int rc = 1;
    if (cmd == L"list") rc = List();
    else if (cmd == L"feed") rc = Feed(arg(2, 1), arg(3, 10), arg(4, 1920), arg(5, 1080), arg(6, 30));
    else if (cmd == L"grab") rc = Grab(arg(2, 1), arg(3, 60), arg(4, 0), arg(5, 0));
    std::fflush(stdout);
    CoUninitialize();
    return rc;
}
