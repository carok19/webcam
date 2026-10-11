using CamaraNdi.Core;
using Microsoft.Win32;

namespace ReceptorNdi;

/// <summary>"Iniciar con Windows": entrada en HKCU\...\Run (no requiere administrador).</summary>
public static class StartupManager
{
    private const string Key = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string Name = "ReceptorCamaraNDI";

    public static void Apply(bool enabled)
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(Key, writable: true) ?? Registry.CurrentUser.CreateSubKey(Key);
            if (enabled)
            {
                var exe = Environment.ProcessPath ?? "";
                k.SetValue(Name, $"\"{exe}\" --minimized");
            }
            else if (k.GetValue(Name) != null)
            {
                k.DeleteValue(Name, false);
            }
        }
        catch (Exception ex) { Log.Error("No se pudo cambiar el inicio con Windows", ex); }
    }
}
