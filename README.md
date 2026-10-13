# Cámara NDI — el celular como cámara para OBS

Sistema tipo DroidCam / NDI HX Camera, pero simple: **imagen natural y fluida**, con el retraso que tú elijas
(desde ~0,1 s hasta ~1 s a cambio de más calidad). Como DroidCam: **sólo el APK y el .exe**, no hay que instalar
NDI Tools ni plugins en OBS.

```
 Celular Android ──WiFi (UDP)──▶ Receptor Windows (.exe) ──▶ OBS: «Dispositivo de captura de vídeo»
 Camera2 → H.265/H.264 por hardware   decodifica (GPU)        «Cámara NDI - Celular 1», «… 2», «… 3»
```

## 0. Cómo se conecta todo (en palabras simples)

Son **dos piezas** (y OBS) y cada una hace una sola cosa:

| Pieza | Dónde se instala | Qué hace |
|---|---|---|
| **App "Cámara NDI"** | En cada celular | Toma el video de la cámara, lo comprime (H.265/H.264) y lo manda por el WiFi al PC. |
| **Receptor Cámara NDI** (`ReceptorCamaraNDI.exe`) | En el PC con OBS (se descomprime el zip y listo) | Recibe el video de los celulares, recupera los paquetes que se pierden en el WiFi, lo descomprime con la tarjeta gráfica, **corrige el rango de color** y lo entrega a las **cámaras "Cámara NDI - Celular 1, 2, 3, 4"**, que OBS (y cualquier programa) ve como webcams. |
| **OBS** | En el PC | Sin plugins: *Fuentes → + → Dispositivo de captura de vídeo* → «Cámara NDI - Celular 1». |

```
 Celular ──WiFi──▶ Receptor (.exe) ──memoria compartida──▶ «Cámara NDI - Celular 1» ──▶ OBS ──▶ YouTube / Facebook / grabación
                   corrige color,      (dentro del PC, sin                (como una webcam)
                   recupera pérdidas    comprimir de nuevo)
```

* Entre el receptor y OBS la imagen pasa **sin volver a comprimirse** (memoria compartida dentro del mismo PC):
  no pierde calidad ni agrega retraso apreciable. Sólo se trabaja cuando OBS de verdad está mirando la cámara.
* Cada celular recibe **su** cámara y la conserva: el primero que se conecta queda en «Celular 1», el segundo en
  «Celular 2»… y la próxima vez cada uno vuelve a la suya. En la tarjeta del celular se puede cambiar.
* El orden para abrirlos no importa. Si el receptor está cerrado o el celular no transmite, la cámara muestra un
  cartel ("Abre el Receptor…" / "Esperando video del celular…") en vez de quedar negra; cuando vuelve el video,
  OBS sigue en la misma fuente sin tocar nada.
* **¿Y NDI?** Ya no hace falta. Queda como opción (casilla "Publicar también por NDI") sólo para mandar los
  celulares a **otro PC** por la red; para eso sí se necesita el NDI Runtime en los dos PC. En el mismo PC la
  cámara es igual o mejor: no hay una compresión extra y gasta menos CPU.

* `android-app/` — app Android en Kotlin (Camera2 + MediaCodec por Surface, servicio en primer plano).
* `windows-receiver/` — receptor en C# / .NET 8 (WPF) con FFmpeg para decodificar; `windows-receiver/virtualcam/`
  es la cámara virtual (C++, DirectShow) que ve OBS.
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
| Color | El receptor lee el **rango** (completo 0–255 o limitado 16–235) y la **matriz** (BT.601/709) del video y lo deja en limitado BT.709, que es lo que OBS espera. Si el celular no lo dice, lo detecta. | Si un video de rango completo se pasa tal cual, **todo lo oscuro se vuelve negro puro** (ver sección 3b). Cuesta ~1 ms por frame. |
| Salida | **Cámaras virtuales DirectShow** ("Cámara NDI - Celular 1..4", una DLL propia) alimentadas por memoria compartida; NDI opcional | OBS las abre como cualquier webcam, sin plugins ni NDI Tools. Si OBS pide otro tamaño (por ejemplo 1080p con un celular en 4K) el receptor lo reduce con promedio exacto 2x2 (o el escalado de FFmpeg) fuera del hilo de video. |
| Descubrimiento | Broadcast UDP (puerto 47811) + IP escrita a mano + recuerda la última IP | Más simple y fiable que mDNS en Windows; funciona en cualquier router doméstico. |

**Presupuesto de latencia estimado** (modo "mínima", 1080p30, WiFi 5 GHz; en "calidad" súmale ~0,3 s y en "máxima" ~0,5–0,8 s): exposición/lectura del sensor ~20–33 ms + ISP ~10 ms
+ codificación 10–25 ms + red 2–10 ms + decodificación GPU 2–6 ms + cámara→OBS ~1 frame + pantalla ~1 frame ≈ **80–140 ms**.
Es una estimación: la cifra real se mide con el método de la sección 7.

## 2. Instalación rápida

### Celular
1. Instala `CamaraNDI.apk` (permite "instalar apps de origen desconocido" cuando lo pida).
2. Ábrela, concede **cámara** y **notificaciones**.
3. En ⚙ Ajustes pon el nombre (ej. "Cel 1") y, recomendado, toca **"Permitir sin restricciones de batería"**.

### PC con Windows 10/11 (64 bits)
1. Descomprime `ReceptorCamaraNDI-win-x64.zip` donde quieras (por ejemplo en `Documentos`) y ejecuta
   `ReceptorCamaraNDI.exe`. No hace falta instalar .NET (va dentro del .exe) ni NDI Tools.
   **No borres ni muevas** las carpetas `ffmpeg` y `camara-virtual` que vienen al lado.
2. Al abrirlo, el receptor instala solo las cámaras «Cámara NDI - Celular 1..4» para tu usuario de Windows
   (arriba dice **"✔ Cámaras para OBS listas"**). Si Windows no las muestra así, aparece el botón
   **"Instalar cámaras para OBS"**: púlsalo y acepta el permiso de administrador (**una sola vez**).
   Si mueves la carpeta a otro lugar, el receptor las vuelve a registrar al abrirlo.
3. Cuando Windows pregunte por el Firewall, permite **redes privadas**. Si los celulares no aparecen,
   ejecuta `abrir-firewall.bat` como administrador y comprueba que tu WiFi esté como **red privada**.

### OBS
1. *Fuentes → + → **Dispositivo de captura de vídeo*** → nombre "Celular 1" → *Dispositivo:* **«Cámara NDI - Celular 1»**.
2. Para usar la resolución completa del celular: *Resolución/Tipo de FPS:* **Personalizado** →
   *Resolución:* **1920x1080** (celular en 1080p) o **3840x2160** (celular en 4K) → *FPS:* **"Igualar FPS de salida"**.
   Con "Predeterminado del dispositivo" la cámara entrega 1920x1080 a 30 fps (si el celular manda 4K, se reduce
   a la mitad con promedio exacto, que se ve muy nítido).
3. *Espacio de color:* **Predeterminado** o **709**; *Rango de color:* **Predeterminado** o **Parcial** (es lo que
   entrega el receptor). Si ves pequeños saltos de movimiento, pon *Almacenamiento en búfer:* **Activar**
   (suma ~2 cuadros de retraso pero el movimiento queda parejo).
4. Repite con «Cámara NDI - Celular 2», «… 3». En la tarjeta de cada celular del receptor dice qué cámara usa y
   si OBS la está usando.
5. ¿Otra app (Zoom, Meet, el navegador)? También ven «Cámara NDI - Celular N» como una webcam más.
6. ¿Mandar los celulares a **otro PC** por la red? Marca "Publicar también por NDI" en el receptor e instala el
   NDI Runtime en los dos PC; en el otro PC usa OBS con el plugin DistroAV (*NDI™ Source*, **Bandwidth: Highest**).

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
| **Rango de color mal interpretado** | Muchos Android graban en rango completo (negro = 0). OBS espera rango limitado (negro = 16). Si nadie convierte, todo lo que está entre 0 y 16 —justo los negros con detalle— se vuelve negro puro. | **Corregido en el receptor** (automático). En la tarjeta de cada celular, la línea **Color** dice qué detectó. Si aun así ves negros aplastados, prueba "Rango de entrada: Completo"; si los ves grises/lavados, "Limitado". |
| **Pocos bits en zonas oscuras** | Con poco bitrate, el codificador "ahorra" en lo oscuro y en lo que se mueve (zoom): la textura se vuelve bloques. | Modo **Calidad** (por defecto) o **Máxima calidad**: más bitrate, VBR, keyframe cada 2 s y tope de cuantización. |
| **Curva de tono del fabricante** | Las cámaras de celular usan una curva con mucho contraste que oscurece las sombras para que la foto "impacte". | Ajustes → Imagen → **Curva neutral sRGB** (sólo si el celular lo permite; la app lo detecta). |
| **Exposición engañada** | Ventanas, focos o una pantalla brillante detrás del púlpito hacen que la cámara baje la exposición y la ropa oscura se hunde. | Ajustes → Imagen → **Exposición según el centro**. Y/o sube **EV** a +0,3 o +0,7 (se recuerda). Para que no "respire" cuando alguien de negro cruza: **mantén pulsado** sobre la cara de quien habla (bloquea exposición y enfoque). |
| **Poca luz** | Con poca luz el sensor mete ruido; la reducción de ruido lo "barre" y se lleva la textura de la tela. | En calidad la app usa la reducción de ruido **de alta calidad** del celular y prefiere **30 fps** (60 fps deja la mitad de luz por cuadro). Lo que más ayuda de verdad: **una luz LED suave de frente** al púlpito. |
| **Pérdidas en el WiFi** | Cada frame perdido obliga a pedir un keyframe, y durante un momento la imagen se ve más pobre. | En calidad el receptor espera hasta 0,3–0,8 s y vuelve a pedir los paquetes perdidos: con 6 % de pérdida simulada, **0 frames perdidos** (ver sección 7). |
| **OBS** | Una resolución de captura más baja que la del celular, o un bitrate de transmisión bajo en YouTube/Facebook. | En el «Dispositivo de captura de vídeo» elige *Personalizado* con la resolución del celular. Para transmitir: 1080p30 a **6000 kbps**, keyframe 2 s. Con x264 agrega en *Opciones x264*: `aq-mode=3` (cuida las escenas oscuras). Con NVENC: preset **P6 / Calidad**, *Tuning: High Quality*, *Multipass: Two passes (quarter)*, *Psycho Visual Tuning* activado. |

### 4K con varios celulares (lo que necesita el PC)

La app manda **la máxima resolución del celular con sus fps máximos** (en el A52s, 4K a 30 fps). Que eso llegue
fluido depende del PC que recibe:

* **La tarjeta gráfica (GPU) es lo que decide.** Al abrir, el receptor prueba qué decodifica la GPU y lo muestra arriba:
  `Decodificación: <GPU>: H.265 4K · H.264 4K`. Los celulares reciben ese dato y eligen solos el códec que la GPU
  decodifica (por ejemplo H.264 si la GPU no tiene H.265 en 4K).
* Para **2–3 celulares en 4K** hace falta una GPU con decodificación H.265 4K: en general NVIDIA GTX 1050 o más nueva,
  Intel de 7.ª generación o más nueva (gráficos HD 630 / UHD), AMD RX 400 o más nueva. Las cámaras para OBS no
  vuelven a comprimir el video (a diferencia de NDI), así que el resto del trabajo es poco: copiar la imagen y, si
  OBS pide 1080p de un celular 4K, reducirla (≈2–3 ms por imagen).
* **Sin GPU compatible**, el receptor decodifica con **todos los núcleos** de la CPU. Un 4K puede andar bien;
  2–3 a la vez, difícilmente.
* **Si el PC no alcanza**, el receptor nunca deja que el retraso crezca (salta frames viejos), lo avisa en la tarjeta
  del celular y —si está marcada la casilla **"Bajar resolución si este PC no alcanza"**— le pide al celular un
  escalón menos de píxeles por segundo para que siga fluido: 4K30 → 1440p30 (o 1080p60), 1440p30 → 1080p30,
  1080p30 → 720p30. Nunca sube los fps a cambio (1440p30 no pasa a 1080p60, que pesa más).
  * Las pérdidas del WiFi **no** cuentan como "PC lento": sólo se baja si de verdad no alcanza a decodificar.
  * Si fue algo pasajero (por ejemplo, Windows actualizando), pulsa **"Quitar límite"** en la tarjeta: el celular
    vuelve a su máxima resolución. También se quita al elegir una resolución a mano en el celular, al reiniciar la
    app del celular o al desmarcar la casilla.
  * Si prefieres mantener 4K aunque vaya a saltos, desmarca la casilla (sólo avisa).
* Retraso esperado: modo **Calidad** ≈ 0,4–0,6 s en total; **Máxima calidad** ≈ 1 s.

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
5. En OBS: «Dispositivo de captura de vídeo» → «Cámara NDI - Celular N» en **Personalizado** con la resolución del
   celular, **709** y **Parcial** (o "Predeterminado").

### Mejora con IA (opcional, experimental)
Casilla **"Mejora con IA de la GPU (NVIDIA RTX, experimental)"** en el receptor. Lo que hay que saber, sin vender humo:

* La única IA que funciona **en tiempo real** en un PC normal para esto es **NVIDIA RTX Video Super Resolution**,
  la misma que usan Chrome, Edge, Firefox, VLC y mpv. Necesita una **GeForce RTX** (serie 20, 30, 40 o 50) con
  controlador 531 o más nuevo, y estar activada en la *NVIDIA App / Panel de control* → *Mejora de video RTX* →
  **Súper resolución** (ahí se elige también el nivel de calidad 1–4).
* **Sólo ayuda cuando la imagen se agranda** (por ejemplo celular en 1080p y OBS en 4K) o para limpiar un poco los
  bloques de compresión. Si el celular ya manda la misma resolución que OBS usa, la IA no puede "inventar" más
  nitidez real: la mejor calidad viene de la resolución del celular, la luz y el bitrate (sección 3b).
* Cambia un poco el aspecto (más definido). Como pediste imagen natural, viene **apagada**: compárala con y sin.
* El receptor **sólo dice "Activa"** cuando el controlador de NVIDIA confirma que la está aplicando a esa cámara
  (la línea "IA:" de la tarjeta). Si está apagada en el panel, si la tarjeta no la aplica o si tarda más de medio
  cuadro, lo explica ahí y usa el escalado normal. NVIDIA no documenta esta función para video que no se muestra
  en pantalla, así que puede que en algunos PC nunca se active: no se pudo probar en una tarjeta RTX real.
* Intel y AMD: no hay una IA equivalente que un programa pueda usar de forma fiable (la de Intel no se puede
  comprobar y la de AMD no es IA), así que no se ofrece.

## 4. Receptor de Windows

* Una tarjeta por celular: miniatura, estado (EN VIVO / En espera / Sin señal / Desconectado), video
  (resolución, fps, códec), bitrate, pérdida (antes y después de reenvíos), latencia estimada, decodificador
  (GPU/CPU), temperatura/batería del celular y una línea **Prueba** (tiempo en vivo, fps
  promedio y mínimo, temperatura máxima, CPU de la app) y una línea **Color** (qué rango/matriz detectó y si
  convirtió), con el selector **Rango de entrada** (Automático / Completo / Limitado).
* **"En OBS: Cámara NDI - Celular N"**: qué cámara usa ese celular (se puede cambiar; si otra estaba ocupada, se
  intercambian) y si OBS la está usando ahora mismo. Cada celular conserva su cámara entre sesiones.
* **Renombrar** (se guarda por celular; es el nombre que muestra el cartel de la cámara y la fuente NDI),
  **Pedir keyframe**, **Desconectar** (el celular pasa a "En espera").
* Opciones: buffer de red 30–1000 ms (si "usar el modo de cada celular" está marcado, lo elige cada celular), usar el modo de latencia de cada celular, decodificar con GPU/CPU,
  **publicar también por NDI** (sólo para otro PC), **mejora con IA de la GPU** (ver arriba), **iniciar con Windows** (minimizado a la bandeja),
  **minimizar a la bandeja**, **quitar cámaras de Windows** (por ejemplo antes de borrar la carpeta).
* Si el celular pierde la conexión, la cámara **no desaparece** de OBS: muestra "Esperando video del celular…" y
  cuando vuelve sigue en la misma fuente.
* Registro: `%LOCALAPPDATA%\ReceptorCamaraNDI\logs\receptor-AAAAMMDD.log` (botón "Abrir registros").
* Botón **"Reloj para medir latencia"** (ver sección 7).

### Cámaras virtuales (cómo funcionan)
* `camara-virtual\CamaraVirtual.dll` es un filtro **DirectShow** propio (C++, sin dependencias: no hace falta el
  runtime de Visual C++). Windows la registra como 4 "dispositivos de captura de video". Cuando OBS abre una, la
  DLL se carga **dentro de OBS** y lee las imágenes que deja el receptor en memoria compartida
  (`Local\CamaraNDI_VCam_N`, 3 búferes con número de secuencia: nunca se lee una imagen a medio escribir).
* Formatos que ofrece: NV12 3840x2160, 2560x1440, 1920x1080 (predeterminado), 1280x720; YUY2 y RGB32 en 1080p/720p;
  de 5 a 60 fps. La cámara le avisa al receptor qué tamaño eligió OBS y el receptor se lo entrega ya en ese tamaño.
* Si el receptor no está abierto, el celular no transmite o la imagen tiene más de 1,5 s, la cámara muestra un
  cartel explicando qué falta (nunca una imagen congelada vieja).
* Registro: primero sólo para tu usuario (sin pedir permiso); con el botón, para todo el PC (`regsvr32`, pide
  administrador). A mano: `regsvr32 camara-virtual\CamaraVirtual.dll` (todo el PC) o
  `regsvr32 /n /i:user camara-virtual\CamaraVirtual.dll` (sólo el usuario); para quitarlas, lo mismo con `/u`.

### DLLs y licencias
* **Cámaras virtuales**: `CamaraVirtual.dll` usa las *DirectShow BaseClasses* de Microsoft (licencia MIT, copia
  tomada del proyecto *softcam*). Ver `LICENCIAS.txt`.
* **NDI (opcional)**: el receptor **no incluye** la librería NDI. Sólo si marcas "Publicar también por NDI" carga
  `Processing.NDI.Lib.x64.dll` del *NDI Runtime* instalado (busca en la carpeta del .exe, `ndi\`,
  `%NDI_RUNTIME_DIR_V6%`, `%NDI_RUNTIME_DIR_V5%` y las rutas típicas de NDI Tools).
* **FFmpeg 6.1**: `ffmpeg\avcodec-60.dll`, `avutil-58.dll`, `swresample-4.dll`, `swscale-7.dll` (paquete NuGet
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
Requisitos: .NET 8 SDK (<https://dotnet.microsoft.com/download/dotnet/8.0>) y, para las cámaras virtuales,
Visual Studio 2022 con "Desarrollo para el escritorio con C++" (trae CMake).
```powershell
cd windows-receiver
.\publish.ps1          # compila CamaraVirtual.dll con CMake y publica el receptor
# → publish\ReceptorCamaraNDI\ReceptorCamaraNDI.exe  y  publish\ReceptorCamaraNDI-win-x64.zip
```
Sólo la cámara: `cmake -S virtualcam -B virtualcam/build -A x64` y `cmake --build virtualcam/build --config Release`.
Prueba en Windows: `virtualcam\tools\ci-test.ps1 -Build virtualcam\build\Release` (la usa GitHub Actions).
Pruebas: `dotnet test windows-receiver/tests/CamaraNdi.Tests` (las de punta a punta necesitan FFmpeg 6.1 en el sistema;
en Linux: `apt install ffmpeg`).

### GitHub Actions
`.github/workflows/compilar.yml` compila el APK, la cámara virtual y el receptor en cada push y los deja como
*artifacts* (pestaña **Actions** → la ejecución → *Artifacts*: `CamaraNDI-apk` y `ReceptorCamaraNDI-win-x64`).
En un Windows real registra las cámaras y las prueba: captura con DirectShow y con FFmpeg (como OBS) en 1080p, 4K y
720p, NV12/YUY2/RGB32, sin receptor (cartel) y la cadena completa celular simulado → receptor → cámara.

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
| 4K H.265 por CPU (máquina de pruebas de 4 núcleos) | Versión 1.0.5: **1 hilo → 11,5 fps**. Ahora: **todos los núcleos → 28–56 fps** |
| "PC lento" simulado (decodifica sólo ~14 fps de un 1080p30) | Antes: la cola crecía hasta 240 frames (**~8–9 s de retraso**, 1 fps). Ahora: retraso máx. **~1 s**, aviso en la tarjeta y pedido de menos píxeles por segundo (1080p30 → 720p30). "Quitar límite" lo deshace y el aviso se borra cuando el PC vuelve a ir a la par |
| 20 % de pérdida en ambos sentidos (720p40, modo mínima), PC que sí alcanza | Versión anterior: confundía la pérdida con "PC lento" y **pedía bajar la resolución**. Ahora: no la baja |
| Decodificación por CPU con 4 hilos + jitter de red de 0–40 ms (modo Calidad) | Antes: salida a tirones (desviación 16 ms, 27 saltos de más de 50 ms). Ahora: **desviación 2,6 ms, ningún salto** |
| "Detener" 1,2 s y volver a "Transmitir" en el celular | Antes: salían al aire imágenes de antes de la pausa (de hace 1,3 s). Ahora: la imagen más vieja tiene ≈0,2 s |
| **Cámara virtual** (DLL compilada para Windows, probada con Wine): registro, lista de dispositivos, captura DirectShow | Aparecen las 4 cámaras; 1080p: 61 imágenes a 30,2 fps con contenido que cambia; 4K pedido por la aplicación: 3840x2160 a ~31 fps |
| Receptor → memoria de la cámara (prueba de punta a punta con el celular simulado en 1080p y "OBS" pidiendo 720p) | 175 imágenes en 5,9 s, todas del tamaño pedido; 4K → 1080p se reduce en ~2,5 ms |
| En **Windows real** (GitHub Actions, en cada versión) | Registro para todo el PC y por usuario, captura con DirectShow y con FFmpeg (como OBS) en 1080p/4K/720p y NV12/YUY2/RGB32, cartel sin receptor, y celular simulado → receptor → cámara. Si algo falla, no se publica la versión. |

No se pudo probar aquí: cámara real del celular, codificador por hardware, OBS en Windows (sí FFmpeg/DirectShow), D3D11VA y la interfaz WPF/Android en pantalla.

### Para llenar con los celulares (método)

| Celular | Resolución elegida | Latencia medida (ms) | CPU app (%) | Temp. tras 30 min (°C) | fps sostenidos |
|---|---|---|---|---|---|
| Infinix Hot 50i | (la que marque la app) | | | | |
| Samsung A52s 5G | (la que marque la app) | | | | |
| Ambos a la vez | | | | | |

* **Latencia (cristal a cristal)**: en el receptor pulsa **"Reloj para medir latencia"**. Apunta el celular a
  ese reloj y pon la fuente «Cámara NDI - Celular 1» de OBS en la misma pantalla. Captura de pantalla (Win+Shift+S): reloj real menos
  reloj visto en OBS = latencia total. 5 capturas, promedio.
* **CPU, temperatura y fps**: deja transmitiendo 30 min (con cargador). La línea **Prueba** de cada tarjeta del
  receptor muestra tiempo en vivo, fps promedio y mínimo, temperatura máxima de batería y CPU de la app
  (sólo el proceso de la app; el codificador por hardware y la cámara corren en procesos del sistema).

## 8. Solución de problemas

| Síntoma | Qué hacer |
|---|---|
| El celular dice "Buscando receptor…" | Firewall (`abrir-firewall.bat`), red privada, mismo WiFi, sin aislamiento de clientes. Escribe la IP que muestra el receptor. |
| En OBS no aparece «Cámara NDI - Celular 1» | Abre el receptor y mira la línea de las cámaras arriba. Si dice "¿No aparecen en OBS?" o "hay que instalar las cámaras", pulsa el botón y acepta el permiso. Luego cierra y abre OBS. Pasa si OBS se abre "como administrador" o si moviste la carpeta del receptor. |
| La cámara en OBS muestra "Abre el Receptor Cámara NDI en este PC" | El receptor está cerrado: ábrelo. |
| La cámara muestra "Sin celular…" o "Esperando video del celular…" | Ningún celular está transmitiendo a esa cámara: pulsa Transmitir en el celular y mira en su tarjeta del receptor qué cámara le tocó ("En OBS: …"). |
| Se ve borroso en OBS con un celular 4K | En la fuente pon *Personalizado* → **3840x2160**. Con "Predeterminado del dispositivo" OBS pide 1080p. |
| "NDI: no instalado" en el receptor | Es normal: NDI ya no hace falta. Sólo se usa si marcas "Publicar también por NDI" (para otro PC). |
| Tirones | Modo "Calidad" o "Máxima calidad" (buffer grande), 5 GHz, PC por cable. Mira "Pérdida" en la tarjeta. |
| El celular quedó en menos resolución ("Este PC no alcanzaba …") | El PC no alcanzó a decodificar. Si fue algo pasajero, pulsa **"Quitar límite"** en la tarjeta (o elige la resolución a mano en el celular). Si vuelve a pasar, el PC no da para esa resolución con tantos celulares: mira la línea "Decodificación" (GPU) arriba. |
| Salida a 1 fps y varios segundos de retraso | Era la versión 1.0.5 con un 4K que la GPU no decodificaba (quedaba en CPU con un solo hilo). Desde la 1.0.6 se usan todos los núcleos, se prueba la GPU al abrir y el retraso nunca se acumula. Mira la línea "Decodificación" arriba y el aviso naranja en la tarjeta. |
| Negros aplastados / ropa oscura como una mancha | Ver sección 3b: mira la línea **Color** de la tarjeta, prueba "Curva neutral", "Exposición según el centro", EV +0,3 y revisa la fuente de OBS (*Rango de color:* Parcial o Predeterminado, *Espacio de color:* 709 o Predeterminado). |
| Negros grises / imagen lavada | En OBS la fuente debe estar en *Rango de color: Parcial* (o Predeterminado), no "Completo". Si ya lo está, en el receptor pon "Rango de entrada: Limitado". |
| Se corta con la pantalla apagada | Permiso "sin restricciones de batería" (Ajustes de la app) y los pasos de Samsung/Infinix de arriba. |
| Imagen invertida | Algunos sensores van montados al revés; la app gira la orientación sola según el sensor. Si aún así, avísalo con el modelo. |
| Celular caliente (indicador rojo) | Quita la funda, ventílalo; la app ya bajó bitrate/fps. |

## 9. Fase 2 (pendiente)
* Audio del celular.
