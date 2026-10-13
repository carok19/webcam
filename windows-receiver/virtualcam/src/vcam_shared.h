// Memoria compartida entre el receptor (escribe las imágenes de cada celular) y la cámara virtual
// (el filtro DirectShow que corre DENTRO de OBS u otro programa y las lee).
//
// Dos objetos por sesión de Windows:
//   "Local\\CamaraNDI_VCam_Control"   4 KB, lo crea el receptor al abrir: ¿está vivo?, qué celular va
//                                      en cada cámara (para el cartel de espera).
//   "Local\\CamaraNDI_VCam_<n>"        n = 1..VCAM_COUNT, lo crea el receptor cuando asigna un celular a
//                                      esa cámara: 3 búferes NV12 de hasta 3840x2160 + cabecera.
// El receptor C# tiene una copia de estos números en VirtualCam/VCamLayout.cs: si se cambia algo aquí,
// hay que cambiarlo allá (y subir VCAM_VERSION).
#pragma once

#include <stdint.h>

#define VCAM_COUNT 4u
#define VCAM_VERSION 1u

#define VCAM_CONTROL_MAGIC 0x4C54434Eu  /* "NCTL" */
#define VCAM_FRAME_MAGIC 0x56444E43u    /* "CNDV" */

#define VCAM_CONTROL_BYTES 4096u
#define VCAM_HEADER_BYTES 4096u
#define VCAM_SLOTS 3u
#define VCAM_MAX_WIDTH 3840u
#define VCAM_MAX_HEIGHT 2160u
/* 3840 * 2160 * 3 / 2 = 12441600, redondeado a 4096 */
#define VCAM_SLOT_BYTES 12443648u
#define VCAM_TOTAL_BYTES (VCAM_HEADER_BYTES + VCAM_SLOTS * VCAM_SLOT_BYTES)

/* Formato de las imágenes en la memoria compartida: NV12, rango limitado BT.709 (lo que espera OBS). */
#define VCAM_FORMAT_NV12 1u

/* Estado de cada cámara según el receptor */
#define VCAM_STATE_EMPTY 0u      /* ningún celular asignado */
#define VCAM_STATE_WAITING 1u    /* celular asignado, sin video ahora (en espera, sin señal) */
#define VCAM_STATE_LIVE 2u       /* llegan imágenes */

#define VCAM_NAME_CHARS 64u

#pragma pack(push, 8)

typedef struct VCamControlCamera {
    volatile uint32_t state;              /* VCAM_STATE_* */
    uint32_t reserved;
    uint16_t name[VCAM_NAME_CHARS];       /* UTF-16 terminado en 0: nombre del celular */
} VCamControlCamera;                       /* 136 bytes */

typedef struct VCamControl {
    uint32_t magic;                        /* VCAM_CONTROL_MAGIC (se escribe al final de inicializar) */
    uint32_t version;
    uint32_t cameraCount;
    uint32_t receiverPid;
    volatile int64_t receiverHeartbeatMs;  /* GetTickCount64() del receptor; viejo = receptor cerrado */
    VCamControlCamera cameras[VCAM_COUNT];
} VCamControl;

typedef struct VCamSlot {
    volatile uint64_t seq;                 /* 0 = vacío o escribiéndose; si no, número de imagen */
    uint32_t width;
    uint32_t height;
    uint32_t format;                       /* VCAM_FORMAT_NV12 */
    uint32_t reserved;
    int64_t timestampMs;                   /* GetTickCount64() cuando el receptor la escribió */
} VCamSlot;                                /* 32 bytes */

typedef struct VCamHeader {
    uint32_t magic;                        /* VCAM_FRAME_MAGIC (se escribe al final de inicializar) */
    uint32_t version;
    uint32_t headerBytes;
    uint32_t slotCount;
    uint32_t slotBytes;
    uint32_t maxWidth;
    uint32_t maxHeight;
    uint32_t reserved0;
    /* ---- escribe el receptor ---- */
    volatile int64_t writerHeartbeatMs;
    volatile uint32_t writerPid;
    volatile uint32_t latestSlot;
    volatile uint64_t latestSeq;           /* 0 = todavía ninguna imagen */
    uint64_t reserved1;
    VCamSlot slots[VCAM_SLOTS];
    /* ---- escribe la cámara virtual (el programa que la está usando) ---- */
    volatile int64_t readerHeartbeatMs;    /* GetTickCount64() mientras un programa la usa */
    volatile uint32_t wantWidth;           /* formato que eligió ese programa: el receptor escala a esto */
    volatile uint32_t wantHeight;
    volatile uint32_t wantFps100;          /* fps x 100 */
    volatile uint32_t readerPid;           /* identificador del lector que hizo el pedido (pid y número de instancia) */
} VCamHeader;

#pragma pack(pop)

#ifdef __cplusplus
#include <cstddef>
static_assert(sizeof(VCamControlCamera) == 136, "VCamControlCamera");
static_assert(offsetof(VCamControl, receiverHeartbeatMs) == 16, "VCamControl.receiverHeartbeatMs");
static_assert(offsetof(VCamControl, cameras) == 24, "VCamControl.cameras");
static_assert(sizeof(VCamControl) <= VCAM_CONTROL_BYTES, "VCamControl size");
static_assert(sizeof(VCamSlot) == 32, "VCamSlot");
static_assert(offsetof(VCamHeader, writerHeartbeatMs) == 32, "writerHeartbeatMs");
static_assert(offsetof(VCamHeader, writerPid) == 40, "writerPid");
static_assert(offsetof(VCamHeader, latestSlot) == 44, "latestSlot");
static_assert(offsetof(VCamHeader, latestSeq) == 48, "latestSeq");
static_assert(offsetof(VCamHeader, slots) == 64, "slots");
static_assert(offsetof(VCamHeader, readerHeartbeatMs) == 160, "readerHeartbeatMs");
static_assert(offsetof(VCamHeader, wantWidth) == 168, "wantWidth");
static_assert(offsetof(VCamHeader, wantHeight) == 172, "wantHeight");
static_assert(offsetof(VCamHeader, wantFps100) == 176, "wantFps100");
static_assert(offsetof(VCamHeader, readerPid) == 180, "readerPid");
static_assert(sizeof(VCamHeader) <= VCAM_HEADER_BYTES, "VCamHeader size");
#endif
