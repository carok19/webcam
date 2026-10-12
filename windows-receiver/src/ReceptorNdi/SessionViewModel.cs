using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CamaraNdi.Core;

namespace ReceptorNdi;

/// <summary>Datos de una tarjeta (un celular), ya formateados para mostrar.</summary>
public sealed class SessionViewModel : INotifyPropertyChanged
{
    private static readonly Brush LiveBrush = Frozen(Color.FromRgb(0xE5, 0x39, 0x35));
    private static readonly Brush IdleBrush = Frozen(Color.FromRgb(0x5F, 0x63, 0x68));
    private static readonly Brush WarnBrush = Frozen(Color.FromRgb(0xF5, 0x7C, 0x00));
    private static readonly Brush OkText = Frozen(Color.FromRgb(0xE8, 0xEA, 0xED));
    private static readonly Brush BadText = Frozen(Color.FromRgb(0xFF, 0x8A, 0x80));
    private static readonly Brush MidText = Frozen(Color.FromRgb(0xFF, 0xD1, 0x80));
    private static readonly Brush OkThermal = Frozen(Color.FromRgb(0x43, 0xA0, 0x47));

    private static Brush Frozen(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }

    public PhoneSession Session { get; }
    private long _thumbVersion;
    private WriteableBitmap? _bmp;

    public SessionViewModel(PhoneSession s) { Session = s; Update(); }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Name { get; private set; } = "";
    public string Model { get; private set; } = "";
    public string StateText { get; private set; } = "";
    public Brush StateBrush { get; private set; } = IdleBrush;
    public string Phone { get; private set; } = "";
    public Brush ThermalBrush { get; private set; } = OkThermal;
    public string Warning { get; private set; } = "";
    public Visibility WarningVisibility { get; private set; } = Visibility.Collapsed;
    /// <summary>Se ve el botón "Quitar límite" cuando este PC le pidió al celular menos resolución.</summary>
    public Visibility LimitVisibility { get; private set; } = Visibility.Collapsed;
    public string Video { get; private set; } = "";
    public string Bitrate { get; private set; } = "";
    public string Loss { get; private set; } = "";
    public Brush LossBrush { get; private set; } = OkText;
    public string Latency { get; private set; } = "";
    public string Decoder { get; private set; } = "";
    public string Ndi { get; private set; } = "";
    public string Test { get; private set; } = "";
    public string ColorInfo { get; private set; } = "";

    /// <summary>Se llama cuando el usuario cambia algo que hay que guardar.</summary>
    public static Action? SaveRequested;

    private static readonly string[] RangeModes = { "auto", "full", "limited" };

    public int RangeIndex
    {
        get => Math.Max(0, Array.IndexOf(RangeModes, Session.RangeOverride));
        set
        {
            if (value < 0 || value >= RangeModes.Length || RangeModes[value] == Session.RangeOverride) return;
            Session.SetRangeOverride(RangeModes[value]);
            SaveRequested?.Invoke();
        }
    }
    public ImageSource? Thumbnail => _bmp;

    public void Update()
    {
        var x = Session.Snapshot;
        Name = x.NdiName;
        Model = $"{x.Model}  ·  {x.Endpoint}";
        (StateText, StateBrush) = x.State switch
        {
            SessionState.EnVivo => ("● EN VIVO", LiveBrush),
            SessionState.EnEspera => ("En espera", IdleBrush),
            SessionState.SinSenal => ("Sin señal", WarnBrush),
            SessionState.Desconectado => ("Desconectado", IdleBrush),
            _ => ("Conectando…", IdleBrush),
        };
        // Sin emojis: WPF no los dibuja en color y salen como cuadritos.
        string bat = x.BatteryPct >= 0 ? $"bat. {x.BatteryPct}%{(x.Charging ? " (cargando)" : "")}" : "";
        string temp = x.BatteryTempC > 0 ? $"{x.BatteryTempC:F0}°C" : "";
        Phone = string.Join(" · ", new[] { temp, bat }.Where(t => t.Length > 0));
        ThermalBrush = x.ThermalLevel switch { "hot" => LiveBrush, "warm" => WarnBrush, _ => OkThermal };
        Warning = x.Warning;
        WarningVisibility = string.IsNullOrEmpty(x.Warning) ? Visibility.Collapsed : Visibility.Visible;
        LimitVisibility = string.IsNullOrEmpty(x.LimitInfo) ? Visibility.Collapsed : Visibility.Visible;
        Video = x.Width > 0 ? $"{Label(x.Width, x.Height)}{x.Fps} {x.Codec}  ·  salida {x.OutputFps:F0} fps" : "-";
        Bitrate = $"{x.RxMbps:F1} Mbps recibidos (celular {x.PhoneBitrateKbps / 1000.0:F1})";
        Loss = $"{x.LossPct:F1} % (no recuperado {x.LostPct:F2} %)  ·  descartados {x.FramesDropped}";
        LossBrush = x.LostPct > 1 ? BadText : x.LossPct > 3 ? MidText : OkText;
        Latency = x.TotalLatencyMs > 0
            ? $"≈ {x.TotalLatencyMs:F0} ms total  (PC {x.ReceiverLatencyMs:F0} · RTT {x.RttMs:F0} · buffer {x.JitterBufferMs})"
            : "-";
        Decoder = x.Decoder;
        Ndi = $"{x.NdiConnections} conexión(es)  ·  modo {x.LatencyMode}";
        ColorInfo = x.ColorInfo;
        var t = TimeSpan.FromSeconds(x.LiveSeconds);
        Test = x.LiveSeconds > 0
            ? $"{(int)t.TotalMinutes:00}:{t.Seconds:00} en vivo · {x.AvgFpsLive:F1} fps prom (mín {x.MinFpsLive:F0}) · celular: temp máx {x.MaxTempC:F0}\u00A0°C, CPU de la app {x.CpuPct:F0}\u00A0%"
            : "-";

        if (Session.TryGetThumbnail(ref _thumbVersion, out var bgra, out int w, out int h) && bgra != null)
        {
            if (_bmp == null || _bmp.PixelWidth != w || _bmp.PixelHeight != h)
            {
                _bmp = new WriteableBitmap(w, h, 96, 96, PixelFormats.Bgra32, null);
                OnChanged(nameof(Thumbnail));
            }
            _bmp.WritePixels(new Int32Rect(0, 0, w, h), bgra, w * 4, 0);
        }
        OnChanged(null);
    }

    private static string Label(int w, int h) => h switch
    {
        2160 => "4K",
        1440 => "1440p",
        1080 => "1080p",
        720 => "720p",
        _ => $"{w}x{h} @",
    };

    private void OnChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
