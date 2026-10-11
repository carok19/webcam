using CamaraNdi.Core;
using CamaraNdi.Sim;

// Uso: SimSender [--host 192.168.1.10] [--port 47810] [--size 1920x1080] [--fps 30]
//                [--kbps 6000] [--codec h264|hevc] [--loss 0.02] [--seconds 60] [--name "Sim 1"]
//      SimSender --receiver   (arranca un receptor sin interfaz, útil en Linux para pruebas)
var a = args.ToList();
string Arg(string name, string def) { int i = a.IndexOf(name); return i >= 0 && i + 1 < a.Count ? a[i + 1] : def; }

if (a.Contains("--receiver"))
{
    var settings = new ReceiverSettings();
    using var server = new ReceiverServer(settings);
    Console.WriteLine($"Receptor sin interfaz escuchando en {server.MediaPort}. Ctrl+C para salir.");
    while (true)
    {
        Thread.Sleep(1000);
        foreach (var s in server.Sessions)
        {
            var x = s.Snapshot;
            Console.WriteLine($"{x.NdiName}: {x.State} {x.Width}x{x.Height} {x.OutputFps:F1} fps {x.RxMbps:F2} Mbps pérdida {x.LossPct:F1}% no recuperado {x.LostPct:F2}% lat {x.ReceiverLatencyMs:F1} ms dec {x.Decoder}");
        }
    }
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
