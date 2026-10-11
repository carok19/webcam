using System.Collections.Concurrent;
using System.Text;

namespace CamaraNdi.Core;

/// <summary>Registro simple a archivo (un archivo por día) + últimas líneas en memoria para la UI.</summary>
public static class Log
{
    private static readonly object Gate = new();
    private static readonly ConcurrentQueue<string> Recent = new();
    private static string? _dir;
    private static StreamWriter? _writer;
    private static DateOnly _writerDate;

    public static string? Directory => _dir;
    public static event Action<string>? LineAdded;

    public static void Init(string directory)
    {
        lock (Gate)
        {
            _dir = directory;
            System.IO.Directory.CreateDirectory(directory);
            // Borrar registros con más de 14 días.
            try
            {
                foreach (var f in System.IO.Directory.GetFiles(directory, "receptor-*.log"))
                    if (File.GetLastWriteTime(f) < DateTime.Now.AddDays(-14)) File.Delete(f);
            }
            catch { /* no es crítico */ }
        }
    }

    public static void Info(string msg) => Write("INFO", msg);
    public static void Warn(string msg) => Write("AVISO", msg);
    public static void Error(string msg, Exception? ex = null) => Write("ERROR", ex == null ? msg : $"{msg}: {ex}");

    public static IReadOnlyList<string> RecentLines() => Recent.ToArray();

    private static void Write(string level, string msg)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {msg}";
        Recent.Enqueue(line);
        while (Recent.Count > 300) Recent.TryDequeue(out _);
        lock (Gate)
        {
            try
            {
                if (_dir != null)
                {
                    var today = DateOnly.FromDateTime(DateTime.Now);
                    if (_writer == null || today != _writerDate)
                    {
                        _writer?.Dispose();
                        _writer = new StreamWriter(Path.Combine(_dir, $"receptor-{today:yyyyMMdd}.log"), append: true, Encoding.UTF8) { AutoFlush = true };
                        _writerDate = today;
                    }
                    _writer.WriteLine(line);
                }
                else
                {
                    Console.Error.WriteLine(line);
                }
            }
            catch { /* el log nunca debe tumbar la app */ }
        }
        try { LineAdded?.Invoke(line); } catch { }
    }
}
