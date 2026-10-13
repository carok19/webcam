// Identificadores fijos de las cámaras virtuales. NO cambiarlos: OBS guarda en sus escenas qué cámara
// usa cada fuente, y el receptor C# los usa para saber si están instaladas (VirtualCam/VCamInstaller.cs).
#pragma once

#include <windows.h>
#include <guiddef.h>

#include "vcam_shared.h"

// {C719E32F-5265-4841-8A31-3C94B32BA321}  Cámara NDI - Celular 1
// {01255299-1F34-4DAA-B1B7-73E77BB3933C}  Cámara NDI - Celular 2
// {0FE49C3A-1E99-4F95-90C3-EDA987B7CCDE}  Cámara NDI - Celular 3
// {B553D60D-F093-4E01-9A17-69367A8FD23F}  Cámara NDI - Celular 4
extern const GUID CLSID_CamaraNdiVCam[VCAM_COUNT];

// Nombre que se ve en OBS ("Cámara NDI - Celular 1").
const wchar_t* CameraName(int index);  // index 1..VCAM_COUNT
