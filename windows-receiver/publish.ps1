# Publica el receptor como carpeta portable + zip. Requiere .NET 8 SDK.
$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot
$out = "publish\ReceptorCamaraNDI"
if (Test-Path publish) { Remove-Item publish -Recurse -Force }
dotnet publish src\ReceptorNdi -c Release -r win-x64 -o $out
Compress-Archive -Path $out -DestinationPath "publish\ReceptorCamaraNDI-win-x64.zip"
Write-Host "Listo: windows-receiver\publish\ReceptorCamaraNDI-win-x64.zip"
