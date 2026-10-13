using System.Threading;
using CamaraNdi.Core.Ndi;

namespace CamaraNdi.Core.VirtualCam;

/// <summary>
/// Escribe las imágenes de un celular en la memoria de "Cámara NDI - Celular N". Sólo trabaja si algún
/// programa (OBS) está mirando esa cámara; si no, no gasta nada.
/// Protocolo (igual que vcam_shm.cpp): 3 búferes; se marca seq=0 en el que se escribe, se copia, se pone
/// su número y recién entonces se publica como el más nuevo. El lector descarta copias a medias.
/// Los tiempos son los de GetTickCount64 (Environment.TickCount64), el mismo reloj en todos los procesos.
/// El escalado (p. ej. 4K -> 1080p) se hace en un hilo propio: el hilo que decodifica sólo deja una copia.
/// </summary>
public sealed unsafe class VirtualCamWriter : IDisposable
{
    private readonly ISharedRegion _region;
    private readonly FrameScaler _scaler = new();
    private readonly byte* _p;
    private ulong _seq;
    private uint _slot;

    // Buzón de una sola imagen: si el hilo todavía no tomó la anterior, se reemplaza (nunca se acumula).
    private readonly object _mail = new();
    private byte[]? _pending;
    private int _pendingW, _pendingH;
    private bool _pendingNv12, _hasPending;
    private byte[]? _work;
    private readonly AutoResetEvent _signal = new(false);
    private Thread? _thread;
    private volatile bool _running = true;

    public int Index { get; }

    /// <summary>Usar la mejora con IA de la GPU si la hay (lo cambia el receptor según la opción).</summary>
    public bool Enhance { get; set; }
    /// <summary>Para pruebas: cómo se crea el escalador de la GPU.</summary>
    public Func<int, int, int, int, IGpuScaler>? GpuScalerFactory { get; set; }
    private GpuEnhancer? _enhancer;
    private byte[]? _nv12Tmp;
    /// <summary>Qué pasa con la mejora con IA ("" si está apagada).</summary>
    public string EnhanceStatus => Enhance ? (_enhancer?.StatusText is { Length: > 0 } t ? t : "Mejora con IA: esperando a que OBS use la cámara") : "";
    public bool EnhanceActive => Enhance && _enhancer?.Active == true;
    public long FramesWritten => Interlocked.Read(ref _framesWritten);
    private long _framesWritten;
    /// <summary>Imágenes que se reemplazaron antes de escribirse (el escalado no alcanzó).</summary>
    public long FramesReplaced { get; private set; }

    public VirtualCamWriter(int index, ISharedRegion region)
    {
        if (region.Length < VCamLayout.TotalBytes) throw new ArgumentException("Región demasiado chica", nameof(region));
        Index = index;
        _region = region;
        _p = region.Pointer;
        if (U32(VCamLayout.HMagic) != VCamLayout.FrameMagic || U32(VCamLayout.HVersion) != VCamLayout.Version)
        {
            SetU32(VCamLayout.HVersion, VCamLayout.Version);
            SetU32(VCamLayout.HHeaderBytes, VCamLayout.HeaderBytes);
            SetU32(VCamLayout.HSlotCount, VCamLayout.Slots);
            SetU32(VCamLayout.HSlotBytes, VCamLayout.SlotBytes);
            SetU32(VCamLayout.HMaxWidth, VCamLayout.MaxWidth);
            SetU32(VCamLayout.HMaxHeight, VCamLayout.MaxHeight);
            Interlocked.MemoryBarrier();
            SetU32(VCamLayout.HMagic, VCamLayout.FrameMagic);
        }
        // Seguir la numeración si la memoria ya tenía imágenes (receptor reiniciado con OBS abierto).
        _seq = Volatile.Read(ref *(ulong*)(_p + VCamLayout.HLatestSeq));
        _slot = U32(VCamLayout.HLatestSlot) % VCamLayout.Slots;
        SetU32(VCamLayout.HWriterPid, (uint)Environment.ProcessId);
    }

    /// <summary>¿Hay un programa usando esta cámara ahora?</summary>
    public bool ConsumerActive()
    {
        if (_disposed) return false;
        long now = Environment.TickCount64;
        long hb = Volatile.Read(ref *(long*)(_p + VCamLayout.HReaderHeartbeat));
        return hb > 0 && now >= hb && now - hb < 3000;
    }

    /// <summary>Tamaño que pidió el programa (0,0 si no pidió nada válido).</summary>
    public (int Width, int Height) Wanted
    {
        get
        {
            int w = (int)U32(VCamLayout.HWantWidth), h = (int)U32(VCamLayout.HWantHeight);
            if (w < 16 || h < 16 || w > VCamLayout.MaxWidth || h > VCamLayout.MaxHeight || (w & 1) != 0 || (h & 1) != 0) return (0, 0);
            return (w, h);
        }
    }

    public void Heartbeat()
    {
        if (!_disposed) Volatile.Write(ref *(long*)(_p + VCamLayout.HWriterHeartbeat), Environment.TickCount64);
    }

    /// <summary>
    /// Entrega una imagen (desde el hilo que decodifica): sólo se copia y se avisa al hilo de la cámara.
    /// Devuelve false si nadie mira la cámara (entonces no se hace nada).
    /// </summary>
    public bool Submit(VideoBuffer frame)
    {
        Heartbeat();
        if (!ConsumerActive() || frame.Width < 2 || frame.Height < 2) return false;
        lock (_mail)
        {
            if (!_running) return false;
            if (_pending == null || _pending.Length < frame.Length)
                _pending = GC.AllocateUninitializedArray<byte>(frame.Length, pinned: true);
            Buffer.BlockCopy(frame.Data, 0, _pending, 0, frame.Length);
            if (_hasPending) FramesReplaced++;
            _pendingW = frame.Width;
            _pendingH = frame.Height;
            _pendingNv12 = frame.FourCC == NdiLib.FourCC_NV12;
            _hasPending = true;
            if (_thread == null)
            {
                _thread = new Thread(Loop) { IsBackground = true, Name = $"Cámara virtual {Index}", Priority = ThreadPriority.AboveNormal };
                _thread.Start();
            }
        }
        _signal.Set();
        return true;
    }

    private void Loop()
    {
        while (_running)
        {
            _signal.WaitOne(500);
            int w, h;
            bool nv12;
            lock (_mail)
            {
                if (!_hasPending || !_running) continue;
                (_pending, _work) = (_work, _pending);
                w = _pendingW;
                h = _pendingH;
                nv12 = _pendingNv12;
                _hasPending = false;
            }
            try
            {
                fixed (byte* src = _work) WriteNow(src, w, h, nv12);
            }
            catch (Exception ex)
            {
                Log.Error($"Cámara virtual {Index}: no se pudo escribir la imagen", ex);
                Thread.Sleep(200);
            }
        }
    }

    /// <summary>Escribe ya mismo, en el hilo que llama (las pruebas lo usan directo).</summary>
    public bool Write(VideoBuffer frame)
    {
        Heartbeat();
        if (!ConsumerActive() || frame.Width < 2 || frame.Height < 2) return false;
        fixed (byte* src = frame.Data) WriteNow(src, frame.Width, frame.Height, frame.FourCC == NdiLib.FourCC_NV12);
        return true;
    }

    private void WriteNow(byte* src, int sw, int sh, bool nv12)
    {
        if (_disposed) return;
        var (tw, th) = Wanted;
        if (tw == 0)
        {
            // Sin pedido: el tamaño del celular, como mucho 4K.
            tw = Math.Min(sw, VCamLayout.MaxWidth) & ~1;
            th = Math.Min(sh, VCamLayout.MaxHeight) & ~1;
        }
        long nowMs = Environment.TickCount64;
        uint slot = (_slot + 1) % VCamLayout.Slots;
        byte* meta = _p + VCamLayout.HSlots + slot * VCamLayout.HSlotSize;
        Volatile.Write(ref *(ulong*)(meta + VCamLayout.SSeq), 0UL); // "escribiendo"
        Interlocked.MemoryBarrier();
        byte* data = _p + VCamLayout.HeaderBytes + (long)slot * VCamLayout.SlotBytes;
        if (!TryEnhance(src, sw, sh, nv12, data, tw, th, nowMs)) _scaler.Convert(src, sw, sh, nv12, data, tw, th);
        *(uint*)(meta + VCamLayout.SWidth) = (uint)tw;
        *(uint*)(meta + VCamLayout.SHeight) = (uint)th;
        *(uint*)(meta + VCamLayout.SFormat) = VCamLayout.FormatNV12;
        *(long*)(meta + VCamLayout.STimestamp) = nowMs;
        ulong seq = ++_seq;
        Interlocked.MemoryBarrier();
        Volatile.Write(ref *(ulong*)(meta + VCamLayout.SSeq), seq);
        SetU32(VCamLayout.HLatestSlot, slot);
        Interlocked.MemoryBarrier();
        Volatile.Write(ref *(ulong*)(_p + VCamLayout.HLatestSeq), seq);
        Volatile.Write(ref *(long*)(_p + VCamLayout.HWriterHeartbeat), nowMs);
        _slot = slot;
        Interlocked.Increment(ref _framesWritten);
    }

    private bool TryEnhance(byte* src, int sw, int sh, bool nv12, byte* dst, int tw, int th, long nowMs)
    {
        if (!Enhance)
        {
            if (_enhancer != null) { _enhancer.Dispose(); _enhancer = null; }
            return false;
        }
        _enhancer ??= new GpuEnhancer(GpuScalerFactory ?? ((a, b, c, d) => new D3D11VsrScaler(a, b, c, d)));
        if ((sw & 1) != 0 || (sh & 1) != 0) return false;
        byte* input = src;
        if (!nv12)
        {
            int need = sw * sh * 3 / 2;
            if (_nv12Tmp == null || _nv12Tmp.Length < need) _nv12Tmp = GC.AllocateUninitializedArray<byte>(need, pinned: true);
            fixed (byte* t = _nv12Tmp)
            {
                FrameScaler.CopySameSize(src, sw, sh, false, t);
                input = t; // arreglo "pinned": el puntero sigue valiendo fuera del fixed
            }
        }
        uint fps100 = U32(VCamLayout.HWantFps100);
        double interval = fps100 >= 100 ? 100000.0 / fps100 : 1000.0 / 30;
        return _enhancer.TryWrite(input, sw, sh, dst, tw, th, interval, nowMs);
    }

    private uint U32(int off) => Volatile.Read(ref *(uint*)(_p + off));
    private void SetU32(int off, uint v) => Volatile.Write(ref *(uint*)(_p + off), v);

    private volatile bool _disposed;

    public void Dispose()
    {
        lock (_mail)
        {
            if (!_running) return;
            _running = false;
        }
        _signal.Set();
        _thread?.Join(2000);
        _disposed = true;
        Volatile.Write(ref *(long*)(_p + VCamLayout.HWriterHeartbeat), 0L);
        _scaler.Dispose();
        _enhancer?.Dispose();
        _region.Dispose();
        _signal.Dispose();
    }
}
