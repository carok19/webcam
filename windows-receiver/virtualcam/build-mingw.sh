#!/bin/bash
# Compila CamaraVirtual.dll y vcamtest.exe con mingw-w64 (sólo para probar en Linux/Wine; la versión
# que se publica la compila GitHub Actions con Visual Studio, ver CMakeLists.txt).
set -e
cd "$(dirname "$0")"
OUT=${OUT:-build-mingw}
mkdir -p "$OUT"
CXX=x86_64-w64-mingw32-g++
FLAGS="-std=c++17 -O2 -DUNICODE -D_UNICODE -DNDEBUG -include baseclasses/mingw_compat.h -Ibaseclasses -Isrc -Wno-attributes"
OBJS=()
for f in baseclasses/amfilter.cpp baseclasses/amvideo.cpp baseclasses/combase.cpp baseclasses/dllentry.cpp \
         baseclasses/dllsetup.cpp baseclasses/mtype.cpp baseclasses/source.cpp baseclasses/wxdebug.cpp \
         baseclasses/wxlist.cpp baseclasses/wxutil.cpp src/vcam_convert.cpp src/vcam_dll.cpp src/vcam_filter.cpp \
         src/vcam_placeholder.cpp src/vcam_shm.cpp; do
  o="$OUT/$(basename "${f%.cpp}").o"
  $CXX $FLAGS -c "$f" -o "$o"
  OBJS+=("$o")
done
$CXX -shared -o "$OUT/CamaraVirtual.dll" "${OBJS[@]}" src/CamaraVirtual.def -static -static-libgcc -static-libstdc++ \
  -lstrmiids -lole32 -loleaut32 -luuid -lwinmm -lgdi32 -luser32 -ladvapi32
if [ -f tools/vcamtest.cpp ]; then
  $CXX -std=c++17 -O2 -DUNICODE -D_UNICODE -Isrc tools/vcamtest.cpp src/vcam_shm.cpp -o "$OUT/vcamtest.exe" -static \
    -lstrmiids -lole32 -loleaut32 -luuid -ladvapi32 -municode
fi
ls -la "$OUT"/*.dll "$OUT"/*.exe 2>/dev/null
