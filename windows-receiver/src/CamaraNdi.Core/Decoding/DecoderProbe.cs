using System.Reflection;

namespace CamaraNdi.Core.Decoding;

/// <summary>Qué puede decodificar la GPU de este PC (resultado de la prueba de arranque).</summary>
public sealed class DecoderCaps
{
    public bool Probed { get; init; }
    /// <summary>Método de GPU que funciona para H.265 (null = ninguno).</summary>
    public string? HevcHw { get; init; }
    /// <summary>Altura máxima que la GPU decodifica en H.265 (0 = no decodifica H.265).</summary>
    public int HevcGpuMaxHeight { get; init; }
    public string? H264Hw { get; init; }
    public int H264GpuMaxHeight { get; init; }
    public string GpuName { get; init; } = "";

    public static readonly DecoderCaps Unknown = new() { Probed = false };

    public string? HwFor(VideoCodec c) => c == VideoCodec.Hevc ? HevcHw : H264Hw;
    public int GpuMaxHeight(VideoCodec c) => c == VideoCodec.Hevc ? HevcGpuMaxHeight : H264GpuMaxHeight;

    public string Describe()
    {
        if (!Probed) return "Probando la GPU…";
        string H(int h) => h >= 2160 ? "4K" : h >= 1080 ? "1080p" : "no";
        var gpu = string.IsNullOrEmpty(GpuName) ? "GPU" : GpuName;
        if (HevcGpuMaxHeight == 0 && H264GpuMaxHeight == 0) return "Sin decodificación por GPU: se usa la CPU (todos los núcleos)";
        return $"{gpu}: H.265 {H(HevcGpuMaxHeight)} · H.264 {H(H264GpuMaxHeight)}";
    }
}

/// <summary>
/// Al arrancar, decodifica unas muestras diminutas (4K y 1080p, H.265 y H.264) con cada método de
/// GPU disponible. Así el celular sabe de antemano qué códec mandar para que el PC lo decodifique
/// por GPU (por ejemplo, H.264 si la GPU no tiene H.265 en 4K).
/// </summary>
public static class DecoderProbe
{
    private static readonly object Gate = new();
    private static DecoderCaps _caps = DecoderCaps.Unknown;
    private static Task<DecoderCaps>? _running;

    public static DecoderCaps Current { get { lock (Gate) return _caps; } }
    public static event Action<DecoderCaps>? Completed;

    /// <summary>Inicia la prueba en segundo plano (una sola vez).</summary>
    public static Task<DecoderCaps> StartAsync()
    {
        lock (Gate)
        {
            return _running ??= Task.Run(() =>
            {
                var caps = Run();
                lock (Gate) _caps = caps;
                Log.Info("Prueba de GPU: " + caps.Describe() +
                         $" (H.265 por {caps.HevcHw ?? "CPU"}, H.264 por {caps.H264Hw ?? "CPU"})");
                try { Completed?.Invoke(caps); } catch { }
                return caps;
            });
        }
    }

    private static DecoderCaps Run()
    {
        if (!VideoDecoder.EnsureFfmpeg()) return new DecoderCaps { Probed = true };
        string gpuName = "";
        VideoDecoder.VerboseSink = line =>
        {
            // D3D11VA: "Using device 10de:1c82 (NVIDIA GeForce GTX 1050 Ti)."
            int i = line.IndexOf("Using device", StringComparison.Ordinal);
            if (i >= 0 && gpuName.Length == 0)
            {
                // El nombre puede tener paréntesis: "Intel(R) UHD Graphics 620".
                int a = line.IndexOf('(', i), b = line.LastIndexOf(')');
                gpuName = a >= 0 && b > a ? line[(a + 1)..b] : line[(i + 12)..].Trim(' ', '.');
            }
        };
        try
        {
            var (hevcHw, hevcH) = Best(VideoCodec.Hevc);
            var (h264Hw, h264H) = Best(VideoCodec.H264);
            return new DecoderCaps
            {
                Probed = true,
                HevcHw = hevcHw, HevcGpuMaxHeight = hevcH,
                H264Hw = h264Hw, H264GpuMaxHeight = h264H,
                GpuName = gpuName,
            };
        }
        finally
        {
            VideoDecoder.VerboseSink = null;
        }
    }

    private static (string? hw, int height) Best(VideoCodec codec)
    {
        string prefix = codec == VideoCodec.Hevc ? "hevc" : "h264";
        var s2160 = Sample($"{prefix}_2160.bin");
        var s1080 = Sample($"{prefix}_1080.bin");
        string? best = null;
        int bestH = 0;
        foreach (var hw in VideoDecoder.PlatformHwTypes())
        {
            if (s2160 != null && VideoDecoder.ProbeHardware(codec, hw, s2160)) return (hw, 2160);
            if (bestH < 1080 && s1080 != null && VideoDecoder.ProbeHardware(codec, hw, s1080)) { best = hw; bestH = 1080; }
        }
        return (best, bestH);
    }

    private static byte[]? Sample(string name)
    {
        var asm = typeof(DecoderProbe).Assembly;
        var res = asm.GetManifestResourceNames().FirstOrDefault(n => n.EndsWith(name, StringComparison.Ordinal));
        if (res == null) return null;
        using var s = asm.GetManifestResourceStream(res)!;
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }

    /// <summary>Sólo para pruebas: fija el resultado sin probar.</summary>
    internal static void SetForTests(DecoderCaps caps)
    {
        lock (Gate) { _caps = caps; _running = Task.FromResult(caps); }
    }
}
