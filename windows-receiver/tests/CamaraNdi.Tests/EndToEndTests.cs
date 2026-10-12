using CamaraNdi.Core;
using CamaraNdi.Core.Decoding;
using CamaraNdi.Sim;
using Xunit;
using Xunit.Abstractions;

namespace CamaraNdi.Tests;

/// <summary>
/// Prueba de punta a punta en la misma máquina: emisor simulado (FFmpeg) -> UDP -> receptor
/// (jitter buffer + NACK + decodificador). Se salta si FFmpeg no está instalado.
/// </summary>
[Collection("E2E")]
public class EndToEndTests
{
    private readonly ITestOutputHelper _out;
    public EndToEndTests(ITestOutputHelper o) => _out = o;

    [Theory]
    [InlineData(VideoCodec.H264, 0.0)]
    [InlineData(VideoCodec.H264, 0.03)]
    [InlineData(VideoCodec.Hevc, 0.02)]
    public void StreamsAndRecoversFromLoss(VideoCodec codec, double loss)
    {
        if (!VideoDecoder.EnsureFfmpeg()) { _out.WriteLine("FFmpeg no disponible: prueba omitida"); return; }
        var settings = new ReceiverSettings { MediaPort = 0, DiscoveryPort = 0, Decoder = "cpu" };
        using var server = new ReceiverServer(settings);
        var sim = new SimSender { Port = server.MediaPort, Width = 1280, Height = 720, Fps = 30, Codec = codec, LossRate = loss, BitrateKbps = 3000 };
        var outputs = new List<double>();
        var t = new Thread(() => sim.Run(TimeSpan.FromSeconds(6)));
        t.Start();
        SessionSnapshot? last = null;
        while (t.IsAlive)
        {
            Thread.Sleep(500);
            var s = server.Sessions.FirstOrDefault();
            if (s == null) continue;
            last = s.Snapshot;
            if (last.State == SessionState.EnVivo) outputs.Add(last.OutputFps);
            _out.WriteLine($"{last.State} {last.OutputFps:F1} fps, pérdida {last.LossPct:F1}% no recuperado {last.LostPct:F2}% lat {last.ReceiverLatencyMs:F1} ms dec {last.Decoder} RTT {sim.LastRttMs:F2}");
        }
        t.Join();
        _out.WriteLine($"enviados {sim.FramesSent} frames, {sim.PacketsSent} paquetes; perdidos a propósito {sim.PacketsDroppedOnPurpose}; reenviados {sim.Retransmitted}; keyframes pedidos {sim.KeyframeRequests}; reportes {sim.Reports}");
        Assert.NotNull(last);
        Assert.True(sim.Reports > 5, "el receptor debe enviar reportes");
        Assert.True(outputs.Count > 4);
        double avg = outputs.Skip(2).Average();
        Assert.True(avg > 26, $"fps promedio {avg:F1} demasiado bajo");
        if (loss > 0) Assert.True(sim.Retransmitted > 0, "deben pedirse reenvíos (NACK)");
    }
}

[Collection("E2E")]
public class MultiPhoneTests
{
    private readonly ITestOutputHelper _out;
    public MultiPhoneTests(ITestOutputHelper o) => _out = o;

    [Fact]
    public void ThreePhonesAtOnce()
    {
        if (!VideoDecoder.EnsureFfmpeg()) { _out.WriteLine("FFmpeg no disponible: prueba omitida"); return; }
        var settings = new ReceiverSettings { MediaPort = 0, DiscoveryPort = 0, Decoder = "cpu", MaxPhones = 3 };
        using var server = new ReceiverServer(settings);
        var sims = new[]
        {
            new SimSender { Port = server.MediaPort, Width = 1280, Height = 720, Fps = 30, Codec = VideoCodec.H264, LossRate = 0.01, Name = "Cel 1", Seed = 1 },
            // (H.264 en los tres: libx265 por software no llega a 30 fps con 3 emisores en una máquina de pruebas)
            new SimSender { Port = server.MediaPort, Width = 1280, Height = 720, Fps = 30, Codec = VideoCodec.H264, LossRate = 0.01, Name = "Cel 2", Seed = 2 },
            new SimSender { Port = server.MediaPort, Width = 960, Height = 540, Fps = 30, Codec = VideoCodec.H264, LossRate = 0.01, Name = "Cel 3", Seed = 3 },
        };
        var threads = sims.Select(s => new Thread(() => s.Run(TimeSpan.FromSeconds(6)))).ToList();
        threads.ForEach(t => t.Start());
        var fps = new Dictionary<string, List<double>>();
        while (threads.Any(t => t.IsAlive))
        {
            Thread.Sleep(500);
            foreach (var s in server.Sessions)
            {
                var x = s.Snapshot;
                if (x.State != SessionState.EnVivo) continue;
                if (!fps.TryGetValue(x.NdiName, out var l)) fps[x.NdiName] = l = new();
                l.Add(x.OutputFps);
            }
        }
        threads.ForEach(t => t.Join());
        foreach (var s in sims) _out.WriteLine($"{s.Name}: el emisor produjo {s.FramesSent / 6.0:F1} fps");
        Assert.Equal(3, server.Sessions.Count);
        foreach (var (name, l) in fps)
        {
            double avg = l.Skip(2).Average();
            _out.WriteLine($"{name}: {avg:F1} fps promedio");
            Assert.True(avg > 26, $"{name}: {avg:F1} fps");
        }
        Assert.Equal(3, fps.Count);

        // Un cuarto celular debe ser rechazado (límite 3).
        var extra = new SimSender { Port = server.MediaPort, Name = "Cel 4", Seed = 4 };
        extra.Run(TimeSpan.FromSeconds(1.5));
        Assert.Equal(3, server.Sessions.Count);
    }
}

[Collection("E2E")]
public class QualityModeTests
{
    private readonly ITestOutputHelper _out;
    public QualityModeTests(ITestOutputHelper o) => _out = o;

    [Theory]
    [InlineData("minima")]
    [InlineData("maxima")]
    public void BigBufferAvoidsDroppedFrames(string mode)
    {
        if (!VideoDecoder.EnsureFfmpeg()) { _out.WriteLine("FFmpeg no disponible: prueba omitida"); return; }
        var settings = new ReceiverSettings { MediaPort = 0, DiscoveryPort = 0, Decoder = "cpu" };
        using var server = new ReceiverServer(settings);
        // 6 % de pérdida en ambos sentidos (también se pierden NACK y reenvíos): WiFi muy malo.
        var sim = new SimSender { Port = server.MediaPort, Width = 1280, Height = 720, Fps = 30, LossRate = 0.06, LatencyMode = mode, BitrateKbps = 4000, Seed = 5 };
        var t = new Thread(() => sim.Run(TimeSpan.FromSeconds(8)));
        t.Start();
        long droppedAtStart = -1;
        SessionSnapshot? last = null;
        var fps = new List<double>();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (t.IsAlive)
        {
            Thread.Sleep(500);
            var s = server.Sessions.FirstOrDefault();
            if (s == null) continue;
            last = s.Snapshot;
            if (sw.Elapsed.TotalSeconds > 2.5)
            {
                if (droppedAtStart < 0) droppedAtStart = last.FramesDropped;
                if (last.State == SessionState.EnVivo) fps.Add(last.OutputFps);
            }
        }
        t.Join();
        long dropped = last!.FramesDropped - droppedAtStart;
        _out.WriteLine($"{mode}: buffer {last.JitterBufferMs} ms, frames perdidos {dropped}, fps prom {fps.Average():F1}, latencia receptor {last.ReceiverLatencyMs:F0} ms, reenvíos {sim.Retransmitted}, keyframes pedidos {sim.KeyframeRequests}");
        if (mode == "maxima")
        {
            Assert.Equal(800, last.JitterBufferMs);
            Assert.True(dropped <= 1, $"se perdieron {dropped} frames");
            Assert.True(fps.Average() > 27, $"fps {fps.Average():F1}");
        }
    }
}

[Collection("E2E")]
public class SlowPcTests
{
    private readonly ITestOutputHelper _out;
    public SlowPcTests(ITestOutputHelper o) => _out = o;

    /// <summary>
    /// Lo que se vio en el video del usuario: el PC decodifica más lento de lo que llega.
    /// Antes: 1 fps de salida y ~8 s de retraso creciente. Ahora: el retraso queda acotado,
    /// se pide al celular menos píxeles por segundo, y el límite se puede quitar.
    /// </summary>
    [Fact]
    public void SlowDecoderKeepsLatencyBoundedAndAsksForLowerResolution()
    {
        if (!VideoDecoder.EnsureFfmpeg()) { _out.WriteLine("FFmpeg no disponible: prueba omitida"); return; }
        var settings = new ReceiverSettings { MediaPort = 0, DiscoveryPort = 0, Decoder = "cpu", AutoLowerResolution = true };
        using var server = new ReceiverServer(settings);
        var sim = new SimSender { Port = server.MediaPort, Width = 1920, Height = 1080, Fps = 30, LatencyMode = "calidad", BitrateKbps = 6000 };
        var t = new Thread(() => sim.Run(TimeSpan.FromSeconds(16)));
        t.Start();
        while (server.Sessions.Count == 0 && t.IsAlive) Thread.Sleep(10);
        var s = server.Sessions.First();
        s.TestDecodeDelayMs = 70; // el "PC" sólo alcanza ~14 fps
        double maxLatency = 0;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (t.IsAlive && sim.LastLimit == null && sw.Elapsed.TotalSeconds < 12)
        {
            Thread.Sleep(250);
            if (sw.Elapsed.TotalSeconds > 4) maxLatency = Math.Max(maxLatency, s.Snapshot.ReceiverLatencyMs);
        }
        _out.WriteLine($"latencia máx receptor {maxLatency:F0} ms; límite pedido: {sim.LastLimit?.MaxHeight}p / {sim.LastLimit?.MaxPixelRate} px/s ({sim.LastLimit?.Reason})");
        Assert.True(maxLatency < 2500, $"el retraso creció a {maxLatency:F0} ms");
        Assert.NotNull(sim.LastLimit);
        Assert.Equal(1080, sim.LastLimit!.MaxHeight);
        Assert.Equal((long)(1920L * 1080 * 30 * 0.8), sim.LastLimit.MaxPixelRate);

        // El PC se recupera: el aviso de "va a saltos" no queda; sí queda informado el límite.
        s.TestDecodeDelayMs = 0;
        Thread.Sleep(3000);
        var snap = s.Snapshot;
        _out.WriteLine($"tras recuperarse: aviso \"{snap.Warning}\", límite \"{snap.LimitInfo}\"");
        Assert.False(string.IsNullOrEmpty(snap.LimitInfo));
        Assert.Equal(snap.LimitInfo, snap.Warning);
        Assert.Equal(sim.LastLimit.MaxPixelRate, sim.Caps!.MaxPixelRate); // el HELLO_ACK también lo lleva

        // "Quitar límite": se avisa al celular y los HELLO_ACK dejan de llevarlo.
        s.ClearLimit();
        Thread.Sleep(1500);
        snap = s.Snapshot;
        _out.WriteLine($"tras quitar el límite: LIMIT {sim.LastLimit?.MaxHeight}/{sim.LastLimit?.MaxPixelRate}, ACK {sim.Caps.MaxHeight}/{sim.Caps.MaxPixelRate}, aviso \"{snap.Warning}\"");
        Assert.Equal(0, sim.LastLimit!.MaxHeight);
        Assert.Equal(0, sim.LastLimit.MaxPixelRate);
        Assert.Equal(0, sim.Caps!.MaxPixelRate);
        Assert.Equal("", snap.LimitInfo);
        Assert.Equal("", snap.Warning);
        t.Join();
    }

    /// <summary>
    /// Con "Bajar resolución" desmarcado sólo se avisa; y el aviso se borra cuando el PC vuelve a ir
    /// a la par (antes quedaba para siempre y al marcar la casilla se mandaba un límite igual).
    /// </summary>
    [Fact]
    public void OnlyWarnsWhenAutoLowerIsOffAndClearsWarningWhenRecovered()
    {
        if (!VideoDecoder.EnsureFfmpeg()) { _out.WriteLine("FFmpeg no disponible: prueba omitida"); return; }
        var settings = new ReceiverSettings { MediaPort = 0, DiscoveryPort = 0, Decoder = "cpu", AutoLowerResolution = false };
        using var server = new ReceiverServer(settings);
        var sim = new SimSender { Port = server.MediaPort, Width = 1920, Height = 1080, Fps = 30, LatencyMode = "calidad", BitrateKbps = 6000 };
        var t = new Thread(() => sim.Run(TimeSpan.FromSeconds(16)));
        t.Start();
        while (server.Sessions.Count == 0 && t.IsAlive) Thread.Sleep(10);
        var s = server.Sessions.First();
        s.TestDecodeDelayMs = 70;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        string warning = "";
        while (t.IsAlive && sw.Elapsed.TotalSeconds < 9 && warning.Length == 0) { Thread.Sleep(250); warning = s.Snapshot.Warning; }
        _out.WriteLine($"aviso con el PC lento: \"{warning}\"");
        Assert.Contains("no alcanza", warning);
        Assert.Null(sim.LastLimit);

        s.TestDecodeDelayMs = 0;
        sw.Restart();
        while (t.IsAlive && sw.Elapsed.TotalSeconds < 6 && s.Snapshot.Warning.Length > 0) Thread.Sleep(250);
        _out.WriteLine($"el aviso se borró a los {sw.Elapsed.TotalSeconds:F1} s de recuperarse");
        Assert.Equal("", s.Snapshot.Warning);

        // Marcar la casilla con el PC ya recuperado no debe mandar un límite.
        settings.AutoLowerResolution = true;
        Thread.Sleep(2500);
        Assert.Null(sim.LastLimit);
        t.Join();
    }

    /// <summary>
    /// Mucha pérdida de WiFi NO es "PC lento": no se debe pedir menos resolución (antes se pedía
    /// porque los frames que perdía la red contaban como frames que el PC no alcanzó a decodificar).
    /// </summary>
    [Theory]
    [InlineData(9)]
    [InlineData(21)]
    public void NetworkLossIsNotMistakenForSlowPc(int seed)
    {
        if (!VideoDecoder.EnsureFfmpeg()) { _out.WriteLine("FFmpeg no disponible: prueba omitida"); return; }
        var settings = new ReceiverSettings { MediaPort = 0, DiscoveryPort = 0, Decoder = "cpu", AutoLowerResolution = true };
        using var server = new ReceiverServer(settings);
        // 20 % de pérdida en ambos sentidos: la versión anterior pedía bajar la resolución con esto.
        var sim = new SimSender { Port = server.MediaPort, Width = 1280, Height = 720, Fps = 40, LatencyMode = "minima", BitrateKbps = 4000, LossRate = 0.20, Seed = seed };
        sim.Run(TimeSpan.FromSeconds(9));
        var snap = server.Sessions.First().Snapshot;
        _out.WriteLine($"pérdida {snap.LossPct:F1} %, descartados {snap.FramesDropped}, aviso \"{snap.Warning}\", límite {sim.LastLimit?.MaxPixelRate}");
        Assert.True(snap.FramesDropped > 0, "la prueba necesita que la red pierda frames");
        Assert.Null(sim.LastLimit);
        Assert.Equal("", snap.Warning);
    }

    [Fact]
    public void HelloAckCarriesDecodeCaps()
    {
        if (!VideoDecoder.EnsureFfmpeg()) { _out.WriteLine("FFmpeg no disponible: prueba omitida"); return; }
        var caps = CamaraNdi.Core.Decoding.DecoderProbe.StartAsync().GetAwaiter().GetResult();
        _out.WriteLine("Prueba de GPU en esta máquina: " + caps.Describe());
        Assert.True(caps.Probed);
        var settings = new ReceiverSettings { MediaPort = 0, DiscoveryPort = 0 };
        using var server = new ReceiverServer(settings);
        var sim = new SimSender { Port = server.MediaPort, Width = 640, Height = 360, Fps = 30 };
        sim.Run(TimeSpan.FromSeconds(2));
        Assert.NotNull(sim.Caps);
        Assert.True(sim.Caps!.Probed);
        Assert.Equal(caps.HevcGpuMaxHeight, sim.Caps.HevcGpuMaxHeight);
    }

    /// <summary>Con "Decodificar con: CPU" el celular no debe elegir el códec pensando en la GPU.</summary>
    [Fact]
    public void CpuDecoderAnnouncesNoGpu()
    {
        if (!VideoDecoder.EnsureFfmpeg()) { _out.WriteLine("FFmpeg no disponible: prueba omitida"); return; }
        CamaraNdi.Core.Decoding.DecoderProbe.StartAsync().GetAwaiter().GetResult();
        var settings = new ReceiverSettings { MediaPort = 0, DiscoveryPort = 0, Decoder = "cpu" };
        using var server = new ReceiverServer(settings);
        var sim = new SimSender { Port = server.MediaPort, Width = 640, Height = 360, Fps = 30 };
        sim.Run(TimeSpan.FromSeconds(2));
        Assert.NotNull(sim.Caps);
        Assert.True(sim.Caps!.Probed);
        Assert.Equal(0, sim.Caps.HevcGpuMaxHeight);
        Assert.Equal(0, sim.Caps.H264GpuMaxHeight);
    }
}

[Collection("E2E")]
public class SmoothOutputTests
{
    private readonly ITestOutputHelper _out;
    public SmoothOutputTests(ITestOutputHelper o) => _out = o;

    /// <summary>
    /// Decodificando por CPU con hilos por frame, cada imagen sale unos frames tarde. El suavizado
    /// tiene que contar con eso: si no, el jitter de la red pasa tal cual a NDI (imagen a tirones).
    /// </summary>
    [Fact]
    public void FrameThreadedCpuDecodeStillOutputsEvenly()
    {
        if (!VideoDecoder.EnsureFfmpeg()) { _out.WriteLine("FFmpeg no disponible: prueba omitida"); return; }
        var settings = new ReceiverSettings { MediaPort = 0, DiscoveryPort = 0, Decoder = "cpu" };
        using var server = new ReceiverServer(settings);
        var sim = new SimSender { Port = server.MediaPort, Width = 1280, Height = 720, Fps = 30, LatencyMode = "calidad", BitrateKbps = 3000, NetJitterMs = 40, Seed = 3 };
        var times = new List<double>();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var t = new Thread(() => sim.Run(TimeSpan.FromSeconds(9)));
        t.Start();
        while (server.Sessions.Count == 0 && t.IsAlive) Thread.Sleep(5);
        var s = server.Sessions.First();
        s.FrameTap = _ => { if (sw.Elapsed.TotalSeconds > 3) lock (times) times.Add(sw.Elapsed.TotalMilliseconds); };
        string decoder = "";
        while (t.IsAlive) { Thread.Sleep(250); if (sw.Elapsed.TotalSeconds > 4 && decoder.Length == 0) decoder = s.Snapshot.Decoder; }
        t.Join();
        List<double> d;
        lock (times) d = times.Zip(times.Skip(1), (a, b) => b - a).ToList();
        double avg = d.Average(), sd = Math.Sqrt(d.Sum(x => (x - avg) * (x - avg)) / d.Count);
        int fast = d.Count(x => x < 10), slow = d.Count(x => x > 50);
        _out.WriteLine($"decodificador {decoder}: {d.Count} intervalos, prom {avg:F1} ms, desviación {sd:F1} ms, <10 ms: {fast}, >50 ms: {slow}");
        Assert.True(sd < 10, $"salida a tirones: desviación {sd:F1} ms");
    }

    /// <summary>
    /// "Detener" y volver a "Transmitir" en el celular: no deben salir al aire imágenes de antes de
    /// la pausa (con hilos por frame quedaban unas cuantas dentro del decodificador).
    /// </summary>
    [Fact]
    public void NoOldFramesAfterPause()
    {
        if (!VideoDecoder.EnsureFfmpeg()) { _out.WriteLine("FFmpeg no disponible: prueba omitida"); return; }
        var settings = new ReceiverSettings { MediaPort = 0, DiscoveryPort = 0, Decoder = "cpu" };
        using var server = new ReceiverServer(settings);
        var sim = new SimSender { Port = server.MediaPort, Width = 1280, Height = 720, Fps = 30, LatencyMode = "calidad", BitrateKbps = 3000, PauseAtSec = 3, PauseForSec = 1.2 };
        var ages = new List<(double At, long Age)>();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var t = new Thread(() => sim.Run(TimeSpan.FromSeconds(7)));
        t.Start();
        while (server.Sessions.Count == 0 && t.IsAlive) Thread.Sleep(5);
        var s = server.Sessions.First();
        s.FrameTap = b => { lock (ages) ages.Add((sw.Elapsed.TotalSeconds, Clock.NowMs - b.FirstArrivalMs)); };
        string decoder = "";
        while (t.IsAlive) { Thread.Sleep(250); if (sw.Elapsed.TotalSeconds > 2 && decoder.Length == 0) decoder = s.Snapshot.Decoder; }
        t.Join();
        List<(double At, long Age)> after;
        lock (ages) after = ages.Where(a => a.At > 4.2).ToList();
        long worst = after.Count > 0 ? after.Max(a => a.Age) : 0;
        _out.WriteLine($"decodificador {decoder}: {after.Count} imágenes después de la pausa, la más vieja tenía {worst} ms");
        Assert.True(after.Count > 30, "debe seguir transmitiendo después de la pausa");
        Assert.True(worst < 1000, $"salió al aire una imagen de hace {worst} ms");
    }
}

[Collection("E2E")]
public class CpuDecodeSpeedTests
{
    private readonly ITestOutputHelper _out;
    public CpuDecodeSpeedTests(ITestOutputHelper o) => _out = o;

    /// <summary>Mide cuántos fps de 4K H.265 decodifica la CPU con 1 hilo (como antes) y con todos los núcleos (ahora).</summary>
    [Fact]
    public void MultiThreadedCpuDecodeIsFaster()
    {
        if (!VideoDecoder.EnsureFfmpeg()) { _out.WriteLine("FFmpeg no disponible: prueba omitida"); return; }
        var frames = EncodeHevc4k(24);
        double Fps(VideoDecoder d)
        {
            var outs = new List<VideoBuffer>();
            var sw = new System.Diagnostics.Stopwatch();
            long pts = 0;
            int got = 0;
            // 3 pasadas por la secuencia (empieza con un IDR, así que se puede repetir): la primera
            // sólo calienta los hilos; se miden las otras dos para que el resultado no dependa del arranque.
            for (int pass = 0; pass < 3; pass++)
            {
                if (pass == 1) { got = 0; sw.Restart(); }
                foreach (var f in frames)
                {
                    outs.Clear();
                    d.Decode(f, f.Length, pts += 33_333, size => new VideoBuffer(size), outs);
                    got += outs.Count;
                }
            }
            return got * 1000.0 / sw.Elapsed.TotalMilliseconds;
        }
        double fpsOne, fpsMulti;
        using (var one = new VideoDecoder(VideoCodec.Hevc, Array.Empty<string>(), lowLatency: true, forceThreads: 1))
            fpsOne = Fps(one);
        using (var multi = new VideoDecoder(VideoCodec.Hevc, Array.Empty<string>(), lowLatency: false))
            fpsMulti = Fps(multi);
        _out.WriteLine($"4K H.265 en CPU: {fpsOne:F1} fps con 1 hilo (versión anterior) vs {fpsMulti:F1} fps con {Environment.ProcessorCount} núcleos");
        Assert.True(fpsMulti > fpsOne * 1.3, $"{fpsMulti:F1} vs {fpsOne:F1}");
    }

    /// <summary>4K H.265 de prueba, codificado dentro del proceso (no hace falta el programa ffmpeg).</summary>
    private static List<byte[]> EncodeHevc4k(int n) =>
        new SimSender { Width = 3840, Height = 2160, Fps = 30, Codec = VideoCodec.Hevc, BitrateKbps = 20000 }.EncodeClip(n);
}
