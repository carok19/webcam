# Cámara NDI — el celular como cámara para OBS

Sistema tipo DroidCam / NDI HX Camera, pero simple y enfocado en **latencia mínima y fluidez**:

```
 Celular Android ──WiFi (UDP)──▶ Receptor Windows (.exe) ──NDI──▶ OBS (DistroAV)
 Camera2 → H.265/H.264 por hardware   decodifica (GPU)     una fuente NDI por celular
```

* `android-app/` — app Android en Kotlin (Camera2 + MediaCodec por Surface, servicio en primer plano).
* `windows-receiver/` — receptor en C# / .NET 8 (WPF) con FFmpeg para decodificar y NDI para publicar.
* `docs/PROTOCOLO.md` — el protocolo de red, byte a byte.

Todo en español. Soporta de 2 a 3 celulares a la vez (el límite por defecto es 4).

---

## 1. Decisiones técnicas (y por qué)

| Tema | Decisión | Motivo |
|---|---|---|
| Transporte | **UDP propio** con paquetes de ≤1200 bytes numerados | TCP bloquea todo el flujo cuando se pierde un paquete en el WiFi (tirones de 200–500 ms). Con UDP decidimos nosotros. |
| Pérdidas | **NACK**: el receptor pide el paquete perdido y el celular lo reenvía desde un búfer de ~4000 paquetes | En una LAN el reenvío llega en 5–20 ms, dentro del buffer. Probado: 3 % de pérdida → 0 % sin recuperar. |
| Si aun así falta algo | Se descarta el frame, se congela el último bueno y se pide **keyframe** al instante | Nunca hay cola ni "cámara lenta" para ponerse al día. |
| Jitter buffer | 40 ms (mínima) / 70 ms (equilibrada) / 110 ms (calidad), ajustable 30–150 ms | Sólo se espera si falta un paquete. Sin pérdidas el frame sale en cuanto llega. |
| Códec | H.265 si hay codificador por hardware que sostenga la resolución; si no H.264 (High sin B-frames) | Menos bitrate para la misma calidad = menos congestión en el WiFi. |
| Codificador | CBR, keyframe cada 1 s, `KEY_LATENCY=1`, prioridad tiempo real, sin B-frames | Recuperación rápida y salida inmediata de cada frame. |
| WiFi | Marca DSCP CS5 (cola de video WMM), WifiLock de baja latencia, socket atado al WiFi | El router prioriza el video; los datos móviles no "roban" la conexión. |
| Decodificación | FFmpeg con D3D11VA (cualquier GPU: NVIDIA, AMD, Intel), luego DXVA2, luego CPU | Sin hilos por frame (cada uno suma 1 frame de retraso). |
| Salida | Una fuente NDI por celular, envío asíncrono NV12/I420 (sin conversión de color) | Mínimo trabajo de CPU en el PC. |
| Descubrimiento | Broadcast UDP (puerto 47811) + IP escrita a mano + recuerda la última IP | Más simple y fiable que mDNS en Windows; funciona en cualquier router doméstico. |

**Presupuesto de latencia estimado** (modo "mínima", 1080p30, WiFi 5 GHz): exposición/lectura del sensor ~20–33 ms + ISP ~10 ms
+ codificación 10–25 ms + red 2–10 ms + decodificación GPU 2–6 ms + NDI→OBS ~1 frame + pantalla ~1 frame ≈ **80–140 ms**.
Es una estimación: la cifra real se mide con el método de la sección 7.

## 2. Instalación rápida

### Celular
1. Instala `CamaraNDI.apk` (permite "instalar apps de origen desconocido" cuando lo pida).
2. Ábrela, concede **cámara** y **notificaciones**.
3. En ⚙ Ajustes pon el nombre (ej. "Cel 1") y, recomendado, toca **"Permitir sin restricciones de batería"**.

### PC con Windows 10/11 (64 bits)
1. Instala **NDI Tools** (incluye el *NDI Runtime*) desde <https://ndi.video/tools/>.
   Si ya usas DistroAV en OBS, el runtime ya está instalado.
2. Descomprime `ReceptorCamaraNDI-win-x64.zip` donde quieras y ejecuta `ReceptorCamaraNDI.exe`.
   No hace falta instalar .NET (va dentro del .exe).
3. Cuando Windows pregunte por el Firewall, permite **redes privadas**. Si los celulares no aparecen,
   ejecuta `abrir-firewall.bat` como administrador y comprueba que tu WiFi esté como **red privada**.

### OBS
1. Instala el plugin **DistroAV** (antes "OBS-NDI"): <https://github.com/DistroAV/DistroAV/releases>.
2. *Fuentes → + → NDI™ Source* → elige `NOMBRE-PC (Cel 1 - Infinix …)`.
3. En la fuente: **Latency Mode: Lowest** y **Bandwidth: Highest**. Repite para cada celular.
4. ¿Otra app que no usa NDI (Zoom, Meet…)? En OBS pulsa **Iniciar cámara virtual**: esa app verá la escena de OBS.

## 3. Uso de la app (pantalla)

Horizontal, la vista previa ocupa toda la pantalla y muestra **exactamente** el cuadro 16:9 que se transmite.
Los controles se ocultan a los 4 s; un toque los vuelve a mostrar.

```
 [⚙]            ● EN VIVO · 1080p30 HEVC · 8.0 Mbps · ≈85 ms · ● 36°C · 87%⚡          ●(esquina)
                                                                                    [▦]
 2.0x            ┌──────────────────────────────────────────────────────┐
 digital         │                                                      │        ( ● )  Transmitir
  │              │                vista previa (16:9)                   │
  ●  zoom        │                                                      │         [⟲]  Frontal/trasera
  │              └──────────────────────────────────────────────────────┘
 [⚡] linterna          (0.5x)(1x)(2x)   [AF][AE][BB]   EV ──●── +0.3
```

* **Toque para enfocar**: con los controles ocultos, o en la zona central (60 %) con controles visibles.
  Mantener pulsado = bloquea enfoque y exposición en ese punto (anillo amarillo).
* **Zoom**: pellizco o slider (escala logarítmica). Etiqueta **óptico** cuando estás en un lente real
  (0.5x, 1x, telefoto) y **digital** cuando es recorte del sensor. Se pone **naranja** cuando el recorte
  supera la resolución real del sensor (a partir de ahí sí se pierde nitidez). El zoom lo hace el ISP del
  celular sobre el sensor completo; la app no aplica ningún escalado extra.
* **Chips de lentes** (0.5x / 1x / 2x…): sólo aparecen los que el celular deja usar de verdad.
* **AF / AE / BB**: bloqueo de enfoque, exposición y balance de blancos. **EV**: compensación de exposición.
* **▦ Cuadrícula**: ninguna → tercios → 4x4 → cruz central → marco seguro 90 %. **No se transmite.**
* **Indicador térmico**: verde / amarillo / rojo (estado térmico del sistema + margen previsto + temperatura de batería).
* **Pantalla apagada**: puedes apagarla con el botón de encendido; la transmisión sigue (notificación fija con
  botón *Detener*). O usa **Modo ahorro** (pantalla negra con brillo mínimo; un toque la reactiva), manual o automático.

### Ajustes (panel lateral)
Resolución+fps (sólo las que el celular sostiene; las demás se listan con el motivo), códec, bitrate máximo,
modo de latencia, nombre del dispositivo, IP del receptor (vacío = automático), estabilización electrónica
(avisa que añade latencia; la óptica OIS se usa siempre), atenuación automática de pantalla, permiso de batería
e información del celular (lentes, zoom sin pérdida, codificadores, banda WiFi).

### Qué detecta la app (sin fingir)
* **Resoluciones**: para cada preset (4K30, 1440p30, 1080p60, 1080p30, 720p60, 720p30) comprueba que la cámara
  entregue ese tamaño a esos fps (`StreamConfigurationMap` + rangos de fps de AE) **y** que el codificador por
  hardware lo sostenga (*performance points* de Android 10+ o las tasas medidas por el fabricante). Elige sola
  la más alta que cumpla. Si 4K no está, no aparece y en Ajustes se explica por qué.
* **Lentes**: lista cámaras lógicas y físicas (`getPhysicalCameraIds`). El ultra gran angular de Samsung suele
  estar disponible como zoom 0.5x de la cámara lógica; si hay telefoto dentro de la cámara lógica, aparece su
  chip y la etiqueta "óptico". Las cámaras traseras extra se prueban una vez al abrir la app: si la marca las
  bloquea, se ocultan y se avisa.
* **Ninguno de los dos celulares actuales tiene telefoto**: el zoom es por recorte y así se indica.
* Si la cámara no acepta *vista previa + video* juntos a una resolución, se **prioriza la transmisión** y se avisa.

### Bitrate adaptativo y temperatura
* El receptor envía cada 500 ms pérdidas, frames descartados, jitter y una sugerencia de bitrate.
* Congestión → bitrate ×0,75 (piso = 20 % del máximo) → si sigue 2 s, baja fps (60→30, 30→24/25) → si sigue,
  baja resolución. Con la red limpia 5 s sube el bitrate +8 %/s; luego recupera fps (10 s) y resolución (20 s).
* Temperatura: amarillo → techo de bitrate al 75 %; rojo → 50 % y un escalón menos de fps. **Nunca corta la
  transmisión.** Consejo (la app lo muestra al transmitir la primera vez): **sin funda, con cargador y ventilado**.

## 4. Receptor de Windows

* Una tarjeta por celular: miniatura, estado (EN VIVO / En espera / Sin señal / Desconectado), video
  (resolución, fps, códec), bitrate, pérdida (antes y después de reenvíos), latencia estimada, decodificador
  (GPU/CPU), conexiones NDI, temperatura/batería del celular y una línea **Prueba** (tiempo en vivo, fps
  promedio y mínimo, temperatura máxima, CPU de la app).
* **Renombrar** (nombre NDI, se guarda por celular), **Pedir keyframe**, **Desconectar** (el celular pasa a
  "En espera").
* Opciones: buffer de red 30–150 ms, usar el modo de latencia de cada celular, decodificar con GPU/CPU,
  **iniciar con Windows** (minimizado a la bandeja), **minimizar a la bandeja**.
* Si el celular pierde la conexión, la fuente NDI **no desaparece** de OBS: cuando vuelve, sigue en la misma fuente.
* Registro: `%LOCALAPPDATA%\ReceptorCamaraNDI\logs\receptor-AAAAMMDD.log` (botón "Abrir registros").
* Botón **"Reloj para medir latencia"** (ver sección 7).

### DLLs y licencias
* **NDI**: el receptor **no incluye** la librería NDI; carga `Processing.NDI.Lib.x64.dll` del *NDI Runtime*
  instalado (busca en la carpeta del .exe, `ndi\`, `%NDI_RUNTIME_DIR_V6%`, `%NDI_RUNTIME_DIR_V5%` y las rutas
  típicas de NDI Tools). Para llevarla junto al .exe: copia `Processing.NDI.Lib.x64.dll` desde el **NDI SDK**
  (gratuito, <https://ndi.video/for-developers/ndi-sdk/>, carpeta `Bin\x64`) al lado del .exe. La licencia del SDK
  permite redistribuir esa DLL con tu aplicación siempre que muestres el aviso de marca "NDI®", el enlace
  <https://ndi.video> e incluyas su acuerdo de licencia. Para uso personal basta con NDI Tools.
* **FFmpeg 6.1**: `ffmpeg\avcodec-60.dll`, `avutil-58.dll`, `swresample-4.dll` (paquete NuGet
  `Sdcb.FFmpeg.runtime.windows-x64`, compilación **GPL**). Para uso personal no hay problema; si vas a
  redistribuir, cámbialas por una compilación **LGPL** (p. ej. BtbN `win64-lgpl-shared-6.1`): mismos nombres,
  funciona igual. Detalles en `LICENCIAS.txt`.

## 5. Compilar

### APK (Android Studio o línea de comandos)
Requisitos: JDK 17 y Android SDK (Android Studio lo instala solo).
```bash
cd android-app
./gradlew assembleRelease        # Windows: gradlew.bat assembleRelease
# → app/build/outputs/apk/release/app-release.apk
```
El APK se firma con `android-app/keystore/camarandi.jks` (contraseña `camarandi`), incluida en el repo para que
las actualizaciones se instalen encima sin desinstalar. **No la uses para publicar en Play Store.**

### Receptor (.exe)
Requisito: .NET 8 SDK (<https://dotnet.microsoft.com/download/dotnet/8.0>).
```powershell
cd windows-receiver
.\publish.ps1          # o ./publish.sh
# → publish\ReceptorCamaraNDI\ReceptorCamaraNDI.exe  y  publish\ReceptorCamaraNDI-win-x64.zip
```
Pruebas: `dotnet test windows-receiver/tests/CamaraNdi.Tests` (las de punta a punta necesitan FFmpeg 6.1 en el sistema;
en Linux: `apt install ffmpeg`).

### GitHub Actions
`.github/workflows/compilar.yml` compila el APK y el receptor en cada push y los deja como *artifacts*
(pestaña **Actions** → la ejecución → *Artifacts*: `CamaraNDI-apk` y `ReceptorCamaraNDI-win-x64`).

### Probar sin celular
`windows-receiver/tools/SimSender` es un "celular" simulado (FFmpeg + misma lógica de red):
```bash
dotnet run --project windows-receiver/tools/SimSender -- --host 192.168.1.20 --size 1920x1080 --fps 30 --loss 0.02
```

## 6. Red y ajustes recomendados

* Usa la banda de **5 GHz** (la app avisa si estás en 2.4 GHz). Mejor aún: el PC **por cable** al router.
* Desactiva el **aislamiento de clientes** ("AP isolation") del router: si está activo los celulares no ven el PC.
* Reserva una IP fija para el PC en el router (DHCP) — o escribe la IP en el celular si el descubrimiento falla.
* Ancho de banda: 1080p30 HEVC ≈ 8 Mbps por celular; 3 celulares en 4K ≈ 60–75 Mbps. Un router 5 GHz normal lo aguanta.
* **Samsung**: Ajustes → Batería → Límites de uso en segundo plano → quita "Cámara NDI" de apps en suspensión.
* **Infinix (XOS)**: Ajustes → Batería/Ahorro de energía → gestión de apps en segundo plano → permite "Cámara NDI".

## 7. Tabla de pruebas

### Lo que se verificó en este entorno de desarrollo (sin celulares reales)
Hecho en un contenedor Linux sin acceso a teléfonos ni a Windows, así que **no hay mediciones en los
celulares**. Lo que sí se probó de verdad:

| Prueba | Resultado |
|---|---|
| Receptor (núcleo) ↔ emisor simulado, H.264 720p30, 0 % pérdida | 30 fps sostenidos, ~1–2 ms de proceso en el receptor (decodificación CPU) |
| Igual con 3 % de pérdida artificial | 100 % de paquetes recuperados por NACK, 0 % sin recuperar, ~28–30 fps |
| H.265 720p30 con 2 % de pérdida | 0 % sin recuperar |
| 3 emisores simultáneos (1 % de pérdida c/u) + un 4.º rechazado por el límite | 3 sesiones a ~29–30 fps; el 4.º rechazado con mensaje |
| **Código de red Kotlin real de la app** (compilado para JVM) → receptor C#, H.264 720p30 | Conecta, 30 fps, RTT 0,6 ms, reportes recibidos en el "celular" |
| Lo mismo a través de un proxy con 3 % de pérdida en ambos sentidos | 113 descartados, 112 reenviados por NACK, 0 % sin recuperar |
| Compilación del APK (contra APIs de Android 14) y del .exe (win-x64) | OK |

No se pudo probar aquí: cámara real, codificador por hardware, NDI en Windows, D3D11VA y la interfaz WPF/Android en pantalla.

### Para llenar con los celulares (método)

| Celular | Resolución elegida | Latencia medida (ms) | CPU app (%) | Temp. tras 30 min (°C) | fps sostenidos |
|---|---|---|---|---|---|
| Infinix Hot 50i | (la que marque la app) | | | | |
| Samsung A52s 5G | (la que marque la app) | | | | |
| Ambos a la vez | | | | | |

* **Latencia (cristal a cristal)**: en el receptor pulsa **"Reloj para medir latencia"**. Apunta el celular a
  ese reloj y pon la fuente NDI de OBS en la misma pantalla. Captura de pantalla (Win+Shift+S): reloj real menos
  reloj visto en OBS = latencia total. 5 capturas, promedio.
* **CPU, temperatura y fps**: deja transmitiendo 30 min (con cargador). La línea **Prueba** de cada tarjeta del
  receptor muestra tiempo en vivo, fps promedio y mínimo, temperatura máxima de batería y CPU de la app
  (sólo el proceso de la app; el codificador por hardware y la cámara corren en procesos del sistema).

## 8. Solución de problemas

| Síntoma | Qué hacer |
|---|---|
| El celular dice "Buscando receptor…" | Firewall (`abrir-firewall.bat`), red privada, mismo WiFi, sin aislamiento de clientes. Escribe la IP que muestra el receptor. |
| "✖ No se encontró NDI Runtime" en el receptor | Instala NDI Tools y reinicia el receptor. |
| Tirones | Modo "equilibrada", baja el bitrate máximo, 5 GHz, PC por cable. Mira "Pérdida" en la tarjeta. |
| Se corta con la pantalla apagada | Permiso "sin restricciones de batería" (Ajustes de la app) y los pasos de Samsung/Infinix de arriba. |
| Imagen invertida | Algunos sensores van montados al revés; la app gira la orientación sola según el sensor. Si aún así, avísalo con el modelo. |
| Celular caliente (indicador rojo) | Quita la funda, ventílalo; la app ya bajó bitrate/fps. |

## 9. Fase 2 (pendiente)
* Salida como **cámara virtual** propia del receptor (hoy: usar la cámara virtual de OBS).
* Audio del celular.
