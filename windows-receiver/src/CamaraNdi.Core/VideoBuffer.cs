namespace CamaraNdi.Core;

/// <summary>Imagen decodificada (NV12 o I420) en memoria fija (pinned) para entregar a NDI.</summary>
public sealed class VideoBuffer
{
    public VideoBuffer(int capacity)
    {
        Data = GC.AllocateUninitializedArray<byte>(capacity, pinned: true);
    }

    public byte[] Data { get; }
    public int Width;
    public int Height;
    /// <summary>Bytes por fila del plano Y.</summary>
    public int Stride;
    /// <summary>NdiLib.FourCC_NV12 o FourCC_I420.</summary>
    public uint FourCC;
    public int Length;
    public uint FrameId;
    public long PtsUs;
    public long FirstArrivalMs;
    public long DecodedMs;
    /// <summary>Rango de color que dice el video: 0 sin especificar, 1 limitado (MPEG), 2 completo (JPEG).</summary>
    public int SignaledRange;
    /// <summary>Matriz que dice el video (AVColorSpace de FFmpeg): 1 BT.709, 5/6 BT.601, 2 sin especificar.</summary>
    public int SignaledMatrix;

    public static int SizeFor(int width, int height) => width * height * 3 / 2;
}

/// <summary>Pool pequeño de buffers para no generar basura a 60 fps.</summary>
public sealed class VideoBufferPool
{
    private readonly Stack<VideoBuffer> _free = new();
    private readonly object _gate = new();
    private int _size;

    public VideoBuffer Rent(int size)
    {
        lock (_gate)
        {
            if (size != _size) { _free.Clear(); _size = size; }
            return _free.Count > 0 ? _free.Pop() : new VideoBuffer(size);
        }
    }

    public void Return(VideoBuffer? b)
    {
        if (b == null) return;
        lock (_gate)
        {
            if (b.Data.Length == _size && _free.Count < 16) _free.Push(b);
        }
    }
}
