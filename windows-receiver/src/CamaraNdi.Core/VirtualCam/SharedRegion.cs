using System.ComponentModel;
using System.Runtime.InteropServices;

namespace CamaraNdi.Core.VirtualCam;

/// <summary>Un bloque de memoria compartida (o, en pruebas, memoria común).</summary>
public unsafe interface ISharedRegion : IDisposable
{
    byte* Pointer { get; }
    long Length { get; }
    /// <summary>true si ya existía (lo había creado la cámara u otro receptor).</summary>
    bool Existed { get; }
}

/// <summary>Memoria común: para pruebas en Linux y para cuando la cámara virtual está apagada.</summary>
public sealed unsafe class HeapRegion : ISharedRegion
{
    public HeapRegion(long length)
    {
        Length = length;
        Pointer = (byte*)NativeMemory.AllocZeroed((nuint)length);
    }

    public byte* Pointer { get; private set; }
    public long Length { get; }
    public bool Existed => false;

    public void Dispose()
    {
        if (Pointer != null) NativeMemory.Free(Pointer);
        Pointer = null;
    }
}

/// <summary>
/// Memoria compartida con nombre de Windows ("Local\..."). Se crea con permisos para todos y etiqueta de
/// integridad baja, así un OBS abierto como administrador también puede leerla.
/// </summary>
public sealed unsafe class WindowsSharedRegion : ISharedRegion
{
    private IntPtr _mapping;

    private WindowsSharedRegion(IntPtr mapping, byte* view, long length, bool existed)
    {
        _mapping = mapping;
        Pointer = view;
        Length = length;
        Existed = existed;
    }

    public byte* Pointer { get; private set; }
    public long Length { get; }
    public bool Existed { get; }

    public static WindowsSharedRegion Create(string name, long length)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Memoria compartida con nombre: sólo Windows");
        IntPtr sd = IntPtr.Zero;
        // Todos pueden leer/escribir (mismo usuario con o sin administrador) y nivel de integridad bajo.
        const string sddl = "D:(A;;GA;;;WD)(A;;GA;;;SY)(A;;GA;;;BA)(A;;GA;;;AC)S:(ML;;NW;;;LW)";
        if (!ConvertStringSecurityDescriptorToSecurityDescriptorW(sddl, 1, out sd, IntPtr.Zero)) sd = IntPtr.Zero;
        try
        {
            var sa = new SECURITY_ATTRIBUTES { nLength = Marshal.SizeOf<SECURITY_ATTRIBUTES>(), lpSecurityDescriptor = sd, bInheritHandle = 0 };
            IntPtr h = CreateFileMappingW(new IntPtr(-1), ref sa, PAGE_READWRITE, (uint)(length >> 32), (uint)(length & 0xFFFFFFFF), name);
            int err = Marshal.GetLastWin32Error();
            if (h == IntPtr.Zero) throw new Win32Exception(err, $"CreateFileMapping {name}");
            bool existed = err == ERROR_ALREADY_EXISTS;
            IntPtr view = MapViewOfFile(h, FILE_MAP_ALL_ACCESS, 0, 0, (UIntPtr)(ulong)length);
            if (view == IntPtr.Zero)
            {
                int e = Marshal.GetLastWin32Error();
                CloseHandle(h);
                throw new Win32Exception(e, $"MapViewOfFile {name}");
            }
            return new WindowsSharedRegion(h, (byte*)view, length, existed);
        }
        finally
        {
            if (sd != IntPtr.Zero) LocalFree(sd);
        }
    }

    public void Dispose()
    {
        if (Pointer != null) UnmapViewOfFile((IntPtr)Pointer);
        Pointer = null;
        if (_mapping != IntPtr.Zero) CloseHandle(_mapping);
        _mapping = IntPtr.Zero;
    }

    private const uint PAGE_READWRITE = 0x04;
    private const uint FILE_MAP_ALL_ACCESS = 0xF001F;
    private const int ERROR_ALREADY_EXISTS = 183;

    [StructLayout(LayoutKind.Sequential)]
    private struct SECURITY_ATTRIBUTES
    {
        public int nLength;
        public IntPtr lpSecurityDescriptor;
        public int bInheritHandle;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateFileMappingW(IntPtr hFile, ref SECURITY_ATTRIBUTES attributes, uint protect,
        uint maxSizeHigh, uint maxSizeLow, string name);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr MapViewOfFile(IntPtr mapping, uint access, uint offsetHigh, uint offsetLow, UIntPtr bytes);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool UnmapViewOfFile(IntPtr view);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr mem);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptorW(string sddl, uint revision,
        out IntPtr descriptor, IntPtr size);
}
