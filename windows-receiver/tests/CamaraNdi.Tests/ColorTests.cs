using CamaraNdi.Core;
using CamaraNdi.Core.Decoding;
using CamaraNdi.Core.Ndi;
using CamaraNdi.Sim;
using Xunit;
using Xunit.Abstractions;

namespace CamaraNdi.Tests;

[Collection("E2E")]
public class ColorTests
{
    private readonly ITestOutputHelper _out;
    public ColorTests(ITestOutputHelper o) => _out = o;

    private static VideoBuffer Frame(int w, int h, byte y, byte cb, byte cr, bool nv12 = true)
    {
        var b = new VideoBuffer(VideoBuffer.SizeFor(w, h))
        {
            Width = w, Height = h, Stride = w, Length = VideoBuffer.SizeFor(w, h),
            FourCC = nv12 ? NdiLib.FourCC_NV12 : NdiLib.FourCC_I420,
        };
        Array.Fill(b.Data, y, 0, w * h);
        if (nv12)
            for (int i = w * h; i < b.Length; i += 2) { b.Data[i] = cb; b.Data[i + 1] = cr; }
        else
        {
            int c = w * h / 4;
            Array.Fill(b.Data, cb, w * h, c);
            Array.Fill(b.Data, cr, w * h + c, c);
        }
        return b;
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void FullRangeIsMappedTo16_235(bool nv12)
    {
        var black = Frame(64, 36, 0, 128, 128, nv12);
        ColorConverter.ToLimited709(black, fullRange: true, bt601: false);
        Assert.Equal(16, black.Data[0]);
        Assert.Equal(128, black.Data[64 * 36]);

        var white = Frame(64, 36, 255, 255, 0, nv12);
        ColorConverter.ToLimited709(white, fullRange: true, bt601: false);
        Assert.Equal(235, white.Data[0]);
        Assert.Equal(240, white.Data[64 * 36]);
        Assert.Equal(16, white.Data[64 * 36 + (nv12 ? 1 : 64 * 36 / 4)]);

        // Un negro "casi negro" (8) y uno más oscuro (2) siguen siendo distintos: no se aplastan.
        var a = Frame(16, 16, 8, 128, 128, nv12);
        var b = Frame(16, 16, 2, 128, 128, nv12);
        ColorConverter.ToLimited709(a, true, false);
        ColorConverter.ToLimited709(b, true, false);
        Assert.True(a.Data[0] > b.Data[0]);
        Assert.True(b.Data[0] >= 16);
    }

    [Fact]
    public void LimitedBt709IsUntouched()
    {
        Assert.False(ColorConverter.Needed(fullRange: false, bt601: false));
        var f = Frame(16, 16, 50, 100, 200);
        var copy = (byte[])f.Data.Clone();
        ColorConverter.ToLimited709(f, false, false);
        Assert.Equal(copy, f.Data);
    }

    [Fact]
    public void Bt601RedBecomesBt709Red()
    {
        // Rojo puro en BT.601 limitado: Y=81, Cb=90, Cr=240. En BT.709: Y=63, Cb=102, Cr=240.
        var f = Frame(16, 16, 81, 90, 240);
        ColorConverter.ToLimited709(f, fullRange: false, bt601: true);
        Assert.InRange(f.Data[0], 61, 65);
        Assert.InRange(f.Data[16 * 16], 100, 104);
        Assert.InRange(f.Data[16 * 16 + 1], 238, 241);
    }

    [Fact]
    public void DetectorFindsUnsignaledFullRange()
    {
        var full = Frame(320, 180, 128, 128, 128);
        for (int i = 0; i < 320 * 30; i++) full.Data[i] = 3; // franja negra 0–255 (imposible en 16–235)
        var det = new RangeDetector();
        for (int i = 0; i < 40; i++) det.Feed(full);
        Assert.True(det.LooksFull);

        var limited = Frame(320, 180, 16, 128, 128);
        var det2 = new RangeDetector();
        for (int i = 0; i < 200; i++) det2.Feed(limited);
        Assert.False(det2.LooksFull);
    }

    [Theory]
    [InlineData(true)]   // el celular dice "rango completo"
    [InlineData(false)]  // el celular no dice nada (hay que detectarlo)
    public void ReceiverOutputsLimitedRangeFromFullRangePhone(bool signal)
    {
        if (!VideoDecoder.EnsureFfmpeg()) { _out.WriteLine("FFmpeg no disponible: prueba omitida"); return; }
        var settings = new ReceiverSettings { MediaPort = 0, DiscoveryPort = 0, Decoder = "cpu" };
        using var server = new ReceiverServer(settings);
        var sim = new SimSender { Port = server.MediaPort, Width = 1280, Height = 720, Fps = 30, FullRange = true, SignalRange = signal, BitrateKbps = 4000 };
        int minAfter = 255, maxAfter = 0, frames = 0;
        var start = DateTime.UtcNow;
        var t = new Thread(() => sim.Run(TimeSpan.FromSeconds(4)));
        t.Start();
        while (server.Sessions.Count == 0 && t.IsAlive) Thread.Sleep(10);
        var s = server.Sessions.First();
        s.FrameTap = b =>
        {
            if ((DateTime.UtcNow - start).TotalSeconds < 2.5) return; // dar tiempo al detector
            int n = b.Width * b.Height;
            for (int i = 0; i < n; i += 3) { minAfter = Math.Min(minAfter, b.Data[i]); maxAfter = Math.Max(maxAfter, b.Data[i]); }
            frames++;
        };
        t.Join();
        _out.WriteLine($"frames {frames}, Y mín {minAfter}, Y máx {maxAfter}, color: {s.Snapshot.ColorInfo}");
        Assert.True(frames > 10);
        Assert.InRange(minAfter, 12, 30);   // el negro queda en ~16 (con un margen por la compresión)
        Assert.InRange(maxAfter, 225, 245);  // el blanco en ~235
        Assert.Contains("completo", s.Snapshot.ColorInfo);
    }
}

[Collection("E2E")]
public class ColorPerfTests
{
    private readonly ITestOutputHelper _out;
    public ColorPerfTests(ITestOutputHelper o) => _out = o;

    [Theory]
    [InlineData(1920, 1080, false)]
    [InlineData(1920, 1080, true)]
    [InlineData(3840, 2160, false)]
    [InlineData(3840, 2160, true)]
    public void ConversionIsCheap(int w, int h, bool bt601)
    {
        var b = new VideoBuffer(VideoBuffer.SizeFor(w, h)) { Width = w, Height = h, Stride = w, Length = VideoBuffer.SizeFor(w, h), FourCC = NdiLib.FourCC_NV12 };
        new Random(1).NextBytes(b.Data);
        ColorConverter.ToLimited709(b, true, bt601); // calentar el JIT
        var sw = System.Diagnostics.Stopwatch.StartNew();
        const int n = 20;
        for (int i = 0; i < n; i++) ColorConverter.ToLimited709(b, true, bt601);
        double ms = sw.Elapsed.TotalMilliseconds / n;
        _out.WriteLine($"{w}x{h} {(bt601 ? "BT.601" : "BT.709")}: {ms:F2} ms por frame");
        // Holgado: en la máquina de pruebas las demás pruebas corren a la vez.
        Assert.True(ms < 80, $"{ms:F1} ms");
    }
}
