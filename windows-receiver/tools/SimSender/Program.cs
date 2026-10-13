using CamaraNdi.Core;
using CamaraNdi.Core.VirtualCam;
using CamaraNdi.Sim;

// Uso: SimSender [--host 192.168.1.10] [--port 47810] [--size 1920x1080] [--fps 30]
//                [--kbps 6000] [--codec h264|hevc] [--loss 0.02] [--seconds 60] [--name "Sim 1"]
//      SimSender --receiver   (arranca un receptor sin interfaz, útil en Linux para pruebas)
//                [--slow 70]        (simula un PC lento: ms extra por frame decodificado)
//                [--clear-after 12] (a los N s quita el límite de resolución pedido a los celulares)
//                [--vcam]           (Windows: publica en las cámaras virtuales "Cámara NDI - Celular N")
//                [--no-ndi]         (no publicar por NDI)
//                [--seconds 40]     (terminar solo después de N s; 0 = hasta Ctrl+C)
//      SimSender --probe-gpu  (Windows: prueba el procesador de video de Direct3D 11, el de la mejora con IA)
var a = args.ToList();
string Arg(string name, string def) { int i = a.IndexOf(name); return i >= 0 && i + 1 < a.Count ? a[i + 1] : def; }

if (a.Contains("--probe-gpu"))
{
    // Prueba del procesador de video de Direct3D 11 (el camino de la mejora con IA) en la GPU que haya.
    // En GitHub Actions es una GPU de software: no hay IA, pero se comprueba que subir/escalar/bajar NV12 funcione.
    Console.WriteLine("GPU: " + D3D11VsrScaler.Describe());
    try
    {
        using var vp = new D3D11VsrScaler(1280, 720, 1920, 1080, requireNvidia: false);
        var src = new byte[1280 * 720 * 3 / 2];
        Array.Fill(src, (byte)128, 0, 1280 * 720);          // gris medio
        Array.Fill(src, (byte)128, 1280 * 720, 1280 * 360); // croma neutro
        for (int y = 0; y < 720; y++) for (int x = 0; x < 640; x++) src[y * 1280 + x] = 40; // mitad izquierda oscura
        var dst = new byte[1920 * 1080 * 3 / 2];
        bool ok;
        unsafe { fixed (byte* s = src) fixed (byte* d = dst) ok = vp.Process(s, 1280, 720, d, 1920, 1080); }
        int left = dst[540 * 1920 + 300], right = dst[540 * 1920 + 1600], uv = dst[1920 * 1080 + 270 * 1920 + 960];
        bool good = ok && Math.Abs(left - 40) <= 3 && Math.Abs(right - 128) <= 3 && Math.Abs(uv - 128) <= 3;
        Console.WriteLine($"PROCESADOR DE VIDEO: procesado={ok} izquierda={left} derecha={right} croma={uv} -> {(good ? "OK" : "MAL")}");
        return;
    }
    catch (Exception ex)
    {
        Console.WriteLine("PROCESADOR DE VIDEO: no disponible en esta GPU (" + ex.GetType().Name + ": " + ex.Message + ")");
        return;
    }
}

if (a.Contains("--receiver"))
{
    var settings = new ReceiverSettings { NdiOutput = !a.Contains("--no-ndi") };
    var cams = a.Contains("--vcam") ? VirtualCamHub.CreateForThisPc() : null;
    if (a.Contains("--vcam") && cams == null) Console.WriteLine("Cámaras virtuales no disponibles (sólo en Windows)");
    using var server = new ReceiverServer(settings, cams);
    Console.WriteLine($"Receptor sin interfaz escuchando en {server.MediaPort}. Ctrl+C para salir.");
    int slow = int.Parse(Arg("--slow", "0"));
    int clearAfter = int.Parse(Arg("--clear-after", "0"));
    int seconds = int.Parse(Arg("--seconds", "0"));
    var clock = System.Diagnostics.Stopwatch.StartNew();
    bool cleared = false;
    while (seconds <= 0 || clock.Elapsed.TotalSeconds < seconds)
    {
        Thread.Sleep(1000);
        foreach (var s in server.Sessions)
        {
            s.TestDecodeDelayMs = cleared ? 0 : slow;
            if (clearAfter > 0 && !cleared && clock.Elapsed.TotalSeconds >= clearAfter)
            {
                cleared = true;
                s.TestDecodeDelayMs = 0;
                s.ClearLimit();
                Console.WriteLine("Se quitó el límite de resolución");
            }
            var x = s.Snapshot;
            Console.WriteLine($"{x.NdiName}: {x.State} {x.Width}x{x.Height} {x.OutputFps:F1} fps {x.RxMbps:F2} Mbps pérdida {x.LossPct:F1}% no recuperado {x.LostPct:F2}% lat {x.ReceiverLatencyMs:F1} ms dec {x.Decoder}" +
                              (cams != null ? $" cámara {x.Camera} en uso {x.CameraInUse} escritas {s.VirtualCam?.FramesWritten ?? 0}" : ""));
        }
    }
    Console.WriteLine("Receptor sin interfaz: fin");
    return;
}

var size = Arg("--size", "1280x720").Split('x');
var sim = new SimSender
{
    Host = Arg("--host", "127.0.0.1"),
    Port = int.Parse(Arg("--port", Protocol.DefaultMediaPort.ToString())),
    Width = int.Parse(size[0]),
    Height = int.Parse(size[1]),
    Fps = int.Parse(Arg("--fps", "30")),
    BitrateKbps = int.Parse(Arg("--kbps", "4000")),
    Codec = Arg("--codec", "h264") == "hevc" ? VideoCodec.Hevc : VideoCodec.H264,
    LossRate = double.Parse(Arg("--loss", "0"), System.Globalization.CultureInfo.InvariantCulture),
    Name = Arg("--name", "Simulador"),
};
var t = new Thread(() => sim.Run(TimeSpan.FromSeconds(int.Parse(Arg("--seconds", "30")))));
t.Start();
while (t.IsAlive)
{
    Thread.Sleep(1000);
    var r = sim.LastReport;
    Console.WriteLine($"frames {sim.FramesSent} paquetes {sim.PacketsSent} perdidos a propósito {sim.PacketsDroppedOnPurpose} reenviados {sim.Retransmitted} keyframes pedidos {sim.KeyframeRequests} | receptor: {r.FramesOutput} fr/0.5s, descartados {r.FramesDropped}, latencia {r.ReceiverLatencyMs:F1} ms, RTT {sim.LastRttMs:F1} ms");
}
