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
