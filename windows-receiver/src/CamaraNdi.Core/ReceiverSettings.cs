using System.Text.Json;

namespace CamaraNdi.Core;

public sealed class ReceiverSettings
{
    /// <summary>Espera máxima por un frame incompleto (30–1000 ms).</summary>
    public int JitterMs { get; set; } = 60;
    /// <summary>Si es true, cada celular elige el buffer según su "modo de latencia".</summary>
    public bool UsePhoneLatencyMode { get; set; } = true;
    public int MaxPhones { get; set; } = 4;
    public int MediaPort { get; set; } = Protocol.DefaultMediaPort;
    public int DiscoveryPort { get; set; } = Protocol.DefaultDiscoveryPort;
    /// <summary>"auto" (GPU si hay, si no CPU), "gpu" o "cpu".</summary>
    public string Decoder { get; set; } = "auto";
    /// <summary>Si el PC no alcanza a decodificar, pedirle al celular menos resolución (si no, sólo avisa).</summary>
    public bool AutoLowerResolution { get; set; } = true;
    public bool StartWithWindows { get; set; }
    public bool MinimizeToTray { get; set; } = true;
    public bool StartMinimized { get; set; }
    /// <summary>Nombre NDI personalizado por deviceId.</summary>
    public Dictionary<string, string> Names { get; set; } = new();
    /// <summary>Rango de color de entrada por deviceId: "auto", "full" o "limited".</summary>
    public Dictionary<string, string> RangeOverrides { get; set; } = new();
    /// <summary>
    /// Cámaras virtuales para OBS ("Cámara NDI - Celular 1..4", como DroidCam): el camino principal,
    /// no necesita NDI ni plugins.
    /// </summary>
    public bool VirtualCameras { get; set; } = true;
    /// <summary>Qué cámara virtual (1..4) usa cada celular (deviceId), para que no cambie entre sesiones.</summary>
    public Dictionary<string, int> CameraSlots { get; set; } = new();
    /// <summary>Publicar también por NDI (sólo si el NDI Runtime está instalado; sirve para mandar a otro PC).</summary>
    public bool NdiOutput { get; set; }
    /// <summary>
    /// Mejora con IA de la GPU (NVIDIA RTX Video Super Resolution) para lo que va a las cámaras virtuales.
    /// Experimental y apagada por defecto: sólo actúa si el controlador confirma que la aplica.
    /// </summary>
    public bool AiEnhance { get; set; }

    /// <summary>
    /// Cuánto se espera a un paquete perdido antes de dar el frame por perdido.
    /// En los modos de calidad se espera mucho más: casi nunca se pierde un frame
    /// (y no hace falta pedir keyframes, que son los que "ensucian" la imagen).
    /// </summary>
    public static int JitterForMode(string? mode, int fallback) => mode switch
    {
        "minima" => 40,
        "equilibrada" => 70,
        "calidad" => 300,
        "maxima" => 800,
        _ => fallback,
    };

    /// <summary>Retardo extra de suavizado (reparto uniforme de frames) según el modo.</summary>
    public static int SmoothingForMode(string? mode) => mode switch
    {
        "equilibrada" => 15,
        "calidad" => 100,
        "maxima" => 300,
        _ => 0,
    };

    public static bool IsQualityMode(string? mode) => mode is "calidad" or "maxima";

    private static readonly JsonSerializerOptions Opts = new() { WriteIndented = true };

    public static ReceiverSettings Load(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                string text = File.ReadAllText(path);
                var s = JsonSerializer.Deserialize<ReceiverSettings>(text, Opts) ?? new();
                // Configuración de una versión anterior (sólo NDI): se sigue publicando por NDI para que las
                // fuentes NDI que ya están en OBS no se queden negras al actualizar. Se puede desmarcar.
                using var doc = JsonDocument.Parse(text);
                if (!doc.RootElement.EnumerateObject().Any(p => p.NameEquals("NdiOutput") || string.Equals(p.Name, "ndiOutput", StringComparison.OrdinalIgnoreCase)))
                    s.NdiOutput = true;
                return s;
            }
        }
        catch (Exception ex) { Log.Error("No se pudo leer la configuración", ex); }
        return new();
    }

    public void Save(string path)
    {
        try
        {
            System.IO.Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(this, Opts));
        }
        catch (Exception ex) { Log.Error("No se pudo guardar la configuración", ex); }
    }
}
