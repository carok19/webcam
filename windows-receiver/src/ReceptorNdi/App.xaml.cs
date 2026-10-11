using System.IO;
using System.Windows;
using CamaraNdi.Core;
using CamaraNdi.Core.Decoding;
using CamaraNdi.Core.Ndi;

namespace ReceptorNdi;

public partial class App : Application
{
    private static Mutex? _single;
    private TrayIcon? _tray;

    public static string DataDir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ReceptorCamaraNDI");
    public static string SettingsPath => Path.Combine(DataDir, "config.json");

    public ReceiverSettings Settings { get; private set; } = new();
    public ReceiverServer? Server { get; private set; }
    public string? StartupError { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _single = new Mutex(true, "ReceptorCamaraNDI_unico", out bool first);
        if (!first)
        {
            MessageBox.Show("El Receptor Cámara NDI ya está abierto (revisa la bandeja del sistema, junto al reloj).",
                "Receptor Cámara NDI", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        Log.Init(Path.Combine(DataDir, "logs"));
        Log.Info("==== Receptor Cámara NDI iniciado ====");
        DispatcherUnhandledException += (_, ex) =>
        {
            Log.Error("Error no controlado en la interfaz", ex.Exception);
            ex.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, ex) => Log.Error("Error fatal", ex.ExceptionObject as Exception);

        Settings = ReceiverSettings.Load(SettingsPath);
        StartupManager.Apply(Settings.StartWithWindows);

        VideoDecoder.EnsureFfmpeg();
        NdiLib.EnsureLoaded();

        try
        {
            Server = new ReceiverServer(Settings);
        }
        catch (Exception ex)
        {
            StartupError = $"No se pudo abrir el puerto UDP {Settings.MediaPort}/{Settings.DiscoveryPort}. ¿Hay otra copia abierta? ({ex.Message})";
            Log.Error(StartupError, ex);
        }

        var win = new MainWindow();
        MainWindow = win;
        _tray = new TrayIcon(win, ExitApp);
        bool minimized = e.Args.Contains("--minimized") || Settings.StartMinimized;
        if (minimized && Settings.MinimizeToTray) { /* sólo bandeja */ }
        else win.Show();
    }

    public void SaveSettings() => Settings.Save(SettingsPath);

    public void ExitApp()
    {
        try
        {
            SaveSettings();
            Server?.Dispose();
            NdiLib.Shutdown();
        }
        catch (Exception ex) { Log.Error("Error al cerrar", ex); }
        _tray?.Dispose();
        Log.Info("==== Receptor cerrado ====");
        Shutdown();
    }
}
