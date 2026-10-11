using System.Text;

namespace CamaraNdi.Core.Ndi;

/// <summary>Una fuente NDI (una por celular). Envío asíncrono con doble buffer.</summary>
public sealed unsafe class NdiSender : IDisposable
{
    private IntPtr _instance;
    private VideoBuffer? _inFlight;
    private readonly object _gate = new();

    public string Name { get; }

    private NdiSender(string name, IntPtr instance)
    {
        Name = name;
        _instance = instance;
    }

    public static NdiSender? TryCreate(string name)
    {
        if (!NdiLib.EnsureLoaded()) return null;
        var nameBytes = Encoding.UTF8.GetBytes(name + "\0");
        fixed (byte* pName = nameBytes)
        {
            var create = new NdiLib.SendCreate
            {
                p_ndi_name = pName,
                p_groups = null,
                clock_video = 0, // enviamos en cuanto hay frame: mínima latencia
                clock_audio = 0,
            };
            var inst = NdiLib.SendCreateFn(&create);
            if (inst == IntPtr.Zero)
            {
                Log.Error($"NDI: no se pudo crear la fuente \"{name}\"");
                return null;
            }
            Log.Info($"NDI: fuente creada \"{name}\"");
            return new NdiSender(name, inst);
        }
    }

    /// <summary>
    /// Envía el frame. NDI usa el buffer hasta la siguiente llamada, por eso se devuelve
    /// el buffer anterior (ya liberado por NDI) para reciclarlo.
    /// </summary>
    public VideoBuffer? Send(VideoBuffer buf, int fpsN, int fpsD)
    {
        lock (_gate)
        {
            if (_instance == IntPtr.Zero) return buf;
            fixed (byte* p = buf.Data)
            {
                var f = new NdiLib.VideoFrameV2
                {
                    xres = buf.Width,
                    yres = buf.Height,
                    FourCC = buf.FourCC,
                    frame_rate_N = fpsN,
                    frame_rate_D = fpsD,
                    picture_aspect_ratio = (float)buf.Width / buf.Height,
                    frame_format_type = NdiLib.FrameFormatProgressive,
                    timecode = NdiLib.TimecodeSynthesize,
                    p_data = p,
                    line_stride_in_bytes = buf.Stride,
                    p_metadata = null,
                    timestamp = 0,
                };
                // El array es "pinned" (GC.AllocateUninitializedArray pinned:true), así que el
                // puntero sigue siendo válido después de salir del bloque fixed.
                NdiLib.SendVideoAsyncV2(_instance, &f);
            }
            var released = _inFlight;
            _inFlight = buf;
            return released;
        }
    }

    public int Connections
    {
        get
        {
            lock (_gate) return _instance == IntPtr.Zero ? 0 : NdiLib.SendGetNoConnections(_instance, 0);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_instance == IntPtr.Zero) return;
            try
            {
                NdiLib.SendVideoAsyncV2(_instance, null); // vacía el envío asíncrono
                NdiLib.SendDestroy(_instance);
            }
            catch (Exception ex) { Log.Error("NDI: error al cerrar la fuente", ex); }
            _instance = IntPtr.Zero;
            _inFlight = null;
            Log.Info($"NDI: fuente cerrada \"{Name}\"");
        }
    }
}
