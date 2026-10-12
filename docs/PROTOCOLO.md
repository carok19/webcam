# Protocolo de transporte "CNDI" (versión 1)

Protocolo propio, muy simple, entre la app Android (emisor) y el receptor de Windows.
Todo va por **UDP**. Todos los enteros son **little-endian**.

## Por qué UDP (y no TCP)

* Con TCP, un solo paquete perdido en el WiFi bloquea **todo** lo que viene detrás
  (head-of-line blocking) hasta que se retransmite. En WiFi eso produce tirones de
  200–500 ms, justo lo que queremos evitar.
* Con UDP decidimos nosotros qué hacer con una pérdida:
  1. el receptor pide el paquete perdido (**NACK**) y el celular lo reenvía desde un
     búfer de los últimos ~2 s (en una LAN el reenvío llega en 5–20 ms);
  2. si aun así no llega antes del límite del *jitter buffer* (30–150 ms, ajustable),
     el receptor **descarta** ese frame, congela el último frame bueno y pide un
     **keyframe** inmediato. Nunca se acumula una cola.
* Paquetes de máximo 1200 bytes: nunca hay fragmentación IP (MTU 1500), cosa que
  en WiFi multiplica las pérdidas.

## Puertos

| Puerto | Uso |
|-------:|-----|
| 47810/udp | Medios + control (un solo socket en el receptor para todos los celulares) |
| 47811/udp | Descubrimiento (broadcast) |

## Cabecera común (8 bytes)

| Offset | Tipo | Campo |
|---:|---|---|
| 0 | u16 | `magic` = 0x4E43 (bytes `'C','N'`) |
| 2 | u8  | `version` = 1 |
| 3 | u8  | `type` |
| 4 | u32 | `sessionId` (aleatorio, uno por arranque de la app) |

## Mensajes celular → receptor

### 0x01 HELLO (1 vez por segundo; también es el *keep-alive*)
Cabecera + JSON UTF-8:
```json
{"deviceId":"…","name":"Cel 1","model":"Infinix X6531","codec":"hevc",
 "width":1920,"height":1080,"fps":30,"bitrateKbps":8000,"maxBitrateKbps":12000,
 "latencyMode":"calidad","streaming":true,"thermal":0,"thermalLevel":"ok",
 "batteryPct":87,"batteryTempC":34.5,"charging":true,"encodeMs":18.2,"rttMs":4.1,
 "cpuPct":12.5,"appVersion":"1.0"}
```
`latencyMode`: `minima` (buffer 40 ms), `equilibrada` (70 ms), `calidad` (300 ms + 100 ms de suavizado)
o `maxima` (800 ms + 300 ms de suavizado). El receptor usa el buffer del modo salvo que el usuario fije otro.

### Color
El video viaja tal cual lo produce el codificador del celular. El receptor lee el rango (`color_range`) y la matriz
(`colorspace`) que trae el video; si no los trae, supone BT.709 en HD y detecta rango completo por los valores
de luma (< 8 o > 247). Siempre entrega a NDI rango limitado BT.709.

### 0x02 PING (4 por segundo)
Cabecera + `u32 pingId` + `u64 phoneTimeUs`. El receptor responde PONG con los mismos
valores; el celular calcula el RTT.

### 0x03 BYE
Cabecera. El celular dejó de transmitir.

### 0x04 DISCOVER (al puerto 47811, por broadcast)
Cabecera + JSON `{"deviceId":"…","name":"Cel 1"}`.

### 0x10 VIDEO
| Offset | Tipo | Campo |
|---:|---|---|
| 8  | u32 | `seq` — número de paquete (consecutivo por sesión) |
| 12 | u32 | `frameId` — número de frame codificado |
| 16 | u16 | `fragIdx` |
| 18 | u16 | `fragCount` |
| 20 | u8  | `flags` — bit0 keyframe, bit1 retransmisión |
| 21 | u8  | `codec` — 1 = H.264, 2 = H.265/HEVC |
| 22 | u16 | reservado |
| 24 | u64 | `ptsUs` — momento de captura (reloj del celular, µs) |
| 32 | …   | datos (máx. 1168 bytes) |

El *payload* de un frame es Annex-B (códigos de inicio `00 00 00 01`). Cada keyframe
lleva delante sus parámetros (VPS/SPS/PPS), así el receptor puede empezar a decodificar
en cualquier keyframe.

## Mensajes receptor → celular (al mismo puerto de origen del celular)

| Tipo | Nombre | Contenido |
|---:|---|---|
| 0x81 | HELLO_ACK | JSON `{"receiver":"PC-SALA","ndiName":"Cel 1","jitterMs":60,"accepted":true,"reason":""}` |
| 0x82 | PONG | `u32 pingId` + `u64 phoneTimeUs` |
| 0x83 | NACK | `u16 count` + `count × u32 seq` |
| 0x84 | KEYFRAME_REQ | `u32 motivo` (1 = pérdida, 2 = inicio, 3 = manual) |
| 0x85 | REPORT | ver abajo, cada 500 ms |
| 0x86 | DISCONNECT | JSON `{"reason":"…"}` — el usuario pulsó "Desconectar" |
| 0x87 | DISCOVER_REPLY | JSON `{"receiver":"PC-SALA","port":47810,"version":1}` |
| 0x88 | LIMIT | JSON `{"maxHeight":2160,"maxPixelRate":199065600,"reason":"…"}` — el PC no alcanzó a decodificar: usar un modo con altura ≤ `maxHeight` y ancho × alto × fps ≤ `maxPixelRate`. Los dos en 0 = se quitó el límite |

`maxPixelRate` es el 80 % de los píxeles por segundo que el PC no alcanzó (4K30 → 1440p30 o 1080p60;
1440p30 → 1080p30, nunca 1080p60, que pesa más). El receptor manda LIMIT sólo con sobrecarga sostenida
(≈2 s) y como mucho una vez cada 15 s; las pérdidas de la red no cuentan como sobrecarga. El límite se quita
con "Quitar límite" o al desmarcar "Bajar resolución si este PC no alcanza" (LIMIT con todo en 0), cuando la app
del celular se reinicia (sesión nueva), o en el celular al elegir una resolución a mano.

HELLO_ACK incluye además `"decode":{"probed":true,"hevcGpuMaxHeight":2160,"h264GpuMaxHeight":2160,"maxHeight":0,"maxPixelRate":0,"gpu":"NVIDIA …"}`:
lo que decodifica la GPU del PC (0 = no puede; con "Decodificar con: CPU" siempre 0) y el límite vigente
(0 = ninguno). El celular aplica el límite del HELLO_ACK sólo cuando cambia (el ACK se repite cada segundo).
En automático elige la mayor resolución que sostiene (con sus fps máximos) dentro del límite, y el códec que la
GPU decodifica a esa altura (H.265 primero; si la GPU no puede con ninguno, H.264, que la CPU decodifica más rápido).
Si la GPU falla de verdad con una resolución, el receptor anuncia desde entonces la altura estándar anterior.

En REPORT, `framesDropped` cuenta sólo lo que perdió la red: los frames que se saltan porque el PC no alcanza
a decodificar no se informan (bajar el bitrate no lo arreglaría y sólo quitaría nitidez).

### REPORT (0x85)
| Offset | Tipo | Campo |
|---:|---|---|
| 8  | u32 | `intervalMs` |
| 12 | u32 | `expectedPackets` |
| 16 | u32 | `missingPackets` (huecos detectados, antes de reenviar) |
| 20 | u32 | `lostPackets` (no recuperados a tiempo) |
| 24 | u32 | `framesOutput` |
| 28 | u32 | `framesDropped` |
| 32 | u16 | `jitterMs × 10` |
| 34 | u16 | `receiverLatencyMs × 10` (llegada del 1er paquete → entrega a NDI) |
| 36 | u32 | `suggestedBitrateKbps` (0 = sin sugerencia) |
| 40 | u32 | `rxBitrateKbps` |

## Bitrate adaptativo (en el celular)

Cada REPORT:
* `pérdida = missing / expected`. Si > 4 % o el RTT sube > 40 ms sobre su mínimo, o el
  receptor sugiere menos → bitrate × 0,75.
* Si el bitrate ya está en el mínimo y sigue la congestión 2 s → baja fps (60 → 30 → 24).
* Si sigue 4 s más → baja resolución (4K → 1440p → 1080p → 720p).
* Con pérdida < 1 % durante 5 s → bitrate × 1,08 hasta el máximo; luego, tras 10 s
  estables, recupera fps; tras 20 s, resolución.
* La temperatura pone un **techo** adicional (ver `README.md`).
