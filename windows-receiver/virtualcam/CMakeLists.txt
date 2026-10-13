# Cámara virtual para OBS (filtro DirectShow) + herramienta de prueba.
#   cmake -S windows-receiver/virtualcam -B build -A x64
#   cmake --build build --config Release
# Resultado: build/Release/CamaraVirtual.dll y build/Release/vcamtest.exe
cmake_minimum_required(VERSION 3.20)
project(CamaraVirtual LANGUAGES CXX)

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
# Sin dependencia del runtime de Visual C++ (así no hay que instalar nada en el PC de la iglesia).
set(CMAKE_MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>")
# Fuentes en UTF-8: los nombres "Cámara NDI - …" llevan tilde (sin esto MSVC las lee como cp1252).
if(MSVC)
    add_compile_options(/utf-8)
endif()

add_library(baseclasses STATIC
    baseclasses/amfilter.cpp
    baseclasses/amvideo.cpp
    baseclasses/combase.cpp
    baseclasses/dllentry.cpp
    baseclasses/dllsetup.cpp
    baseclasses/mtype.cpp
    baseclasses/source.cpp
    baseclasses/wxdebug.cpp
    baseclasses/wxlist.cpp
    baseclasses/wxutil.cpp
)
target_include_directories(baseclasses PUBLIC baseclasses)
target_compile_definitions(baseclasses PUBLIC UNICODE _UNICODE NDEBUG)
if(MSVC)
    target_compile_options(baseclasses PRIVATE /W1 /wd4996)
endif()

add_library(CamaraVirtual SHARED
    src/vcam_convert.cpp
    src/vcam_dll.cpp
    src/vcam_filter.cpp
    src/vcam_placeholder.cpp
    src/vcam_shm.cpp
    src/CamaraVirtual.def
)
target_include_directories(CamaraVirtual PRIVATE src)
# std::min/std::max (las BaseClasses sí usan las macros min/max de windows.h, por eso sólo aquí).
target_compile_definitions(CamaraVirtual PRIVATE NOMINMAX)
target_link_libraries(CamaraVirtual PRIVATE baseclasses strmiids winmm gdi32 user32 advapi32 ole32 oleaut32 uuid)

add_executable(vcamtest tools/vcamtest.cpp src/vcam_shm.cpp)
target_include_directories(vcamtest PRIVATE src)
target_compile_definitions(vcamtest PRIVATE UNICODE _UNICODE NOMINMAX)
target_link_libraries(vcamtest PRIVATE strmiids ole32 oleaut32 uuid advapi32)
