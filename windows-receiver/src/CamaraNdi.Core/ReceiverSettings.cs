using System.Text.Json;

namespace CamaraNdi.Core;

public sealed class ReceiverSettings
{
    /// <summary>Espera máxima por un frame incompleto (30–150 ms).</summary>
    public int JitterMs { get; set; } = 60;
    /// <summary>Si es true, cada celular elige el buffer según su "modo de latencia".</summary>
    public bool UsePhoneLatencyMode { get; set; } = true;
    public int MaxPhones { get; set; } = 4;
    public int MediaPort { get; set; } = Protocol.DefaultMediaPort;
    public int DiscoveryPort { get; set; } = Protocol.DefaultDiscoveryPort;
    /// <summary>"auto" (GPU si hay, si no CPU), "gpu" o "cpu".</summary>
    public string Decoder { get; set; } = "auto";
    public bool StartWithWindows { get; set; }
    public bool MinimizeToTray { get; set; } = true;
    public bool StartMinimized { get; set; }
    /// <summary>Nombre NDI personalizado por deviceId.</summary>
    public Dictionary<string, string> Names { get; set; } = new();

    public static int JitterForMode(string? mode, int fallback) => mode switch
    {
        "minima" => 40,
        "equilibrada" => 70,
        "calidad" => 110,
        _ => fallback,
    };

    /// <summary>Retardo extra de suavizado (reparto uniforme de frames) según el modo.</summary>
    public static int SmoothingForMode(string? mode) => mode switch
    {
        "equilibrada" => 15,
        "calidad" => 35,
        _ => 0,
    };

    private static readonly JsonSerializerOptions Opts = new() { WriteIndented = true };

    public static ReceiverSettings Load(string path)
    {
        try
        {
            if (File.Exists(path))
                return JsonSerializer.Deserialize<ReceiverSettings>(File.ReadAllText(path), Opts) ?? new();
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
