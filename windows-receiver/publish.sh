#!/usr/bin/env bash
# Publica el receptor como carpeta portable + zip (funciona en Windows con Git Bash, Linux o macOS con .NET 8 SDK).
set -euo pipefail
cd "$(dirname "$0")"
OUT=publish/ReceptorCamaraNDI
rm -rf publish
dotnet publish src/ReceptorNdi -c Release -r win-x64 -o "$OUT"
(cd publish && zip -qr ReceptorCamaraNDI-win-x64.zip ReceptorCamaraNDI)
echo "Listo: windows-receiver/publish/ReceptorCamaraNDI-win-x64.zip"
