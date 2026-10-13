// Punto de entrada de CamaraVirtual.dll: tabla de clases COM, registro como "dispositivo de captura
// de video" y funciones auxiliares para el receptor.
#include <streams.h>

#include <initguid.h>
#include <olectl.h>

#include <cwchar>
#include <string>

#include "vcam_filter.h"
#include "vcam_ids.h"

DEFINE_GUID(CLSID_CamaraNdiVCam1_, 0xc719e32f, 0x5265, 0x4841, 0x8a, 0x31, 0x3c, 0x94, 0xb3, 0x2b, 0xa3, 0x21);
DEFINE_GUID(CLSID_CamaraNdiVCam2_, 0x01255299, 0x1f34, 0x4daa, 0xb1, 0xb7, 0x73, 0xe7, 0x7b, 0xb3, 0x93, 0x3c);
DEFINE_GUID(CLSID_CamaraNdiVCam3_, 0x0fe49c3a, 0x1e99, 0x4f95, 0x90, 0xc3, 0xed, 0xa9, 0x87, 0xb7, 0xcc, 0xde);
DEFINE_GUID(CLSID_CamaraNdiVCam4_, 0xb553d60d, 0xf093, 0x4e01, 0x9a, 0x17, 0x69, 0x36, 0x7a, 0x8f, 0xd2, 0x3f);

const GUID CLSID_CamaraNdiVCam[VCAM_COUNT] = {CLSID_CamaraNdiVCam1_, CLSID_CamaraNdiVCam2_, CLSID_CamaraNdiVCam3_,
                                              CLSID_CamaraNdiVCam4_};

namespace {

const wchar_t* const kNames[VCAM_COUNT] = {
    L"Cámara NDI - Celular 1",
    L"Cámara NDI - Celular 2",
    L"Cámara NDI - Celular 3",
    L"Cámara NDI - Celular 4",
};

const AMOVIESETUP_MEDIATYPE kPinTypes[] = {
    {&MEDIATYPE_Video, &MEDIASUBTYPE_NV12},
    {&MEDIATYPE_Video, &MEDIASUBTYPE_YUY2},
    {&MEDIATYPE_Video, &MEDIASUBTYPE_RGB32},
};

const AMOVIESETUP_PIN kPins[] = {{
    const_cast<LPWSTR>(L"Captura"),  // nombre
    FALSE,                           // se renderiza
    TRUE,                            // es de salida
    FALSE,                           // puede no haber ninguno
    FALSE,                           // puede haber varios
    &CLSID_NULL,                     // se conecta a filtro
    nullptr,                         // se conecta a pin
    sizeof(kPinTypes) / sizeof(kPinTypes[0]),
    kPinTypes,
}};

const REGFILTER2 kRegFilter = {1, MERIT_DO_NOT_USE, 1, {kPins}};

}  // namespace

// De dllsetup.cpp (BaseClasses): escribe/borra CLSID\{...}\InprocServer32 de cada clase de g_Templates.
STDAPI RegisterAllServers(LPCWSTR fileName, BOOL doRegister);

const wchar_t* CameraName(int index) {
    if (index < 1 || index > static_cast<int>(VCAM_COUNT)) return L"";
    return kNames[index - 1];
}

// Tabla que usan las BaseClasses (dllentry.cpp) para DllGetClassObject / DllCanUnloadNow.
CFactoryTemplate g_Templates[] = {
    {L"Cámara NDI - Celular 1", &CLSID_CamaraNdiVCam1_, &vcam::CVCamFilter::CreateInstance<1>, nullptr, nullptr},
    {L"Cámara NDI - Celular 2", &CLSID_CamaraNdiVCam2_, &vcam::CVCamFilter::CreateInstance<2>, nullptr, nullptr},
    {L"Cámara NDI - Celular 3", &CLSID_CamaraNdiVCam3_, &vcam::CVCamFilter::CreateInstance<3>, nullptr, nullptr},
    {L"Cámara NDI - Celular 4", &CLSID_CamaraNdiVCam4_, &vcam::CVCamFilter::CreateInstance<4>, nullptr, nullptr},
};
int g_cTemplates = sizeof(g_Templates) / sizeof(g_Templates[0]);

namespace {

// Registra (o borra) las cuatro cámaras en la categoría "Dispositivos de captura de video".
// perUser: en HKEY_CURRENT_USER\Software\Classes (no pide administrador); si no, en
// HKEY_LOCAL_MACHINE\Software\Classes (regsvr32 como administrador). Mientras se escribe, HKCR se redirige
// a la clave elegida: si no, Windows escribiría en HKCU cuando la clave ya existe ahí (vista combinada de
// HKCR) y la instalación "para todo el PC" quedaría sólo para el usuario. El "filter mapper" se crea ANTES
// de redirigir, porque con HKCR redirigido COM puede no encontrar sus propias clases.
HRESULT RegisterAll(bool install, bool perUser) {
    wchar_t path[MAX_PATH];
    DWORD len = GetModuleFileNameW(g_hInst, path, MAX_PATH);
    if (len == 0 || len >= MAX_PATH) return E_FAIL;

    HRESULT co = CoInitializeEx(nullptr, COINIT_APARTMENTTHREADED);
    IFilterMapper2* mapper = nullptr;
    HRESULT hr = CoCreateInstance(CLSID_FilterMapper2, nullptr, CLSCTX_INPROC_SERVER, IID_IFilterMapper2,
                                  reinterpret_cast<void**>(&mapper));
    HKEY classes = nullptr;
    bool overridden = false;
    if (SUCCEEDED(hr)) {
        LONG r = RegCreateKeyExW(perUser ? HKEY_CURRENT_USER : HKEY_LOCAL_MACHINE, L"Software\\Classes", 0, nullptr, 0,
                                 KEY_ALL_ACCESS, nullptr, &classes, nullptr);
        if (r == ERROR_SUCCESS) {
            r = RegOverridePredefKey(HKEY_CLASSES_ROOT, classes);
            overridden = r == ERROR_SUCCESS;
        }
        if (r != ERROR_SUCCESS) hr = HRESULT_FROM_WIN32(r);  // HKLM sin ser administrador: acceso denegado
    }

    if (SUCCEEDED(hr)) {
        if (install) hr = RegisterAllServers(path, TRUE);  // CLSID\{...}\InprocServer32 = esta DLL
        for (unsigned i = 0; i < VCAM_COUNT; i++) {
            mapper->UnregisterFilter(&CLSID_VideoInputDeviceCategory, kNames[i], CLSID_CamaraNdiVCam[i]);
            if (install && SUCCEEDED(hr))
                hr = mapper->RegisterFilter(CLSID_CamaraNdiVCam[i], kNames[i], nullptr, &CLSID_VideoInputDeviceCategory,
                                            kNames[i], &kRegFilter);
        }
        if (!install) {
            HRESULT r = RegisterAllServers(path, FALSE);
            if (SUCCEEDED(hr)) hr = r;
        }
    }

    if (overridden) RegOverridePredefKey(HKEY_CLASSES_ROOT, nullptr);
    if (classes) RegCloseKey(classes);
    if (mapper) mapper->Release();
    if (SUCCEEDED(co)) CoUninitialize();
    return hr;
}

}  // namespace

STDAPI DllRegisterServer() { return RegisterAll(true, false); }

STDAPI DllUnregisterServer() { return RegisterAll(false, false); }

// regsvr32 /n /i:user CamaraVirtual.dll  -> registro sólo para este usuario, sin administrador.
STDAPI DllInstall(BOOL install, LPCWSTR cmdLine) {
    const bool perUser = cmdLine && _wcsicmp(cmdLine, L"user") == 0;
    return RegisterAll(install != FALSE, perUser);
}

// ¿Cuántas de nuestras cámaras ve DirectShow ahora mismo? (lo usa el receptor para saber si ya están
// instaladas y si el registro por usuario funcionó en este Windows).
extern "C" int WINAPI VCam_VisibleCount() {
    int found = 0;
    HRESULT co = CoInitializeEx(nullptr, COINIT_MULTITHREADED);
    ICreateDevEnum* devEnum = nullptr;
    if (SUCCEEDED(CoCreateInstance(CLSID_SystemDeviceEnum, nullptr, CLSCTX_INPROC_SERVER, IID_ICreateDevEnum,
                                   reinterpret_cast<void**>(&devEnum)))) {
        IEnumMoniker* monikers = nullptr;
        if (devEnum->CreateClassEnumerator(CLSID_VideoInputDeviceCategory, &monikers, 0) == S_OK && monikers) {
            IMoniker* m = nullptr;
            while (monikers->Next(1, &m, nullptr) == S_OK) {
                IPropertyBag* bag = nullptr;
                if (SUCCEEDED(m->BindToStorage(nullptr, nullptr, IID_IPropertyBag, reinterpret_cast<void**>(&bag)))) {
                    VARIANT v;
                    VariantInit(&v);
                    if (SUCCEEDED(bag->Read(L"CLSID", &v, nullptr)) && v.vt == VT_BSTR) {
                        CLSID id;
                        if (SUCCEEDED(CLSIDFromString(v.bstrVal, &id)))
                            for (unsigned i = 0; i < VCAM_COUNT; i++)
                                if (id == CLSID_CamaraNdiVCam[i]) found++;
                    }
                    VariantClear(&v);
                    bag->Release();
                }
                m->Release();
            }
            monikers->Release();
        }
        devEnum->Release();
    }
    if (SUCCEEDED(co)) CoUninitialize();
    return found;
}

extern "C" BOOL WINAPI DllEntryPoint(HINSTANCE, ULONG, LPVOID);

BOOL APIENTRY DllMain(HANDLE module, DWORD reason, LPVOID reserved) {
    return DllEntryPoint(static_cast<HINSTANCE>(module), reason, reserved);
}
