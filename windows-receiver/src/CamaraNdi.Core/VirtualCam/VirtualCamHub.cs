using System.Threading;

namespace CamaraNdi.Core.VirtualCam;

/// <summary>
/// Las cámaras virtuales "Cámara NDI - Celular 1..4" del lado del receptor: el bloque de control (receptor
/// vivo, qué celular va en cada cámara) y un <see cref="VirtualCamWriter"/> por cámara en uso.
/// </summary>
public sealed unsafe class VirtualCamHub : IDisposable
{
    private readonly Func<string, long, ISharedRegion> _open;
    private readonly ISharedRegion _control;
    private readonly byte* _c;
    private readonly VirtualCamWriter?[] _writers = new VirtualCamWriter?[VCamLayout.CameraCount];
    private readonly object _gate = new();

    /// <param name="open">Abre/crea una región por nombre y tamaño (Windows: memoria compartida; pruebas: memoria común).</param>
    public VirtualCamHub(Func<string, long, ISharedRegion> open)
    {
        _open = open;
        _control = open(VCamLayout.ControlName, VCamLayout.ControlBytes);
        _c = _control.Pointer;
        SetU32(VCamLayout.CVersion, VCamLayout.Version);
        SetU32(VCamLayout.CCameraCount, VCamLayout.CameraCount);
        SetU32(VCamLayout.CReceiverPid, (uint)Environment.ProcessId);
        for (int i = 1; i <= VCamLayout.CameraCount; i++) SetCamera(i, VCamLayout.StateEmpty, "");
        Interlocked.MemoryBarrier();
        SetU32(VCamLayout.CMagic, VCamLayout.ControlMagic);
        Heartbeat();
    }

    /// <summary>En Windows usa memoria compartida real; en otros sistemas devuelve null (no hay cámaras).</summary>
    public static VirtualCamHub? CreateForThisPc()
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            return new VirtualCamHub((name, size) => WindowsSharedRegion.Create(name, size));
        }
        catch (Exception ex)
        {
            Log.Error("No se pudo preparar la memoria de las cámaras virtuales", ex);
            return null;
        }
    }

    /// <summary>
    /// El receptor está vivo (la cámara muestra "Abre el receptor" si esto se detiene). Llamar cada ~1 s.
    /// Usa GetTickCount64 (Environment.TickCount64): el mismo reloj que ve la cámara dentro de OBS.
    /// </summary>
    public void Heartbeat()
    {
        lock (_gate)
        {
            if (_disposed) return;
            Volatile.Write(ref *(long*)(_c + VCamLayout.CHeartbeat), Environment.TickCount64);
            foreach (var w in _writers) w?.Heartbeat();
        }
    }

    /// <summary>Estado y nombre que muestra el cartel de espera de la cámara (1..4).</summary>
    public void SetCamera(int index, uint state, string name)
    {
        if (index < 1 || index > VCamLayout.CameraCount) return;
        lock (_gate)
        {
            if (_disposed) return;
            byte* cam = _c + VCamLayout.CCameras + (index - 1) * VCamLayout.CCameraSize;
            ushort* n = (ushort*)(cam + VCamLayout.CCamName);
            int len = Math.Min(name.Length, VCamLayout.NameChars - 1);
            for (int i = 0; i < len; i++) n[i] = name[i];
            for (int i = len; i < VCamLayout.NameChars; i++) n[i] = 0;
            Volatile.Write(ref *(uint*)(cam + VCamLayout.CCamState), state);
        }
    }

    /// <summary>El escritor de la cámara (lo crea la primera vez: reserva ~37 MB de memoria compartida).</summary>
    public VirtualCamWriter? Writer(int index)
    {
        if (index < 1 || index > VCamLayout.CameraCount) return null;
        lock (_gate)
        {
            if (_disposed) return null;
            var w = _writers[index - 1];
            if (w != null) return w;
            try
            {
                w = new VirtualCamWriter(index, _open(VCamLayout.FrameName(index), VCamLayout.TotalBytes));
                _writers[index - 1] = w;
                Log.Info($"Cámara virtual \"{VCamLayout.CameraName(index)}\" lista");
            }
            catch (Exception ex)
            {
                Log.Error($"No se pudo preparar la cámara virtual {index}", ex);
            }
            return w;
        }
    }

    /// <summary>¿Algún programa (OBS) está usando esta cámara?</summary>
    public bool InUse(int index)
    {
        lock (_gate)
            return index >= 1 && index <= VCamLayout.CameraCount && _writers[index - 1]?.ConsumerActive() == true;
    }

    private void SetU32(int off, uint v) => Volatile.Write(ref *(uint*)(_c + off), v);

    private bool _disposed;

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            for (int i = 0; i < _writers.Length; i++)
            {
                _writers[i]?.Dispose();
                _writers[i] = null;
            }
            for (int i = 1; i <= VCamLayout.CameraCount; i++)
                Volatile.Write(ref *(uint*)(_c + VCamLayout.CCameras + (i - 1) * VCamLayout.CCameraSize + VCamLayout.CCamState), VCamLayout.StateEmpty);
            Volatile.Write(ref *(long*)(_c + VCamLayout.CHeartbeat), 0L);
            _control.Dispose();
        }
    }
}
