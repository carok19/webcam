@echo off
REM Abre los puertos UDP del Receptor Camara NDI en el Firewall de Windows.
REM Clic derecho sobre este archivo > "Ejecutar como administrador".
net session >nul 2>&1
if errorlevel 1 (
  echo Este script necesita permisos de administrador.
  echo Clic derecho sobre el archivo y elige "Ejecutar como administrador".
  pause
  exit /b 1
)
netsh advfirewall firewall delete rule name="Receptor Camara NDI" >nul 2>&1
netsh advfirewall firewall add rule name="Receptor Camara NDI" dir=in action=allow protocol=UDP localport=47810,47811 profile=private,domain
netsh advfirewall firewall add rule name="Receptor Camara NDI" dir=in action=allow program="%~dp0ReceptorCamaraNDI.exe" profile=private,domain
echo.
echo Listo. Si tu red WiFi esta marcada como "Publica" en Windows, cambiala a "Privada":
echo Configuracion ^> Red e Internet ^> WiFi ^> (tu red) ^> Tipo de perfil de red: Privada
pause
