using System.Diagnostics;

namespace CamaraNdi.Core.VirtualCam;

/// <summary>
/// Estado que informa el controlador de NVIDIA sobre "RTX Video Super Resolution" para un procesador de video
/// (4 bytes que devuelve VideoProcessorGetStreamExtension; los mismos bits que lee Firefox). No está documentado:
/// si algo no cuadra, se toma como "no activa".
/// </summary>
public readonly record struct VsrStatus(uint Raw)
{
    /// <summary>La tarjeta tiene RTX Video Super Resolution.</summary>
    public bool Capable => (Raw & 1) != 0;
    /// <summary>Los demás campos son válidos (hace falta un procesado previo con la mejora pedida).</summary>
    public bool Valid => (Raw & 2) != 0;
    /// <summary>Activada en el panel de NVIDIA (RTX Video Enhancement → Super resolución).</summary>
    public bool Enabled => (Raw & 4) != 0;
    /// <summary>De verdad se está aplicando a ESTE procesador (a esta cámara).</summary>
    public bool InUse => (Raw & 8) != 0;
    /// <summary>Nivel de calidad elegido en el panel (1..4).</summary>
    public int Level => (int)((Raw >> 4) & 7);
}

/// <summary>Un escalador por GPU con la mejora de IA del fabricante (ver <see cref="D3D11VsrScaler"/>).</summary>
public unsafe interface IGpuScaler : IDisposable
{
    /// <summary>NV12 contiguo (stride = ancho) de sw x sh a NV12 contiguo de tw x th. False si falló.</summary>
    bool Process(byte* nv12, int sw, int sh, byte* dst, int tw, int th);
    /// <summary>Lo que dice el controlador después del último procesado (null si no se puede saber).</summary>
    VsrStatus? Status();
}

/// <summary>
/// Decide, imagen por imagen, si se usa la mejora con IA de la GPU o el escalado normal, y explica por qué.
/// Nunca dice "activa" porque sí: sólo cuando el controlador confirma que la está aplicando a esta cámara.
/// Ante cualquier error, lentitud o duda vuelve sola al escalado normal (que es el de siempre).
/// </summary>
public sealed unsafe class GpuEnhancer : IDisposable
{
    private readonly Func<int, int, int, int, IGpuScaler> _create;
    private IGpuScaler? _scaler;
    private (int sw, int sh, int tw, int th) _size;
    private bool _off;              // apagada hasta que se cree otro GpuEnhancer (error que no se arregla solo)
    private long _retryAt;          // para estados que pueden cambiar (panel de NVIDIA, la GPU no la aplicó)
    private int _framesSinceCheck;
    private int _errors;            // errores seguidos de la GPU (pasajeros hasta 3)
    private int _slowStrikes;       // veces que fue lenta con este mismo tamaño
    private bool _slowOff;          // lenta dos veces: no se reintenta hasta que cambie el tamaño
    private readonly Queue<double> _times = new();

    /// <summary>Texto para la tarjeta del celular (vacío si todavía no se intentó).</summary>
    public string StatusText { get; private set; } = "";
    /// <summary>¿La imagen que va a OBS está pasando por la IA ahora mismo?</summary>
    public bool Active { get; private set; }

    /// <summary>Cada cuánto se vuelve a probar cuando la GPU no la aplicó o está apagada en el panel.</summary>
    public int RetryMs { get; set; } = 5000;

    public GpuEnhancer(Func<int, int, int, int, IGpuScaler> create) => _create = create;

    /// <summary>¿Vale la pena preparar la imagen (por ejemplo pasar I420 a NV12) para <see cref="TryWrite"/>?</summary>
    public bool WouldTry(int sw, int sh, int tw, int th, long nowMs)
    {
        if (_off) return false;
        if (tw < sw || th < sh)
        {
            DropScaler();
            Fail("Sin efecto: OBS pide menos resolución que la del celular (la IA sólo sirve para agrandar o limpiar)");
            return false;
        }
        if (_slowOff && _size == (sw, sh, tw, th)) return false;
        return Active || nowMs >= _retryAt;
    }

    /// <summary>
    /// Intenta escribir la imagen mejorada en <paramref name="dst"/>. Devuelve false si hay que usar el escalado
    /// normal (y entonces <paramref name="dst"/> se sobreescribe con él).
    /// </summary>
    public bool TryWrite(byte* nv12, int sw, int sh, byte* dst, int tw, int th, double frameIntervalMs, long nowMs)
    {
        if (!WouldTry(sw, sh, tw, th, nowMs)) return false;
        try
        {
            if (_scaler == null || _size != (sw, sh, tw, th))
            {
                DropScaler();
                if (_size != (sw, sh, tw, th)) { _slowStrikes = 0; _slowOff = false; }
                _scaler = _create(sw, sh, tw, th);
                _size = (sw, sh, tw, th);
                _times.Clear();
                _framesSinceCheck = int.MaxValue; // revisar el estado después del primer procesado
            }
            var sw0 = Stopwatch.GetTimestamp();
            if (!_scaler.Process(nv12, sw, sh, dst, tw, th))
                return GpuError(nowMs, "La GPU no pudo procesar la imagen");
            double ms = Stopwatch.GetElapsedTime(sw0).TotalMilliseconds;

            if (!Active || ++_framesSinceCheck >= 30)
            {
                _framesSinceCheck = 0;
                var st = _scaler.Status();
                if (st == null) return TurnOff("No se puede comprobar si la GPU aplica la IA: se usa el escalado normal");
                var s = st.Value;
                if (!s.Capable) return TurnOff("Esta tarjeta NVIDIA no tiene RTX Video (hace falta una GeForce RTX)");
                if (!s.Enabled)
                    return Later(nowMs, "Apagada en el panel de NVIDIA: activa «Mejora de video RTX» → «Súper resolución» (NVIDIA App o Panel de control)");
                if (!s.InUse)
                    return Later(nowMs, "La GPU no la está aplicando a esta cámara (el controlador de NVIDIA decide; se vuelve a probar sola)");
                Active = true;
                StatusText = $"Activa: NVIDIA RTX Video Super Resolution, nivel {s.Level}";
            }

            _times.Enqueue(ms);
            if (_times.Count > 30) _times.Dequeue();
            if (_times.Count >= 10 && frameIntervalMs > 0)
            {
                double median = _times.OrderBy(t => t).ElementAt(_times.Count / 2);
                if (median > frameIntervalMs * 0.5)
                {
                    _times.Clear();
                    if (++_slowStrikes >= 2)
                    {
                        _slowOff = true;
                        DropScaler();
                        return Fail($"La GPU tarda demasiado ({median:F0} ms por imagen) con este tamaño: se usa el escalado normal");
                    }
                    return Later(nowMs, $"La GPU tarda demasiado ({median:F0} ms por imagen): se usa el escalado normal y se vuelve a probar", 30_000);
                }
            }
            _errors = 0;
            return true;
        }
        catch (Exception ex) when (ex is NotSupportedException or ArgumentException or PlatformNotSupportedException or DllNotFoundException)
        {
            Log.Warn("Mejora con IA de la GPU: " + ex.Message);
            return TurnOff(ex.Message);
        }
        catch (Exception ex)
        {
            Log.Warn("Mejora con IA de la GPU: " + ex.Message);
            return GpuError(nowMs, "Error de la GPU: " + ex.Message);
        }
    }

    /// <summary>Error que puede ser pasajero (por ejemplo el controlador se reinició): se reintenta, hasta 3 seguidos.</summary>
    private bool GpuError(long nowMs, string text)
    {
        DropScaler();
        if (++_errors >= 3) return TurnOff(text + ": se usa el escalado normal");
        return Later(nowMs, text + ": se usa el escalado normal y se vuelve a probar", 30_000);
    }

    private bool Fail(string? text)
    {
        Active = false;
        if (text != null) StatusText = text;
        return false;
    }

    private bool Later(long nowMs, string text, int delayMs = 0)
    {
        _retryAt = nowMs + (delayMs > 0 ? delayMs : RetryMs);
        if (StatusText != text) Log.Info("Mejora con IA de la GPU: " + text);
        return Fail(text);
    }

    private bool TurnOff(string text)
    {
        _off = true;
        DropScaler();
        if (StatusText != text) Log.Info("Mejora con IA de la GPU: " + text);
        return Fail(text);
    }

    private void DropScaler()
    {
        _scaler?.Dispose();
        _scaler = null;
        Active = false;
    }

    public void Dispose() => DropScaler();
}
