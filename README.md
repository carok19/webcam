# Cámara NDI — el celular como cámara para OBS

Sistema tipo DroidCam / NDI HX Camera, pero simple: **imagen natural y fluida**, con el retraso que tú elijas
(desde ~0,1 s hasta ~1 s a cambio de más calidad).

```
 Celular Android ──WiFi (UDP)──▶ Receptor Windows (.exe) ──NDI──▶ OBS (DistroAV)
 Camera2 → H.265/H.264 por hardware   decodifica (GPU)     una fuente NDI por celular
```

## 0. Cómo se conecta todo (en palabras simples)

Son **tres piezas** y cada una hace una sola cosa:

| Pieza | Dónde se instala | Qué hace |
|---|---|---|
| **App "Cámara NDI"** | En cada celular | Toma el video de la cámara, lo comprime (H.265/H.264) y lo manda por el WiFi al PC. |
| **Receptor Cámara NDI** (`ReceptorCamaraNDI.exe`) | En el PC con OBS | Recibe el video de los celulares, recupera los paquetes que se pierden en el WiFi, lo descomprime con la tarjeta gráfica, **corrige el rango de color** y publica **una fuente NDI por celular** ("Cel 1", "Cel 2"…). |
| **NDI Tools / NDI Runtime** | En el PC (se instala una vez) | No es un programa que haya que abrir: es la "librería" de NDI que permite que un programa le pase video a otro. El receptor la usa para **publicar** y OBS la usa para **leer**. |
| **OBS + plugin DistroAV** | En el PC | DistroAV agrega a OBS la fuente **"NDI™ Source"**. Ahí eliges `NOMBRE-PC (Cel 1 - …)` y el celular aparece como una cámara más. |

```
 Celular ──WiFi──▶ Receptor (.exe) ──▶ [NDI Runtime] ──▶ OBS: fuente "NDI Source" ──▶ YouTube / Facebook / grabación
                   corrige color,       (va dentro          (plugin DistroAV)
                   recupera pérdidas     del PC)
```

* Todo esto pasa **dentro del mismo PC**: entre el receptor y OBS no hay WiFi de por medio, así que esa parte no
  pierde calidad ni agrega retraso apreciable.
* El orden para abrirlos no importa. Si cierras el receptor, OBS muestra el último cuadro; al volver a abrirlo,
  los celulares se reconectan solos y OBS sigue en la misma fuente.

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
| Buffer de red | 40 ms (mínima) / 70 ms (equilibrado) / **300 ms (calidad, por defecto)** / 800 ms (máxima), ajustable 30–1000 ms | Cuanto más buffer, más veces se puede volver a pedir un paquete perdido antes de rendirse: en los modos de calidad prácticamente nunca se pierde un frame. |
| Códec | H.265 si hay codificador por hardware que sostenga la resolución; si no H.264 (High sin B-frames) | Menos bitrate para la misma calidad = menos congestión en el WiFi. |
| Codificador | Modos rápidos: CBR, keyframe cada 1 s, `KEY_LATENCY=1`. **Modos de calidad**: VBR, keyframe cada 2 s, +60 % / +120 % de bitrate y tope de cuantización (Android 12+). Siempre sin B-frames. | En calidad el codificador pone los bits donde hacen falta (sombras, ropa oscura, zoom) en vez de repartirlos parejo. |
| WiFi | Marca DSCP CS5 (cola de video WMM), WifiLock de baja latencia, socket atado al WiFi | El router prioriza el video; los datos móviles no "roban" la conexión. |
| Decodificación | FFmpeg con D3D11VA (cualquier GPU: NVIDIA, AMD, Intel), luego DXVA2, luego CPU | Sin hilos por frame (cada uno suma 1 frame de retraso). |
| Color | El receptor lee el **rango** (completo 0–255 o limitado 16–235) y la **matriz** (BT.601/709) del video y lo deja en limitado BT.709, que es lo que NDI y OBS esperan. Si el celular no lo dice, lo detecta. | Si un video de rango completo se pasa tal cual, **todo lo oscuro se vuelve negro puro** (ver sección 3b). Cuesta ~1 ms por frame. |
| Salida | Una fuente NDI por celular, envío asíncrono NV12/I420 | Mínimo trabajo de CPU en el PC. |
| Descubrimiento | Broadcast UDP (puerto 47811) + IP escrita a mano + recuerda la última IP | Más simple y fiable que mDNS en Windows; funciona en cualquier router doméstico. |

**Presupuesto de latencia estimado** (modo "mínima", 1080p30, WiFi 5 GHz; en "calidad" súmale ~0,3 s y en "máxima" ~0,5–0,8 s): exposición/lectura del sensor ~20–33 ms + ISP ~10 ms
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
3. Propiedades de la fuente (los nombres pueden variar un poco según la versión de DistroAV):
   * **Bandwidth: Highest** — ¡importante! "Lowest" manda una versión pequeña y borrosa.
   * **YUV Range: Partial** y **YUV Color Space: BT.709** — es lo que manda el receptor. Si aquí pones "Full",
     los negros se ven grises; si el video llegara en rango completo y aquí dice "Partial", los negros se aplastan
     (el receptor ya evita esto último).
   * **Latency mode**: "Normal" si usas los modos de calidad; "Lowest" sólo si usas "Mínima latencia".
4. Repite para cada celular.
5. ¿Otra app que no usa NDI (Zoom, Meet…)? En OBS pulsa **Iniciar cámara virtual**: esa app verá la escena de OBS.

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
**modo (rapidez o calidad)**, **imagen** (curva de tono y medición de la exposición), nombre del dispositivo, IP del receptor (vacío = automático), estabilización electrónica
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
* Modos rápidos: congestión → bitrate ×0,75 (piso = 20 % del máximo) → si sigue 2 s, baja fps (60→30, 30→24/25) →
  si sigue, baja resolución. Con la red limpia 5 s sube el bitrate +8 %/s; luego recupera fps (10 s) y resolución (20 s).
* **Modos de calidad**: como el buffer grande ya absorbe las pérdidas, sólo baja el bitrate (×0,85, piso = 50 %)
  si de verdad se pierden frames, y **nunca baja fps ni resolución por la red**.
* Temperatura: amarillo → techo de bitrate al 75 %; rojo → 50 % y un escalón menos de fps. **Nunca corta la
  transmisión.** Consejo (la app lo muestra al transmitir la primera vez): **sin funda, con cargador y ventilado**.

## 3b. Calidad de imagen: negros, sombras y zoom (guía para la iglesia)

**El problema típico**: alfombra café + pantalón o saco negro → en la transmisión se ve una sola **mancha oscura**,
sin pliegues ni bordes. Tiene varias causas que se suman; esto es lo que hace la app/receptor y lo que conviene ajustar.
Nada de esto es un "filtro": son maneras de no perder información que la cámara sí captó.

| Causa | Qué pasa | Qué se hizo / qué hacer |
|---|---|---|
| **Rango de color mal interpretado** | Muchos Android graban en rango completo (negro = 0). NDI/OBS esperan rango limitado (negro = 16). Si nadie convierte, todo lo que está entre 0 y 16 —justo los negros con detalle— se vuelve negro puro. | **Corregido en el receptor** (automático). En la tarjeta de cada celular, la línea **Color** dice qué detectó. Si aun así ves negros aplastados, prueba "Rango de entrada: Completo"; si los ves grises/lavados, "Limitado". |
| **Pocos bits en zonas oscuras** | Con poco bitrate, el codificador "ahorra" en lo oscuro y en lo que se mueve (zoom): la textura se vuelve bloques. | Modo **Calidad** (por defecto) o **Máxima calidad**: más bitrate, VBR, keyframe cada 2 s y tope de cuantización. |
| **Curva de tono del fabricante** | Las cámaras de celular usan una curva con mucho contraste que oscurece las sombras para que la foto "impacte". | Ajustes → Imagen → **Curva neutral sRGB** (sólo si el celular lo permite; la app lo detecta). |
| **Exposición engañada** | Ventanas, focos o una pantalla brillante detrás del púlpito hacen que la cámara baje la exposición y la ropa oscura se hunde. | Ajustes → Imagen → **Exposición según el centro**. Y/o sube **EV** a +0,3 o +0,7 (se recuerda). Para que no "respire" cuando alguien de negro cruza: **mantén pulsado** sobre la cara de quien habla (bloquea exposición y enfoque). |
| **Poca luz** | Con poca luz el sensor mete ruido; la reducción de ruido lo "barre" y se lleva la textura de la tela. | En calidad la app usa la reducción de ruido **de alta calidad** del celular y prefiere **30 fps** (60 fps deja la mitad de luz por cuadro). Lo que más ayuda de verdad: **una luz LED suave de frente** al púlpito. |
| **Pérdidas en el WiFi** | Cada frame perdido obliga a pedir un keyframe, y durante un momento la imagen se ve más pobre. | En calidad el receptor espera hasta 0,3–0,8 s y vuelve a pedir los paquetes perdidos: con 6 % de pérdida simulada, **0 frames perdidos** (ver sección 7). |
| **OBS** | "Bandwidth: Lowest" en la fuente NDI, o un bitrate de transmisión bajo en YouTube/Facebook. | Fuente NDI en **Highest**. Para transmitir: 1080p30 a **6000 kbps**, keyframe 2 s. Con x264 agrega en *Opciones x264*: `aq-mode=3` (cuida las escenas oscuras). Con NVENC: preset **P6 / Calidad**, *Tuning: High Quality*, *Multipass: Two passes (quarter)*, *Psycho Visual Tuning* activado. |

### Zoom sin perder calidad
* **Haz el zoom en el celular, no en OBS.** En el celular el recorte se hace sobre el sensor completo (12–64 MP),
  así que hasta cierto punto no se pierde nada; en OBS sólo se agranda un video que ya llegó a 1080p.
* La etiqueta del zoom muestra **digital** en blanco mientras el recorte no pierde resolución, y en **naranja** cuando
  ya está ampliando. En Ajustes → Información verás "recorte casi sin pérdida hasta ≈X" para tu celular y resolución
  (en 1080p suele ser ≈2x–2,4x).
* Con zoom, cualquier temblor se nota más: usa trípode. Si no hay trípode, activa la estabilización electrónica
  (en los modos de calidad su pequeño retraso no importa).
* El A52s puede transmitir en 4K: si en OBS quieres "acercar" (recortar) una parte de la imagen, desde 4K puedes
  recortar hasta 2x y seguir teniendo nitidez de 1080p real. Calienta más el celular: úsalo con cargador y sin funda.

### Ajuste recomendado para el culto
1. Celulares en **Calidad** (por defecto) — o **Máxima calidad** si no te importa ~1 s de retraso.
2. **1080p30** (o lo que marque la app como recomendado con 30 fps). En el Hot 50i probablemente sea 1080p30.
3. Ajustes → Imagen: prueba **Curva neutral** y **Exposición según el centro**; compara en OBS con y sin.
4. Antes de empezar: toca la cara de quien predica para enfocar, sube EV si la ropa oscura se ve hundida,
   y **mantén pulsado** para bloquear exposición y enfoque. Bloquea también **BB** (balance de blancos).
5. En OBS: fuente NDI en **Bandwidth: Highest**, **YUV Range: Partial**, **BT.709**.

## 4. Receptor de Windows

* Una tarjeta por celular: miniatura, estado (EN VIVO / En espera / Sin señal / Desconectado), video
  (resolución, fps, códec), bitrate, pérdida (antes y después de reenvíos), latencia estimada, decodificador
  (GPU/CPU), conexiones NDI, temperatura/batería del celular y una línea **Prueba** (tiempo en vivo, fps
  promedio y mínimo, temperatura máxima, CPU de la app) y una línea **Color** (qué rango/matriz detectó y si
  convirtió), con el selector **Rango de entrada** (Automático / Completo / Limitado).
* **Renombrar** (nombre NDI, se guarda por celular), **Pedir keyframe**, **Desconectar** (el celular pasa a
  "En espera").
* Opciones: buffer de red 30–1000 ms (si "usar el modo de cada celular" está marcado, lo elige cada celular), usar el modo de latencia de cada celular, decodificar con GPU/CPU,
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
| Video en **rango completo** (como muchos Android), señalizado y sin señalizar | El receptor entrega negro ≈16 y blanco ≈235 (antes: 0 y 255 → negros aplastados en OBS). Conversión: ~1 ms por frame 1080p |
| 6 % de pérdida en ambos sentidos, modo **mínima** vs **máxima calidad** | Mínima: 13 frames perdidos en ~5 s (≈27 fps). Máxima: **0 frames perdidos, 30 fps**, ~0,3 s más de retraso |

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
| Tirones | Modo "Calidad" o "Máxima calidad" (buffer grande), 5 GHz, PC por cable. Mira "Pérdida" en la tarjeta. |
| Negros aplastados / ropa oscura como una mancha | Ver sección 3b: mira la línea **Color** de la tarjeta, prueba "Curva neutral", "Exposición según el centro", EV +0,3 y revisa la fuente NDI de OBS (Highest, Partial, BT.709). |
| Negros grises / imagen lavada | En OBS la fuente NDI debe estar en "YUV Range: Partial". Si ya lo está, en el receptor pon "Rango de entrada: Limitado". |
| Se corta con la pantalla apagada | Permiso "sin restricciones de batería" (Ajustes de la app) y los pasos de Samsung/Infinix de arriba. |
| Imagen invertida | Algunos sensores van montados al revés; la app gira la orientación sola según el sensor. Si aún así, avísalo con el modelo. |
| Celular caliente (indicador rojo) | Quita la funda, ventílalo; la app ya bajó bitrate/fps. |

## 9. Fase 2 (pendiente)
* Salida como **cámara virtual** propia del receptor (hoy: usar la cámara virtual de OBS).
* Audio del celular.
