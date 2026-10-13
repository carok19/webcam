# Publica el receptor como carpeta portable + zip. Requiere .NET 8 SDK y, para las cámaras virtuales,
# Visual Studio 2022 con "Desarrollo para el escritorio con C++" (trae CMake).
$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot
$out = "publish\ReceptorCamaraNDI"
if (Test-Path publish) { Remove-Item publish -Recurse -Force }
if (Get-Command cmake -ErrorAction SilentlyContinue) {
    cmake -S virtualcam -B virtualcam\build -A x64
    cmake --build virtualcam\build --config Release
} else {
    Write-Warning "No hay CMake: el receptor quedará sin cámaras virtuales para OBS."
}
dotnet publish src\ReceptorNdi -c Release -r win-x64 -o $out
Compress-Archive -Path $out -DestinationPath "publish\ReceptorCamaraNDI-win-x64.zip"
Write-Host "Listo: windows-receiver\publish\ReceptorCamaraNDI-win-x64.zip"
