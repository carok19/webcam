using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using CamaraNdi.Core;
using Microsoft.Win32;

namespace ReceptorNdi;

/// <summary>
/// Instala las cámaras virtuales "Cámara NDI - Celular 1..4" (como hace DroidCam con su cámara).
/// Primero intenta sólo para este usuario (sin pedir nada); si Windows no las muestra así, se instalan para
/// todo el PC con regsvr32, que pide permiso de administrador una sola vez.
/// </summary>
internal static class VCamInstaller
{
    // CLSID de "Cámara NDI - Celular 1" (ver virtualcam/src/vcam_ids.h).
    private const string Cam1Clsid = "{C719E32F-5265-4841-8A31-3C94B32BA321}";

    public static string DllPath => Path.Combine(AppContext.BaseDirectory, "camara-virtual", "CamaraVirtual.dll");
    public static bool DllPresent => File.Exists(DllPath);

    private static IntPtr _lib;

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int VisibleCountFn();

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int DllInstallFn(int install, [MarshalAs(UnmanagedType.LPWStr)] string cmdLine);

    private static T Export<T>(string name) where T : Delegate
    {
        if (_lib == IntPtr.Zero) _lib = NativeLibrary.Load(DllPath);
        return Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(_lib, name));
    }

    /// <summary>Cuántas de nuestras cámaras ve DirectShow ahora (lo mismo que ve OBS).</summary>
    public static int VisibleCount()
    {
        if (!DllPresent) return 0;
        try
        {
            return Export<VisibleCountFn>("VCam_VisibleCount")();
        }
        catch (Exception ex)
        {
            Log.Warn("No se pudo consultar las cámaras virtuales: " + ex.Message);
            return 0;
        }
    }

    /// <summary>Ruta de la DLL registrada en esa raíz (null si no está registrada).</summary>
    private static string? RegisteredPath(RegistryKey root, string prefix)
    {
        try
        {
            using var k = root.OpenSubKey($@"{prefix}CLSID\{Cam1Clsid}\InprocServer32");
            return k?.GetValue(null) as string;
        }
        catch
        {
            return null;
        }
    }

    private static bool IsHere(string? path)
    {
        try
        {
            return path != null && string.Equals(Path.GetFullPath(path), Path.GetFullPath(DllPath), StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Instaladas, apuntando a ESTA carpeta (si se movió la carpeta hay que registrar de nuevo) y visibles.
    /// HKCR es la vista combinada: si hay registro del usuario, manda ese.
    /// </summary>
    public static bool IsInstalledHere() => IsHere(RegisteredPath(Registry.ClassesRoot, "")) && VisibleCount() >= 4;

    /// <summary>¿Están registradas para todo el PC apuntando a esta carpeta?</summary>
    public static bool MachineWideHere() => IsHere(RegisteredPath(Registry.LocalMachine, @"SOFTWARE\Classes\"));

    private static bool MachineWideAny() => RegisteredPath(Registry.LocalMachine, @"SOFTWARE\Classes\") != null;

    /// <summary>Registro sólo para este usuario: no pide permiso. Devuelve true si OBS ya las puede ver.</summary>
    public static bool TryInstallPerUser()
    {
        if (!DllPresent) return false;
        try
        {
            int hr = Export<DllInstallFn>("DllInstall")(1, "user");
            Log.Info($"Cámaras virtuales: registro por usuario -> 0x{hr:X8}");
            bool ok = IsInstalledHere();
            if (!ok) Log.Info("Windows no muestra las cámaras registradas sólo para el usuario: hace falta instalarlas para todo el PC");
            return ok;
        }
        catch (Exception ex)
        {
            Log.Warn("No se pudo registrar las cámaras para este usuario: " + ex.Message);
            return false;
        }
    }

    /// <summary>Registro para todo el PC con regsvr32 (Windows pide permiso de administrador).</summary>
    public static bool InstallForAllUsers()
    {
        if (!DllPresent) return false;
        if (!RunElevated($"/s \"{DllPath}\"")) return false;
        bool ok = IsInstalledHere();
        Log.Info(ok ? "Cámaras virtuales instaladas para todo el PC" : "regsvr32 terminó pero Windows no muestra las cámaras");
        return ok;
    }

    /// <summary>Quita las cámaras de Windows (las del usuario y, si las hay, las de todo el PC: eso pide permiso).</summary>
    public static bool Uninstall()
    {
        if (!DllPresent) return false;
        try
        {
            int hr = Export<DllInstallFn>("DllInstall")(0, "user");
            Log.Info($"Cámaras virtuales: quitado el registro del usuario -> 0x{hr:X8}");
        }
        catch (Exception ex)
        {
            Log.Warn("No se pudo quitar el registro del usuario: " + ex.Message);
        }
        bool ok = !MachineWideAny() || RunElevated($"/u /s \"{DllPath}\"");
        Log.Info(ok ? "Cámaras virtuales quitadas de Windows" : "No se pudieron quitar las cámaras de todo el PC");
        return ok;
    }

    private static bool RunElevated(string args)
    {
        try
        {
            var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "regsvr32.exe"), args)
            {
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden,
            };
            using var p = Process.Start(psi);
            if (p == null) return false;
            p.WaitForExit(60_000);
            Log.Info($"regsvr32 {args} -> código {p.ExitCode}");
            return p.ExitCode == 0;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            Log.Info("El usuario no dio permiso para instalar las cámaras");
            return false;
        }
        catch (Exception ex)
        {
            Log.Error("No se pudo ejecutar regsvr32", ex);
            return false;
        }
    }
}
