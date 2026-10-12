using System.Runtime.InteropServices;

namespace CamaraNdi.Core.Ndi;

/// <summary>
/// Carga dinámica de la librería NDI (Processing.NDI.Lib.x64.dll).
/// No se enlaza en tiempo de compilación: si no está instalada, el receptor sigue
/// funcionando (miniaturas) y avisa al usuario.
/// Orden de búsqueda: carpeta del .exe, carpeta "ndi" junto al .exe,
/// %NDI_RUNTIME_DIR_V6%, %NDI_RUNTIME_DIR_V5%, rutas típicas de NDI Tools.
/// </summary>
public static unsafe class NdiLib
{
    public const uint FourCC_NV12 = 'N' | ('V' << 8) | ('1' << 16) | ('2' << 24);
    public const uint FourCC_I420 = 'I' | ('4' << 8) | ('2' << 16) | ('0' << 24);
    public const uint FourCC_UYVY = 'U' | ('Y' << 8) | ('V' << 16) | ('Y' << 24);
    public const int FrameFormatProgressive = 1;
    public const long TimecodeSynthesize = long.MaxValue;

    [StructLayout(LayoutKind.Sequential)]
    public struct SendCreate
    {
        public byte* p_ndi_name;
        public byte* p_groups;
        public byte clock_video;
        public byte clock_audio;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct VideoFrameV2
    {
        public int xres;
        public int yres;
        public uint FourCC;
        public int frame_rate_N;
        public int frame_rate_D;
        public float picture_aspect_ratio;
        public int frame_format_type;
        public long timecode;
        public byte* p_data;
        public int line_stride_in_bytes;
        public byte* p_metadata;
        public long timestamp;
    }

    private static readonly object Gate = new();
    private static bool _attempted;
    private static IntPtr _lib;

    internal static delegate* unmanaged<byte> Initialize;
    internal static delegate* unmanaged<void> Destroy;
    internal static delegate* unmanaged<byte*> Version;
    internal static delegate* unmanaged<SendCreate*, IntPtr> SendCreateFn;
    internal static delegate* unmanaged<IntPtr, void> SendDestroy;
    internal static delegate* unmanaged<IntPtr, VideoFrameV2*, void> SendVideoV2;
    internal static delegate* unmanaged<IntPtr, VideoFrameV2*, void> SendVideoAsyncV2;
    internal static delegate* unmanaged<IntPtr, uint, int> SendGetNoConnections;

    public static bool IsLoaded { get; private set; }
    public static string Status { get; private set; } = "NDI no cargado";
    public static string? LoadedFrom { get; private set; }

    /// <summary>Volver a buscar NDI (p. ej. el usuario lo acaba de instalar). No hace nada si ya está cargado.</summary>
    public static bool Retry()
    {
        lock (Gate)
        {
            if (IsLoaded) return true;
            _attempted = false;
        }
        return EnsureLoaded();
    }

    private static IEnumerable<string> RegistryEnv(string name)
    {
        var list = new List<string>();
        if (!OperatingSystem.IsWindows()) return list;
        try
        {
            using var hklm = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Session Manager\Environment");
            if (hklm?.GetValue(name) is string a && a.Length > 0) list.Add(Environment.ExpandEnvironmentVariables(a));
            using var hkcu = Microsoft.Win32.Registry.CurrentUser.OpenSubKey("Environment");
            if (hkcu?.GetValue(name) is string b && b.Length > 0) list.Add(Environment.ExpandEnvironmentVariables(b));
        }
        catch { }
        return list;
    }

    private static IEnumerable<string> FindFile(string root, string name, int depth)
    {
        var found = new List<string>();
        try
        {
            if (!System.IO.Directory.Exists(root)) return found;
            var f = Path.Combine(root, name);
            if (File.Exists(f)) found.Add(f);
            if (depth > 0)
                foreach (var d in System.IO.Directory.GetDirectories(root))
                    found.AddRange(FindFile(d, name, depth - 1));
        }
        catch { }
        return found;
    }

    private static readonly HashSet<string> LoggedOnce = new();

    /// <summary>
    /// Si falta NDI se reintenta cada ~10 s: cada aviso se registra una sola vez, para no tapar el
    /// registro (y que la ventana de registro no salte al final todo el tiempo).
    /// </summary>
    private static void LogOnce(string message, bool error = false, Exception? ex = null)
    {
        if (!LoggedOnce.Add(message)) return;
        if (error) Log.Error(message, ex);
        else Log.Warn(message);
    }

    public static bool EnsureLoaded()
    {
        lock (Gate)
        {
            if (_attempted) return IsLoaded;
            _attempted = true;
            if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
            {
                Status = "Sistema no soportado por NDI";
                return false;
            }
            foreach (var path in Candidates())
            {
                try
                {
                    if (!File.Exists(path)) continue;
                    _lib = NativeLibrary.Load(path);
                    LoadedFrom = path;
                    break;
                }
                catch (Exception ex) { LogOnce($"No se pudo cargar {path}: {ex.Message}"); }
            }
            if (_lib == IntPtr.Zero)
            {
                Status = "No se encontró NDI Runtime. Instala \"NDI Tools\" o \"NDI Runtime\" desde https://ndi.video/tools/ (DistroAV también lo instala).";
                LogOnce(Status);
                return false;
            }
            try
            {
                Initialize = (delegate* unmanaged<byte>)NativeLibrary.GetExport(_lib, "NDIlib_initialize");
                Destroy = (delegate* unmanaged<void>)NativeLibrary.GetExport(_lib, "NDIlib_destroy");
                Version = (delegate* unmanaged<byte*>)NativeLibrary.GetExport(_lib, "NDIlib_version");
                SendCreateFn = (delegate* unmanaged<SendCreate*, IntPtr>)NativeLibrary.GetExport(_lib, "NDIlib_send_create");
                SendDestroy = (delegate* unmanaged<IntPtr, void>)NativeLibrary.GetExport(_lib, "NDIlib_send_destroy");
                SendVideoV2 = (delegate* unmanaged<IntPtr, VideoFrameV2*, void>)NativeLibrary.GetExport(_lib, "NDIlib_send_send_video_v2");
                SendVideoAsyncV2 = (delegate* unmanaged<IntPtr, VideoFrameV2*, void>)NativeLibrary.GetExport(_lib, "NDIlib_send_send_video_async_v2");
                SendGetNoConnections = (delegate* unmanaged<IntPtr, uint, int>)NativeLibrary.GetExport(_lib, "NDIlib_send_get_no_connections");
                if (Initialize() == 0)
                {
                    Status = "NDI no pudo iniciar (CPU sin soporte SSE4.2?)";
                    LogOnce(Status, error: true);
                    return false;
                }
                var v = Marshal.PtrToStringUTF8((IntPtr)Version()) ?? "?";
                Status = $"NDI listo ({v})";
                Log.Info($"{Status} desde {LoadedFrom}");
                IsLoaded = true;
                return true;
            }
            catch (Exception ex)
            {
                Status = "La librería NDI encontrada no es compatible: " + ex.Message;
                LogOnce(Status, error: true, ex);
                return false;
            }
        }
    }

    public static void Shutdown()
    {
        lock (Gate)
        {
            if (IsLoaded) { try { Destroy(); } catch { } }
            IsLoaded = false;
        }
    }

    private static IEnumerable<string> Candidates()
    {
        string name = OperatingSystem.IsWindows() ? "Processing.NDI.Lib.x64.dll"
            : OperatingSystem.IsLinux() ? "libndi.so.6" : "libndi.dylib";
        var baseDir = AppContext.BaseDirectory;
        yield return Path.Combine(baseDir, name);
        yield return Path.Combine(baseDir, "ndi", name);
        foreach (var env in new[] { "NDI_RUNTIME_DIR_V6", "NDI_RUNTIME_DIR_V5", "NDI_RUNTIME_DIR_V4" })
        {
            var dir = Environment.GetEnvironmentVariable(env);
            if (!string.IsNullOrEmpty(dir)) yield return Path.Combine(dir, name);
            // Si NDI se instaló con el receptor (o Windows) ya abierto, la variable todavía no llega
            // a este proceso: se lee directo del registro.
            if (OperatingSystem.IsWindows())
                foreach (var d in RegistryEnv(env)) yield return Path.Combine(d, name);
        }
        if (OperatingSystem.IsWindows())
        {
            var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            yield return Path.Combine(pf, "NDI", "NDI 6 Runtime", "v6", name);
            yield return Path.Combine(pf, "NDI", "NDI 6 Tools", "Runtime", name);
            yield return Path.Combine(pf, "NDI", "NDI 5 Runtime", "v5", name);
            yield return Path.Combine(pf, "NDI", "NDI 6 SDK", "Bin", "x64", name);
            yield return Path.Combine(pf, "NDI", "NDI 5 SDK", "Bin", "x64", name);
            yield return Path.Combine(pf, "NewTek", "NDI 4 Runtime", "v4", name);
            // Último recurso: buscar en las carpetas de NDI / NewTek.
            foreach (var root in new[] { Path.Combine(pf, "NDI"), Path.Combine(pf, "NewTek") })
                foreach (var f in FindFile(root, name, 4)) yield return f;
        }
        else if (OperatingSystem.IsLinux())
        {
            yield return "/usr/lib/libndi.so.6";
            yield return "/usr/local/lib/libndi.so.6";
            yield return "/usr/lib/x86_64-linux-gnu/libndi.so.6";
        }
    }
}
