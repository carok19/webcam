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
        foreach (ComboBoxItem it in DecoderCombo.Items)
            if ((string)it.Tag == s.Decoder) DecoderCombo.SelectedItem = it;
        if (DecoderCombo.SelectedItem == null) DecoderCombo.SelectedIndex = 0;
        _loading = false;

        if (_app.StartupError != null)
        {
            ErrorText.Text = _app.StartupError;
            ErrorText.Visibility = Visibility.Visible;
        }

        if (_app.Server != null)
        {
            _app.Server.SessionAdded += sess => Dispatcher.BeginInvoke(() => _cards.Add(new SessionViewModel(sess)));
            _app.Server.SessionRemoved += sess => Dispatcher.BeginInvoke(() =>
            {
                var vm = _cards.FirstOrDefault(c => c.Session == sess);
                if (vm != null) _cards.Remove(vm);
            });
            _app.Server.Notice += msg => Dispatcher.BeginInvoke(() => { ErrorText.Text = msg; ErrorText.Visibility = Visibility.Visible; });
        }

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
        StatusText.Text = $"{(NdiLib.IsLoaded ? "✔ " : "✖ ")}{NdiLib.Status}   ·   {(VideoDecoder.FfmpegStatus.StartsWith("FFmpeg ") ? "✔ " : "✖ ")}{VideoDecoder.FfmpegStatus}   ·   " +
                          $"Escuchando en UDP {port}   ·   IP de este PC: {string.Join(", ", ips)}";
        GpuText.Text = "Decodificación: " + CamaraNdi.Core.Decoding.DecoderProbe.Current.Describe() + $"   ·   CPU del PC (receptor): {PcCpu():F0} %";
        JitterSlider.IsEnabled = UsePhoneMode.IsChecked != true;
        JitterText.Text = UsePhoneMode.IsChecked == true ? "lo elige cada celular" : $"{(int)JitterSlider.Value} ms";
        NdiMissingPanel.Visibility = NdiLib.IsLoaded ? Visibility.Collapsed : Visibility.Visible;
        // Si falta NDI, volver a buscarlo cada ~10 s (por si lo acaban de instalar).
        if (!NdiLib.IsLoaded && ++_ndiRetryTicks % 20 == 0) NdiLib.Retry();
        EmptyHint.Visibility = _cards.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyHintText.Text = "Abre la app Cámara NDI en el celular (misma red WiFi) y pulsa Transmitir. " +
                             "El celular encuentra este PC solo; si no, escribe en el celular una de estas IP: " +
                             string.Join(", ", ips.Select(i => i.Split(' ')[0])) +
                             ". Si Windows pregunta por el Firewall, permite el acceso en redes privadas.";
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

    private void OnDownloadNdi(object sender, RoutedEventArgs e) =>
        Process.Start(new ProcessStartInfo("https://ndi.video/tools/") { UseShellExecute = true });

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
