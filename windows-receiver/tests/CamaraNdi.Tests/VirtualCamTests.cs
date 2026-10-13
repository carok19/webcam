using System.Diagnostics;
using CamaraNdi.Core;
using CamaraNdi.Core.Decoding;
using CamaraNdi.Core.Ndi;
using CamaraNdi.Core.VirtualCam;
using CamaraNdi.Sim;
using Xunit;
using Xunit.Abstractions;

namespace CamaraNdi.Tests;

/// <summary>
/// Hace de "cámara virtual dentro de OBS" leyendo la misma memoria con el mismo protocolo que
/// vcam_shm.cpp (para probar el lado del receptor sin Windows).
/// </summary>
internal sealed unsafe class FakeCameraReader
{
    private readonly byte* _p;
    public FakeCameraReader(ISharedRegion region) => _p = region.Pointer;

    public void Want(int w, int h, int fps = 30)
    {
        *(uint*)(_p + VCamLayout.HWantWidth) = (uint)w;
        *(uint*)(_p + VCamLayout.HWantHeight) = (uint)h;
        *(uint*)(_p + VCamLayout.HWantFps100) = (uint)(fps * 100);
        Heartbeat();
    }

    public void Heartbeat() => Volatile.Write(ref *(long*)(_p + VCamLayout.HReaderHeartbeat), Environment.TickCount64);
    public void Leave() => Volatile.Write(ref *(long*)(_p + VCamLayout.HReaderHeartbeat), 0L);

    public ulong LatestSeq => Volatile.Read(ref *(ulong*)(_p + VCamLayout.HLatestSeq));
    public long WriterHeartbeat => Volatile.Read(ref *(long*)(_p + VCamLayout.HWriterHeartbeat));

    /// <summary>Copia la última imagen (null si no hay o si la copia salió a medias).</summary>
    public (byte[] Data, int W, int H)? Read()
    {
        if (*(uint*)(_p + VCamLayout.HMagic) != VCamLayout.FrameMagic) return null;
        ulong seq = LatestSeq;
        if (seq == 0) return null;
        uint slot = *(uint*)(_p + VCamLayout.HLatestSlot);
        byte* meta = _p + VCamLayout.HSlots + slot * VCamLayout.HSlotSize;
        ulong s1 = Volatile.Read(ref *(ulong*)(meta + VCamLayout.SSeq));
        if (s1 != seq) return null;
        int w = (int)*(uint*)(meta + VCamLayout.SWidth), h = (int)*(uint*)(meta + VCamLayout.SHeight);
        var data = new byte[w * h * 3 / 2];
        fixed (byte* d = data)
            Buffer.MemoryCopy(_p + VCamLayout.HeaderBytes + (long)slot * VCamLayout.SlotBytes, d, data.Length, data.Length);
        if (Volatile.Read(ref *(ulong*)(meta + VCamLayout.SSeq)) != s1) return null;
        return (data, w, h);
    }
}

public class VirtualCamTests
{
    private readonly ITestOutputHelper _out;
    public VirtualCamTests(ITestOutputHelper o) => _out = o;

    private static VideoBuffer Frame(int w, int h, bool nv12, Func<int, int, byte> luma, byte u = 100, byte v = 160)
    {
        var b = new VideoBuffer(VideoBuffer.SizeFor(w, h))
        {
            Width = w, Height = h, Stride = w, Length = VideoBuffer.SizeFor(w, h),
            FourCC = nv12 ? NdiLib.FourCC_NV12 : NdiLib.FourCC_I420,
        };
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                b.Data[y * w + x] = luma(x, y);
        int ys = w * h;
        if (nv12)
        {
            for (int i = 0; i < w * h / 2; i += 2) { b.Data[ys + i] = u; b.Data[ys + i + 1] = v; }
        }
        else
        {
            Array.Fill(b.Data, u, ys, w * h / 4);
            Array.Fill(b.Data, v, ys + w * h / 4, w * h / 4);
        }
        return b;
    }

    [Fact]
    public void NothingIsWrittenWhileNoProgramWatches()
    {
        using var region = new HeapRegion(VCamLayout.TotalBytes);
        using var writer = new VirtualCamWriter(1, region);
        var reader = new FakeCameraReader(region);
        Assert.False(writer.Write(Frame(64, 36, true, (x, y) => 50)));
        Assert.Equal(0UL, reader.LatestSeq);
        Assert.True(reader.WriterHeartbeat > 0, "el receptor igual avisa que está vivo");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void FourKBecomesExactHalfWhenObsAsksFor1080p(bool nv12)
    {
        using var region = new HeapRegion(VCamLayout.TotalBytes);
        using var writer = new VirtualCamWriter(2, region);
        var reader = new FakeCameraReader(region);
        reader.Want(1920, 1080);
        // Columnas alternadas 20/220: al promediar de a 2 queda 120 en todos lados.
        var f = Frame(3840, 2160, nv12, (x, y) => (byte)(x % 2 == 0 ? 20 : 220));
        Assert.True(writer.Write(f));
        var got = reader.Read();
        Assert.NotNull(got);
        var (data, w, h) = got!.Value;
        Assert.Equal((1920, 1080), (w, h));
        Assert.All(new[] { 0, 1, 1919, 1920 * 540 + 960, 1920 * 1080 - 1 }, i => Assert.Equal(120, data[i]));
        int ys = 1920 * 1080;
        Assert.Equal(100, data[ys]);     // U
        Assert.Equal(160, data[ys + 1]); // V (intercalado aunque la entrada sea I420)
    }

    [Fact]
    public void SameSizeIsCopiedAndSequenceAdvances()
    {
        using var region = new HeapRegion(VCamLayout.TotalBytes);
        using var writer = new VirtualCamWriter(1, region);
        var reader = new FakeCameraReader(region);
        reader.Want(1280, 720);
        for (int i = 1; i <= 7; i++)
        {
            byte level = (byte)(16 + i * 10);
            Assert.True(writer.Write(Frame(1280, 720, true, (x, y) => level)));
            Assert.Equal((ulong)i, reader.LatestSeq);
            var got = reader.Read();
            Assert.NotNull(got);
            Assert.Equal(level, got!.Value.Data[1280 * 360 + 640]);
        }
        Assert.Equal(7, writer.FramesWritten);
    }

    [Theory]
    [InlineData(1920, 1080, 2560, 1440, false)]
    [InlineData(1280, 720, 1920, 1080, false)]
    [InlineData(2560, 1440, 1920, 1080, true)]
    public void OtherSizesAreScaled(int sw, int sh, int tw, int th, bool disableSwscale)
    {
        if (!disableSwscale && !VideoDecoder.EnsureFfmpeg()) { _out.WriteLine("FFmpeg no disponible: prueba omitida"); return; }
        using var region = new HeapRegion(VCamLayout.TotalBytes);
        using var writer = new VirtualCamWriter(3, region);
        var reader = new FakeCameraReader(region);
        reader.Want(tw, th);
        // Mitad izquierda oscura, mitad derecha clara: después de escalar tiene que seguir igual.
        var f = Frame(sw, sh, true, (x, y) => (byte)(x < sw / 2 ? 30 : 200));
        var sw2 = Stopwatch.StartNew();
        Assert.True(writer.Write(f));
        _out.WriteLine($"{sw}x{sh} -> {tw}x{th}: {sw2.Elapsed.TotalMilliseconds:F1} ms");
        var (data, w, h) = reader.Read()!.Value;
        Assert.Equal((tw, th), (w, h));
        Assert.InRange(data[th / 2 * tw + tw / 8], 28, 32);
        Assert.InRange(data[th / 2 * tw + tw * 7 / 8], 198, 202);
    }

    [Fact]
    public void ControlBlockTellsTheCameraWhoIsConnected()
    {
        var regions = new Dictionary<string, HeapRegion>();
        using var hub = new VirtualCamHub((name, size) => regions[name] = new HeapRegion(size));
        hub.SetCamera(2, VCamLayout.StateLive, "Samsung A52s");
        unsafe
        {
            byte* c = regions[VCamLayout.ControlName].Pointer;
            Assert.Equal(VCamLayout.ControlMagic, *(uint*)(c + VCamLayout.CMagic));
            Assert.Equal((uint)VCamLayout.CameraCount, *(uint*)(c + VCamLayout.CCameraCount));
            Assert.True(Environment.TickCount64 - *(long*)(c + VCamLayout.CHeartbeat) < 2000);
            byte* cam2 = c + VCamLayout.CCameras + VCamLayout.CCameraSize;
            Assert.Equal(VCamLayout.StateLive, *(uint*)(cam2 + VCamLayout.CCamState));
            Assert.Equal("Samsung A52s", new string((char*)(cam2 + VCamLayout.CCamName)));
        }
        Assert.NotNull(hub.Writer(2));
        Assert.Same(hub.Writer(2), hub.Writer(2));
    }
}

[Collection("E2E")]
public class VirtualCamEndToEndTests
{
    private readonly ITestOutputHelper _out;
    public VirtualCamEndToEndTests(ITestOutputHelper o) => _out = o;

    /// <summary>
    /// Celular simulado en 1080p -> receptor -> memoria de "Cámara NDI - Celular 1" -> "OBS" pidiendo 1280x720.
    /// Cada celular recibe su cámara y la conserva entre sesiones.
    /// </summary>
    [Fact]
    public void PhoneFramesReachItsVirtualCamera()
    {
        if (!VideoDecoder.EnsureFfmpeg()) { _out.WriteLine("FFmpeg no disponible: prueba omitida"); return; }
        var regions = new Dictionary<string, HeapRegion>();
        var hub = new VirtualCamHub((name, size) => regions[name] = new HeapRegion(size));
        var settings = new ReceiverSettings { MediaPort = 0, DiscoveryPort = 0, Decoder = "cpu", NdiOutput = false };
        settings.CameraSlots["sim-otro"] = 1; // la 1 "pertenece" a otro celular conocido
        using var server = new ReceiverServer(settings, hub);
        var sim = new SimSender { Port = server.MediaPort, Width = 1920, Height = 1080, Fps = 30, LatencyMode = "calidad", BitrateKbps = 5000, DeviceId = "sim-a" };
        var t = new Thread(() => sim.Run(TimeSpan.FromSeconds(6)));
        t.Start();
        while (server.Sessions.Count == 0 && t.IsAlive) Thread.Sleep(10);
        var s = server.Sessions.First();
        Assert.Equal(2, s.CameraIndex);
        Assert.Equal(2, settings.CameraSlots["sim-a"]);
        var reader = new FakeCameraReader(regions[VCamLayout.FrameName(2)]);
        var seen = new HashSet<ulong>();
        int sizeOk = 0;
        var sw = Stopwatch.StartNew();
        while (t.IsAlive)
        {
            reader.Want(1280, 720);
            var f = reader.Read();
            if (f != null && seen.Add(reader.LatestSeq) && f.Value.W == 1280 && f.Value.H == 720) sizeOk++;
            Thread.Sleep(15);
        }
        t.Join();
        _out.WriteLine($"imágenes nuevas leídas: {seen.Count} en {sw.Elapsed.TotalSeconds:F1} s, con el tamaño pedido: {sizeOk}; en uso: {s.Snapshot.CameraInUse}");
        Assert.True(seen.Count > 60, $"sólo {seen.Count} imágenes");
        Assert.True(sizeOk > 60);
    }
}

/// <summary>Escalador de GPU de mentira: devuelve el estado que se le diga y escribe un valor fijo.</summary>
internal sealed unsafe class FakeGpuScaler : IGpuScaler
{
    public uint StatusRaw;
    public bool Fails;
    public int SleepMs;
    public int Processed;
    public bool Disposed;
    public bool Process(byte* nv12, int sw, int sh, byte* dst, int tw, int th)
    {
        if (Fails) return false;
        if (SleepMs > 0) Thread.Sleep(SleepMs);
        new Span<byte>(dst, tw * th * 3 / 2).Fill(77);
        Processed++;
        return true;
    }
    public VsrStatus? Status() => new VsrStatus(StatusRaw);
    public void Dispose() => Disposed = true;
}

public class GpuEnhancerTests
{
    private const uint Capable = 1, Valid = 2, Enabled = 4, InUse = 8;

    [Fact]
    public void StatusBitsAndDriverVersionAreDecoded()
    {
        var s = new VsrStatus(Capable | Valid | Enabled | InUse | (3u << 4));
        Assert.True(s.Capable && s.Valid && s.Enabled && s.InUse);
        Assert.Equal(3, s.Level);
        Assert.False(new VsrStatus(Capable | Valid | Enabled).InUse);
        // Controlador de Windows 32.0.15.6094 = NVIDIA 560.94
        long umd = (32L << 48) | (0L << 32) | (15L << 16) | 6094L;
        Assert.Equal(560.94, D3D11VsrScaler.NvidiaVersionFromUmd(umd), 2);
    }

    private static unsafe (bool ok, byte first) Run(GpuEnhancer e, int sw, int sh, int tw, int th, long now = 0, double interval = 33)
    {
        var src = new byte[sw * sh * 3 / 2];
        var dst = new byte[tw * th * 3 / 2];
        fixed (byte* s = src)
        fixed (byte* d = dst)
        {
            bool ok = e.TryWrite(s, sw, sh, d, tw, th, interval, now);
            return (ok, dst[0]);
        }
    }

    [Fact]
    public void ActiveOnlyWhenTheDriverSaysItIsApplied()
    {
        var fake = new FakeGpuScaler { StatusRaw = Capable | Valid | Enabled | InUse | (2u << 4) };
        using var e = new GpuEnhancer((a, b, c, d) => fake);
        var (ok, first) = Run(e, 1280, 720, 1920, 1080);
        Assert.True(ok);
        Assert.Equal(77, first);
        Assert.True(e.Active);
        Assert.Contains("nivel 2", e.StatusText);
    }

    [Fact]
    public void OffInThePanelMeansNormalScalingAndRetryLater()
    {
        var fake = new FakeGpuScaler { StatusRaw = Capable | Valid };
        using var e = new GpuEnhancer((a, b, c, d) => fake) { RetryMs = 1000 };
        Assert.False(Run(e, 1280, 720, 1920, 1080, now: 0).ok);
        Assert.False(e.Active);
        Assert.Contains("panel de NVIDIA", e.StatusText);
        Assert.False(Run(e, 1280, 720, 1920, 1080, now: 500).ok);
        Assert.Equal(1, fake.Processed); // no se reintenta antes de tiempo
        fake.StatusRaw = Capable | Valid | Enabled | InUse;
        Assert.True(Run(e, 1280, 720, 1920, 1080, now: 1500).ok);
        Assert.True(e.Active);
    }

    [Fact]
    public void EnabledButNotAppliedIsNeverReportedAsActive()
    {
        var fake = new FakeGpuScaler { StatusRaw = Capable | Valid | Enabled };
        using var e = new GpuEnhancer((a, b, c, d) => fake);
        Assert.False(Run(e, 1280, 720, 1920, 1080).ok);
        Assert.False(e.Active);
        Assert.Contains("no la está aplicando", e.StatusText);
    }

    [Fact]
    public void NoRtxOrErrorsTurnItOffForGood()
    {
        var fake = new FakeGpuScaler { StatusRaw = 0 };
        using var e = new GpuEnhancer((a, b, c, d) => fake);
        Assert.False(Run(e, 1280, 720, 1920, 1080).ok);
        Assert.Contains("no tiene RTX Video", e.StatusText);
        Assert.True(fake.Disposed);
        Assert.False(Run(e, 1280, 720, 1920, 1080, now: 999_999).ok);

        using var e2 = new GpuEnhancer((a, b, c, d) => throw new NotSupportedException("No hay tarjeta NVIDIA en este PC"));
        Assert.False(Run(e2, 1280, 720, 1920, 1080).ok);
        Assert.Equal("No hay tarjeta NVIDIA en este PC", e2.StatusText);

        var failing = new FakeGpuScaler { Fails = true };
        using var e3 = new GpuEnhancer((a, b, c, d) => failing);
        Assert.False(Run(e3, 1280, 720, 1920, 1080).ok);
        Assert.Contains("no pudo procesar", e3.StatusText);
    }

    [Fact]
    public void DownscaleIsSkippedAndSlowGpuFallsBack()
    {
        var fake = new FakeGpuScaler { StatusRaw = Capable | Valid | Enabled | InUse };
        using var e = new GpuEnhancer((a, b, c, d) => fake);
        Assert.False(Run(e, 3840, 2160, 1920, 1080).ok);
        Assert.Contains("Sin efecto", e.StatusText);
        Assert.Equal(0, fake.Processed);

        fake.SleepMs = 25; // más de la mitad de 33 ms
        bool fellBack = false;
        for (int i = 0; i < 15 && !fellBack; i++) fellBack = !Run(e, 640, 360, 1280, 720, now: 0).ok;
        Assert.True(fellBack);
        Assert.Contains("tarda demasiado", e.StatusText);
        Assert.False(e.Active);
    }

    [Fact]
    public void WriterUsesTheGpuOnlyWhenEnabled()
    {
        using var region = new HeapRegion(VCamLayout.TotalBytes);
        using var writer = new VirtualCamWriter(1, region);
        var fake = new FakeGpuScaler { StatusRaw = Capable | Valid | Enabled | InUse | (4u << 4) };
        writer.GpuScalerFactory = (a, b, c, d) => fake;
        var reader = new FakeCameraReader(region);
        reader.Want(1920, 1080);
        var f = new VideoBuffer(VideoBuffer.SizeFor(1280, 720)) { Width = 1280, Height = 720, Stride = 1280, Length = VideoBuffer.SizeFor(1280, 720), FourCC = NdiLib.FourCC_I420 };
        Array.Fill(f.Data, (byte)50);
        Assert.True(writer.Write(f));
        Assert.Equal(50, reader.Read()!.Value.Data[0]);
        Assert.Equal("", writer.EnhanceStatus);
        writer.Enhance = true;
        Assert.True(writer.Write(f));
        Assert.Equal(77, reader.Read()!.Value.Data[0]);
        Assert.True(writer.EnhanceActive);
        Assert.Contains("nivel 4", writer.EnhanceStatus);
        writer.Enhance = false;
        Assert.True(writer.Write(f));
        Assert.Equal(50, reader.Read()!.Value.Data[0]);
        Assert.True(fake.Disposed);
    }
}
