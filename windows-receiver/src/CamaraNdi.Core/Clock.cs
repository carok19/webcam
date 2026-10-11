using System.Diagnostics;

namespace CamaraNdi.Core;

/// <summary>Reloj monotónico en milisegundos.</summary>
public static class Clock
{
    private static readonly long Start = Stopwatch.GetTimestamp();
    public static long NowMs => (Stopwatch.GetTimestamp() - Start) * 1000 / Stopwatch.Frequency;
    public static double NowMsPrecise => (Stopwatch.GetTimestamp() - Start) * 1000.0 / Stopwatch.Frequency;
}
