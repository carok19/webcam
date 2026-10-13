namespace CamaraNdi.Core.VirtualCam;

/// <summary>
/// Copia en C# de windows-receiver/virtualcam/src/vcam_shared.h: la memoria compartida entre el receptor
/// (escribe) y la cámara virtual que corre dentro de OBS (lee). Si se cambia algo allá, cambiarlo aquí.
/// </summary>
public static class VCamLayout
{
    public const int CameraCount = 4;
    public const uint Version = 1;
    public const uint ControlMagic = 0x4C54434E; // "NCTL"
    public const uint FrameMagic = 0x56444E43;   // "CNDV"

    public const int ControlBytes = 4096;
    public const int HeaderBytes = 4096;
    public const int Slots = 3;
    public const int MaxWidth = 3840;
    public const int MaxHeight = 2160;
    public const int SlotBytes = 12443648; // 3840*2160*3/2 redondeado a 4096
    public const long TotalBytes = HeaderBytes + (long)Slots * SlotBytes;
    public const uint FormatNV12 = 1;

    public const uint StateEmpty = 0;
    public const uint StateWaiting = 1;
    public const uint StateLive = 2;
    public const int NameChars = 64;

    // VCamControl
    public const int CMagic = 0, CVersion = 4, CCameraCount = 8, CReceiverPid = 12, CHeartbeat = 16, CCameras = 24;
    public const int CCameraSize = 136, CCamState = 0, CCamName = 8;

    // VCamHeader
    public const int HMagic = 0, HVersion = 4, HHeaderBytes = 8, HSlotCount = 12, HSlotBytes = 16, HMaxWidth = 20, HMaxHeight = 24;
    public const int HWriterHeartbeat = 32, HWriterPid = 40, HLatestSlot = 44, HLatestSeq = 48, HSlots = 64, HSlotSize = 32;
    public const int SSeq = 0, SWidth = 8, SHeight = 12, SFormat = 16, STimestamp = 24;
    public const int HReaderHeartbeat = 160, HWantWidth = 168, HWantHeight = 172, HWantFps100 = 176, HReaderPid = 180;

    public const string ControlName = @"Local\CamaraNDI_VCam_Control";
    public static string FrameName(int index) => $@"Local\CamaraNDI_VCam_{index}";

    /// <summary>Nombre de la cámara tal como aparece en OBS.</summary>
    public static string CameraName(int index) => $"Cámara NDI - Celular {index}";
}
