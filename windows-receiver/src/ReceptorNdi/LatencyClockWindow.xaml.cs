using System.Diagnostics;
using System.Windows;
using System.Windows.Media;

namespace ReceptorNdi;

/// <summary>Reloj en milisegundos que se redibuja en cada refresco de pantalla.</summary>
public partial class LatencyClockWindow : Window
{
    private readonly Stopwatch _sw = Stopwatch.StartNew();

    public LatencyClockWindow()
    {
        InitializeComponent();
        CompositionTarget.Rendering += OnRender;
        Closed += (_, _) => CompositionTarget.Rendering -= OnRender;
    }

    private void OnRender(object? sender, EventArgs e)
    {
        var t = _sw.Elapsed;
        ClockText.Text = $"{(int)t.TotalMinutes:00}:{t.Seconds:00}.{t.Milliseconds:000}";
    }
}
