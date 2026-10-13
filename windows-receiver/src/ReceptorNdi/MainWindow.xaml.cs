using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using CamaraNdi.Core;
using CamaraNdi.Core.Decoding;
using CamaraNdi.Core.Ndi;

namespace ReceptorNdi;

public partial class MainWindow : Window
{
    private readonly App _app = (App)Application.Current;
    private readonly ObservableCollection<SessionViewModel> _cards = new();
    private readonly DispatcherTimer _timer;
    private bool _loading = true;
    private bool _trayHintShown;
    private int _ndiRetryTicks;
    /// <summary>Estado de las cámaras virtuales: checking, ok (todo el PC), ok-user (sólo este usuario), needs-admin,
    /// installing, removed, missing, off.</summary>
    private volatile string _camState = "checking";
    private TimeSpan _lastCpu;
    private DateTime _lastCpuWall;
    private double _cpuPct;

    /// <summary>Uso de CPU del proceso del receptor (todos los núcleos = 100 %).</summary>
    private double PcCpu()
    {
        var now = DateTime.UtcNow;
        var cpu = Process.GetCurrentProcess().TotalProcessorTime;
        if (_lastCpuWall != default)
        {
            double wall = (now - _lastCpuWall).TotalMilliseconds;
            if (wall > 200) _cpuPct = 100.0 * (cpu - _lastCpu).TotalMilliseconds / (wall * Environment.ProcessorCount);
        }
        _lastCpu = cpu;
        _lastCpuWall = now;
        return _cpuPct;
    }

    public MainWindow()
    {
        InitializeComponent();
        Cards.ItemsSource = _cards;
        SessionViewModel.SaveRequested = () => _app.SaveSettings();

        var s = _app.Settings;
        JitterSlider.Value = s.JitterMs;
        JitterText.Text = $"{s.JitterMs} ms";
        UsePhoneMode.IsChecked = s.UsePhoneLatencyMode;
        StartWithWindows.IsChecked = s.StartWithWindows;
        MinimizeToTray.IsChecked = s.MinimizeToTray;
        AutoLower.IsChecked = s.AutoLowerResolution;
        NdiOutput.IsChecked = s.NdiOutput;
        AiEnhance.IsChecked = s.AiEnhance;
        foreach (ComboBoxItem it in DecoderCombo.Items)
            if ((string)it.Tag == s.Decoder) DecoderCombo.SelectedItem = it;
        if (DecoderCombo.SelectedItem == null) DecoderCombo.SelectedIndex = 0;
        _loading = false;

        if (_app.StartupError != null)
        {
            ErrorText.Text = _app.StartupError;
            ErrorText.Visibility = Visibility.Visible;
        }

        SessionViewModel.CameraChangeRequested = (sess, index) => _app.Server?.SetCamera(sess, index);
        if (_app.Server != null)
        {
            _app.Server.SettingsChanged += () => Dispatcher.BeginInvoke(() => _app.SaveSettings());
            _app.Server.SessionAdded += sess => Dispatcher.BeginInvoke(() =>
            {
                if (!_cards.Any(c => c.Session == sess)) _cards.Add(new SessionViewModel(sess));
            });
            _app.Server.SessionRemoved += sess => Dispatcher.BeginInvoke(() =>
            {
                var vm = _cards.FirstOrDefault(c => c.Session == sess);
                if (vm != null) _cards.Remove(vm);
            });
            _app.Server.Notice += msg => Dispatcher.BeginInvoke(() => { ErrorText.Text = msg; ErrorText.Visibility = Visibility.Visible; });
            // Celulares que se conectaron antes de que existiera la ventana (el servidor arranca antes).
            foreach (var sess in _app.Server.Sessions)
                if (!_cards.Any(c => c.Session == sess)) _cards.Add(new SessionViewModel(sess));
            _app.SaveSettings();
        }

        _ = CheckCamerasAsync();
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(500), DispatcherPriority.Background, (_, _) => Refresh(), Dispatcher);
        _timer.Start();
        Refresh();
        StateChanged += OnStateChanged;
        Closing += OnClosing;
    }

    private void Refresh()
    {
        var ips = ReceiverServer.LocalAddresses();
        int port = _app.Server?.MediaPort ?? _app.Settings.MediaPort;
        string ndi = !_app.Settings.NdiOutput ? "NDI: apagado"
            : NdiLib.IsLoaded ? NdiLib.Status
            : "NDI: no instalado (opcional, sólo para mandar a otro PC)";
        StatusText.Text = $"{(VideoDecoder.FfmpegStatus.StartsWith("FFmpeg ") ? "✔ " : "✖ ")}{VideoDecoder.FfmpegStatus}   ·   " +
                          $"Escuchando en UDP {port}   ·   IP de este PC: {string.Join(", ", ips)}   ·   {ndi}";
        UpdateCameraStatus();
        GpuText.Text = "Decodificación: " + CamaraNdi.Core.Decoding.DecoderProbe.Current.Describe() + $"   ·   CPU del PC (receptor): {PcCpu():F0} %";
        JitterSlider.IsEnabled = UsePhoneMode.IsChecked != true;
        JitterText.Text = UsePhoneMode.IsChecked == true ? "lo elige cada celular" : $"{(int)JitterSlider.Value} ms";
        // Si NDI está activado pero falta, volver a buscarlo cada ~10 s (por si lo acaban de instalar).
        if (_app.Settings.NdiOutput && !NdiLib.IsLoaded && ++_ndiRetryTicks % 20 == 0) NdiLib.Retry();
        EmptyHint.Visibility = _cards.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyHintText.Text = "Abre la app Cámara NDI en el celular (misma red WiFi) y pulsa Transmitir. " +
                             "El celular encuentra este PC solo; si no, escribe en el celular una de estas IP: " +
                             string.Join(", ", ips.Select(i => i.Split(' ')[0])) +
                             ". Si Windows pregunta por el Firewall, permite el acceso en redes privadas. " +
                             "En OBS: + (Fuentes) → «Dispositivo de captura de vídeo» → «Cámara NDI - Celular 1».";
        foreach (var c in _cards) c.Update();
        if (LogBox.IsVisible)
        {
            var lines = Log.RecentLines();
            var text = string.Join(Environment.NewLine, lines.Skip(Math.Max(0, lines.Count - 200)));
            if (text != LogBox.Text) { LogBox.Text = text; LogBox.ScrollToEnd(); }
        }
    }

    private void OnJitterChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (JitterText == null) return;
        JitterText.Text = $"{(int)JitterSlider.Value} ms";
        if (_loading) return;
        _app.Settings.JitterMs = (int)JitterSlider.Value;
        _app.SaveSettings();
    }

    private void OnOptionChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var s = _app.Settings;
        s.UsePhoneLatencyMode = UsePhoneMode.IsChecked == true;
        s.MinimizeToTray = MinimizeToTray.IsChecked == true;
        bool autoLower = AutoLower.IsChecked == true;
        if (s.AutoLowerResolution && !autoLower)
        {
            // "Sólo avisar": los celulares a los que se les había pedido menos resolución vuelven a la máxima.
            foreach (var session in _app.Server?.Sessions ?? Array.Empty<PhoneSession>()) session.ClearLimit();
        }
        s.AutoLowerResolution = autoLower;
        s.NdiOutput = NdiOutput.IsChecked == true;
        if (s.AiEnhance != (AiEnhance.IsChecked == true))
        {
            s.AiEnhance = AiEnhance.IsChecked == true;
            if (s.AiEnhance) _ = Task.Run(() => Log.Info("Mejora con IA: " + CamaraNdi.Core.VirtualCam.D3D11VsrScaler.Describe()));
        }
        var decoder = (DecoderCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "auto";
        if (decoder != s.Decoder)
        {
            s.Decoder = decoder;
            // Se aplica en el próximo keyframe de cada celular (sin cortar la transmisión).
            foreach (var session in _app.Server?.Sessions ?? Array.Empty<PhoneSession>()) session.ApplyDecoderSetting();
        }
        if (s.StartWithWindows != (StartWithWindows.IsChecked == true))
        {
            s.StartWithWindows = StartWithWindows.IsChecked == true;
            StartupManager.Apply(s.StartWithWindows);
        }
        _app.SaveSettings();
    }

    private void OnOpenLogs(object sender, RoutedEventArgs e)
    {
        if (Log.Directory != null) Process.Start(new ProcessStartInfo("explorer.exe", Log.Directory) { UseShellExecute = true });
    }

    private void OnLatencyClock(object sender, RoutedEventArgs e) => new LatencyClockWindow().Show();

    // ---------------- Cámaras virtuales ----------------

    private async Task CheckCamerasAsync()
    {
        if (!_app.Settings.VirtualCameras || _app.Server?.VirtualCams == null) { _camState = "off"; return; }
        if (!VCamInstaller.DllPresent) { _camState = "missing"; return; }
        _camState = "checking";
        UpdateCameraStatus();
        // Primero sin pedir permiso (sólo para este usuario); si Windows no las muestra así, se ofrece el botón.
        _camState = await Task.Run(() =>
        {
            bool ok = VCamInstaller.IsInstalledHere() || VCamInstaller.TryInstallPerUser();
            return !ok ? "needs-admin" : VCamInstaller.MachineWideHere() ? "ok" : "ok-user";
        });
        UpdateCameraStatus();
    }

    private async void OnInstallCameras(object sender, RoutedEventArgs e)
    {
        _camState = "installing";
        UpdateCameraStatus();
        bool ok = await Task.Run(() =>
        {
            // También el registro del usuario (si quedó uno viejo apuntando a otra carpeta, manda sobre el del PC).
            VCamInstaller.TryInstallPerUser();
            return VCamInstaller.InstallForAllUsers();
        });
        _camState = ok ? "ok" : await Task.Run(() => VCamInstaller.IsInstalledHere()) ? "ok-user" : "needs-admin";
        UpdateCameraStatus();
        ErrorText.Text = ok ? "" : "No se pudieron instalar las cámaras para todo el PC (¿se canceló el permiso?). Puedes intentarlo de nuevo; los detalles están en el registro.";
        ErrorText.Visibility = ok ? Visibility.Collapsed : Visibility.Visible;
    }

    private async void OnUninstallCameras(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this, "¿Quitar las cámaras «Cámara NDI - Celular 1..4» de Windows?\n\nOBS dejará de verlas. Si vuelves a abrir el receptor, se instalan de nuevo.",
                "Quitar cámaras", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        UninstallCamsButton.IsEnabled = false;
        bool ok = await Task.Run(VCamInstaller.Uninstall);
        UninstallCamsButton.IsEnabled = true;
        _camState = ok ? "removed"
            : await Task.Run(() => VCamInstaller.MachineWideHere() ? "ok" : VCamInstaller.IsInstalledHere() ? "ok-user" : "needs-admin");
        UpdateCameraStatus();
        if (!ok)
        {
            ErrorText.Text = "No se pudieron quitar las cámaras de todo el PC (¿se canceló el permiso?).";
            ErrorText.Visibility = Visibility.Visible;
        }
    }

    private void UpdateCameraStatus()
    {
        (string text, string color) = _camState switch
        {
            "ok" or "ok-user" => ("✔ Cámaras para OBS listas: en OBS agrega «Dispositivo de captura de vídeo» y elige «Cámara NDI - Celular 1» (2, 3, 4…). " +
                     "Para 4K: en sus propiedades, Resolución/FPS «Personalizado» → 3840x2160.", "#81C995"),
            "needs-admin" => ("", "#FFD180"),
            "installing" => ("Instalando las cámaras para OBS… (acepta el permiso de Windows)", "#FFD180"),
            "removed" => ("Cámaras quitadas de Windows. Se instalan de nuevo la próxima vez que abras el receptor.", "#9AA0A6"),
            "missing" => ("✖ Falta la carpeta «camara-virtual» junto al programa: descomprime el zip completo.", "#FF8A80"),
            "off" => ("Cámaras virtuales apagadas.", "#9AA0A6"),
            _ => ("Preparando las cámaras para OBS…", "#9AA0A6"),
        };
        CamStatusText.Text = text;
        CamStatusText.Foreground = (System.Windows.Media.Brush)new System.Windows.Media.BrushConverter().ConvertFromString(color)!;
        CamStatusText.Visibility = text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        // Sin permiso de administrador las cámaras quedan sólo para este usuario; si OBS no las ve (por ejemplo
        // porque se abre "como administrador"), con el botón se instalan para todo el PC.
        bool needs = _camState == "needs-admin";
        VCamPanel.Visibility = needs || _camState == "ok-user" || _camState == "installing" ? Visibility.Visible : Visibility.Collapsed;
        VCamPanelText.Text = needs || _camState == "installing"
            ? "Para que OBS vea los celulares hay que instalar las cámaras (una sola vez)."
            : "¿No aparecen en OBS? (por ejemplo si abres OBS como administrador)";
        VCamPanelText.Foreground = (System.Windows.Media.Brush)new System.Windows.Media.BrushConverter().ConvertFromString(needs ? "#FFD180" : "#9AA0A6")!;
        InstallCamsButton.Content = needs ? "Instalar cámaras para OBS" : "Instalar para todo el PC";
        InstallCamsButton.IsEnabled = _camState != "installing";
        UninstallCamsButton.Visibility = _camState is "ok" or "ok-user" or "needs-admin" ? Visibility.Visible : Visibility.Collapsed;
    }

    private static SessionViewModel? Vm(object sender) => (sender as FrameworkElement)?.Tag as SessionViewModel;

    private void OnRename(object sender, RoutedEventArgs e)
    {
        var vm = Vm(sender);
        if (vm == null) return;
        var dlg = new RenameWindow(vm.Session.NdiName) { Owner = this };
        if (dlg.ShowDialog() == true)
        {
            vm.Session.RequestRename(dlg.Result);
            _app.SaveSettings();
            Log.Info($"Fuente NDI renombrada a \"{vm.Session.NdiName}\" (en OBS hay que elegirla de nuevo)");
        }
    }

    private void OnKeyframe(object sender, RoutedEventArgs e) => Vm(sender)?.Session.RequestKeyframe();

    private void OnClearLimit(object sender, RoutedEventArgs e) => Vm(sender)?.Session.ClearLimit();

    private void OnDisconnect(object sender, RoutedEventArgs e)
    {
        var vm = Vm(sender);
        if (vm == null || _app.Server == null) return;
        if (MessageBox.Show(this, $"¿Desconectar \"{vm.Name}\"? El celular dejará de transmitir y la fuente NDI desaparecerá.",
                "Desconectar", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        _app.Server.Remove(vm.Session);
    }

    private void OnStateChanged(object? sender, EventArgs e)
    {
        if (WindowState == WindowState.Minimized && _app.Settings.MinimizeToTray)
        {
            Hide();
            ShowTrayHintOnce();
        }
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_app.Settings.MinimizeToTray)
        {
            e.Cancel = true;
            Hide();
            ShowTrayHintOnce();
            return;
        }
        _app.ExitApp();
    }

    private void ShowTrayHintOnce()
    {
        if (_trayHintShown) return;
        _trayHintShown = true;
        Log.Info("Ventana oculta: el receptor sigue funcionando en la bandeja del sistema");
    }
}
