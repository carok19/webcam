using System.Runtime.InteropServices;
using SharpGen.Runtime;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice;
using Vortice.Mathematics;

namespace CamaraNdi.Core.VirtualCam;

/// <summary>
/// Escala NV12 con el procesador de video de Direct3D 11 de una tarjeta NVIDIA y le pide al controlador
/// "RTX Video Super Resolution" (la súper resolución con IA de las GeForce RTX).
/// La extensión no está documentada por NVIDIA: se usa exactamente como la usan mpv, Chromium, Firefox, VLC y Kodi
/// (GUID {D43CE1B3-…}, {versión 1, método 2, activar}). Que la llamada devuelva "OK" no prueba nada: por eso
/// <see cref="GpuEnhancer"/> sólo la da por activa si el controlador dice que la está aplicando.
/// Sin mejoras automáticas del controlador (para no cambiar el aspecto natural de la imagen).
/// </summary>
public sealed unsafe class D3D11VsrScaler : IGpuScaler
{
    public const uint VendorNvidia = 0x10DE, VendorMicrosoft = 0x1414;
    private static readonly Guid NvidiaPpe = new(0xd43ce1b3, 0x1f4b, 0x48ac, 0xba, 0xee, 0xc3, 0xc2, 0x53, 0x75, 0xe6, 0xf7);

    [StructLayout(LayoutKind.Sequential)]
    private struct NvidiaStreamExt
    {
        public uint Version, Method, Enable;
    }

    private readonly ID3D11Device _dev;
    private readonly ID3D11DeviceContext _ctx;
    private readonly ID3D11VideoDevice _vdev;
    private readonly ID3D11VideoContext _vctx;
    private readonly ID3D11VideoProcessorEnumerator _enum;
    private readonly ID3D11VideoProcessor _vp;
    private readonly ID3D11Texture2D _inStaging, _inTex, _outTex, _outStaging;
    private readonly ID3D11VideoProcessorInputView _inView;
    private readonly ID3D11VideoProcessorOutputView _outView;
    private readonly int _sw, _sh, _tw, _th;
    private readonly bool _vsr;
    private uint _frame;

    /// <param name="requireNvidia">false sólo para la prueba del procesador de video en una GPU cualquiera (sin IA).</param>
    public D3D11VsrScaler(int sw, int sh, int tw, int th, bool requireNvidia = true)
    {
        if (!OperatingSystem.IsWindows()) throw new NotSupportedException("La mejora con IA de la GPU sólo existe en Windows");
        if (((sw | sh | tw | th) & 1) != 0) throw new ArgumentException("NV12 necesita tamaños pares");
        (_sw, _sh, _tw, _th) = (sw, sh, tw, th);

        using var adapter = (requireNvidia ? FindNvidia(out string why) : FindAny(out why))
                            ?? throw new NotSupportedException(why);
        _vsr = adapter.Description1.VendorId == VendorNvidia;
        D3D11.D3D11CreateDevice(adapter, DriverType.Unknown, DeviceCreationFlags.VideoSupport | DeviceCreationFlags.BgraSupport,
            new[] { FeatureLevel.Level_11_1, FeatureLevel.Level_11_0 }, out _dev!, out _ctx!).CheckError();
        _vdev = _dev.QueryInterfaceOrNull<ID3D11VideoDevice>()
                ?? throw new NotSupportedException("Esta GPU no tiene procesador de video de Direct3D 11");
        _vctx = _ctx.QueryInterface<ID3D11VideoContext>();

        _enum = _vdev.CreateVideoProcessorEnumerator(new VideoProcessorContentDescription
        {
            InputFrameFormat = VideoFrameFormat.Progressive,
            InputFrameRate = new Rational(30, 1), InputWidth = (uint)sw, InputHeight = (uint)sh,
            OutputFrameRate = new Rational(30, 1), OutputWidth = (uint)tw, OutputHeight = (uint)th,
            Usage = VideoUsage.PlaybackNormal,
        });
        var nv12 = _enum.CheckVideoProcessorFormat(Format.NV12);
        if ((nv12 & VideoProcessorFormatSupport.Input) == 0 || (nv12 & VideoProcessorFormatSupport.Output) == 0)
            throw new NotSupportedException("El procesador de video de esta GPU no acepta NV12");
        _vp = _vdev.CreateVideoProcessor(_enum, 0);

        _vctx.VideoProcessorSetStreamAutoProcessingMode(_vp, 0, false);
        _vctx.VideoProcessorSetStreamFrameFormat(_vp, 0, VideoFrameFormat.Progressive);
        _vctx.VideoProcessorSetStreamSourceRect(_vp, 0, true, new RawRect(0, 0, sw, sh));
        _vctx.VideoProcessorSetStreamDestRect(_vp, 0, true, new RawRect(0, 0, tw, th));
        _vctx.VideoProcessorSetOutputTargetRect(_vp, true, new RawRect(0, 0, tw, th));
        // El receptor ya entrega BT.709 rango limitado: entra y sale igual (sin corrimientos de color).
        using (var v1 = _vctx.QueryInterfaceOrNull<ID3D11VideoContext1>())
        {
            v1?.VideoProcessorSetStreamColorSpace1(_vp, 0, ColorSpaceType.YcbcrStudioG22LeftP709);
            v1?.VideoProcessorSetOutputColorSpace1(_vp, ColorSpaceType.YcbcrStudioG22LeftP709);
        }

        _inStaging = Texture(sw, sh, ResourceUsage.Staging, BindFlags.None, CpuAccessFlags.Write);
        _inTex = Texture(sw, sh, ResourceUsage.Default, BindFlags.None, CpuAccessFlags.None);
        _outTex = Texture(tw, th, ResourceUsage.Default, BindFlags.RenderTarget, CpuAccessFlags.None);
        _outStaging = Texture(tw, th, ResourceUsage.Staging, BindFlags.None, CpuAccessFlags.Read);
        _inView = _vdev.CreateVideoProcessorInputView(_inTex, _enum, new VideoProcessorInputViewDescription
        {
            FourCC = 0,
            ViewDimension = VideoProcessorInputViewDimension.Texture2D,
            Texture2D = new Texture2DVideoProcessorInputView { MipSlice = 0, ArraySlice = 0 },
        });
        _outView = _vdev.CreateVideoProcessorOutputView(_outTex, _enum, new VideoProcessorOutputViewDescription
        {
            ViewDimension = VideoProcessorOutputViewDimension.Texture2D,
            Texture2D = new Texture2DVideoProcessorOutputView { MipSlice = 0 },
        });

        if (_vsr)
        {
            var ext = new NvidiaStreamExt { Version = 1, Method = 2, Enable = 1 };
            _vctx.VideoProcessorSetStreamExtension(_vp, 0, NvidiaPpe, (uint)sizeof(NvidiaStreamExt), (IntPtr)(&ext)).CheckError();
        }
    }

    private ID3D11Texture2D Texture(int w, int h, ResourceUsage usage, BindFlags bind, CpuAccessFlags cpu) =>
        _dev.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)w, Height = (uint)h, MipLevels = 1, ArraySize = 1, Format = Format.NV12,
            SampleDescription = new SampleDescription(1, 0), Usage = usage, BindFlags = bind, CPUAccessFlags = cpu,
        });

    public bool Process(byte* nv12, int sw, int sh, byte* dst, int tw, int th)
    {
        if (sw != _sw || sh != _sh || tw != _tw || th != _th) return false;
        // Subir: filas de Y y luego las de UV, que empiezan en RowPitch * alto.
        var m = _ctx.Map(_inStaging, 0, MapMode.Write, Vortice.Direct3D11.MapFlags.None);
        byte* p = (byte*)m.DataPointer;
        long pitch = m.RowPitch;
        for (int y = 0; y < sh; y++) Buffer.MemoryCopy(nv12 + (long)y * sw, p + y * pitch, pitch, sw);
        byte* srcUv = nv12 + (long)sw * sh;
        byte* pUv = p + pitch * sh;
        for (int y = 0; y < sh / 2; y++) Buffer.MemoryCopy(srcUv + (long)y * sw, pUv + y * pitch, pitch, sw);
        _ctx.Unmap(_inStaging, 0);
        _ctx.CopyResource(_inTex, _inStaging);

        var stream = new VideoProcessorStream { Enable = true, InputSurface = _inView, OutputIndex = 0, InputFrameOrField = _frame };
        var hr = _vctx.VideoProcessorBlt(_vp, _outView, _frame, 1, new[] { stream });
        _frame++;
        if (hr.Failure) return false;

        // Bajar: espera a que la GPU termine (suma el tiempo de GPU, no un cuadro entero).
        _ctx.CopyResource(_outStaging, _outTex);
        var r = _ctx.Map(_outStaging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
        byte* q = (byte*)r.DataPointer;
        long rp = r.RowPitch;
        for (int y = 0; y < th; y++) Buffer.MemoryCopy(q + y * rp, dst + (long)y * tw, tw, tw);
        byte* qUv = q + rp * th;
        byte* dUv = dst + (long)tw * th;
        for (int y = 0; y < th / 2; y++) Buffer.MemoryCopy(qUv + y * rp, dUv + (long)y * tw, tw, tw);
        _ctx.Unmap(_outStaging, 0);
        return true;
    }

    public VsrStatus? Status()
    {
        if (!_vsr) return null;
        uint raw = 0;
        var hr = _vctx.VideoProcessorGetStreamExtension(_vp, 0, NvidiaPpe, sizeof(uint), (IntPtr)(&raw));
        return hr.Success ? new VsrStatus(raw) : null;
    }

    /// <summary>La tarjeta NVIDIA (no la integrada ni la de software) con controlador 530 o más nuevo.</summary>
    private static IDXGIAdapter1? FindNvidia(out string why)
    {
        why = "No hay tarjeta NVIDIA en este PC: la mejora con IA (RTX Video) sólo existe en GeForce RTX";
        using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
        for (uint i = 0; factory.EnumAdapters1(i, out var a).Success; i++)
        {
            var d = a.Description1;
            if (d.VendorId == VendorNvidia && (d.Flags & AdapterFlags.Software) == 0)
            {
                double? ver = DriverVersion(a);
                if (ver is double v && v < 530)
                {
                    why = $"El controlador de NVIDIA es viejo ({v:F2}): RTX Video necesita la versión 531 o más nueva";
                    a.Dispose();
                    continue;
                }
                return a;
            }
            a.Dispose();
        }
        return null;
    }

    private static IDXGIAdapter1? FindAny(out string why)
    {
        why = "No hay ninguna GPU";
        using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
        return factory.EnumAdapters1(0, out var a).Success ? a : null;
    }

    /// <summary>Versión "de NVIDIA" (p. ej. 560.94 para el controlador de Windows 32.0.15.6094).</summary>
    public static double? DriverVersion(IDXGIAdapter1 adapter)
    {
        if (!adapter.CheckInterfaceSupport<IDXGIDevice>(out long umd)) return null;
        return NvidiaVersionFromUmd(umd);
    }

    /// <summary>32.0.15.6094 → 560.94 (los 5 últimos dígitos de las dos últimas partes).</summary>
    public static double NvidiaVersionFromUmd(long umd)
    {
        uint revision = (uint)((umd >> 16) & 0xFFFF);
        uint build = (uint)(umd & 0xFFFF);
        return ((revision % 10) * 10000 + build) / 100.0;
    }

    /// <summary>Una línea para la interfaz: qué GPU hay y si puede tener la mejora con IA.</summary>
    public static string Describe()
    {
        if (!OperatingSystem.IsWindows()) return "Mejora con IA: sólo en Windows";
        try
        {
            using var a = FindNvidia(out string why);
            if (a == null) return why;
            double? v = DriverVersion(a);
            return $"{a.Description1.Description.Trim()} (controlador {(v is double x ? x.ToString("F2") : "?")}): puede tener RTX Video Super Resolution";
        }
        catch (Exception ex)
        {
            return "No se pudo revisar la GPU: " + ex.Message;
        }
    }

    public void Dispose()
    {
        _outView.Dispose();
        _inView.Dispose();
        _outStaging.Dispose();
        _outTex.Dispose();
        _inTex.Dispose();
        _inStaging.Dispose();
        _vp.Dispose();
        _enum.Dispose();
        _vctx.Dispose();
        _vdev.Dispose();
        _ctx.Dispose();
        _dev.Dispose();
    }
}
