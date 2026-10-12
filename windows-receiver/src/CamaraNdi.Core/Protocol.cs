using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CamaraNdi.Core;

/// <summary>Protocolo CNDI v1. Ver docs/PROTOCOLO.md. Todo little-endian.</summary>
public static class Protocol
{
    public const ushort Magic = 0x4E43; // bytes 'C','N'
    public const byte Version = 1;
    public const int HeaderSize = 8;
    public const int VideoHeaderSize = 32;
    public const int MaxPayload = 1168;
    public const int DefaultMediaPort = 47810;
    public const int DefaultDiscoveryPort = 47811;

    // Celular -> receptor
    public const byte Hello = 0x01;
    public const byte Ping = 0x02;
    public const byte Bye = 0x03;
    public const byte Discover = 0x04;
    public const byte Video = 0x10;

    // Receptor -> celular
    public const byte HelloAck = 0x81;
    public const byte Pong = 0x82;
    public const byte Nack = 0x83;
    public const byte KeyframeRequest = 0x84;
    public const byte Report = 0x85;
    public const byte Disconnect = 0x86;
    public const byte DiscoverReply = 0x87;
    /// <summary>El PC no alcanza a decodificar: pide al celular una altura máxima (JSON LimitMessage).</summary>
    public const byte Limit = 0x88;

    public const byte FlagKeyframe = 0x01;
    public const byte FlagRetransmit = 0x02;

    public const int MaxNackPerPacket = 128;

    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    public static bool TryReadHeader(ReadOnlySpan<byte> p, out byte type, out uint sessionId)
    {
        type = 0; sessionId = 0;
        if (p.Length < HeaderSize) return false;
        if (BinaryPrimitives.ReadUInt16LittleEndian(p) != Magic) return false;
        if (p[2] != Version) return false;
        type = p[3];
        sessionId = BinaryPrimitives.ReadUInt32LittleEndian(p[4..]);
        return true;
    }

    public static void WriteHeader(Span<byte> p, byte type, uint sessionId)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(p, Magic);
        p[2] = Version;
        p[3] = type;
        BinaryPrimitives.WriteUInt32LittleEndian(p[4..], sessionId);
    }

    public static byte[] BuildJson<T>(byte type, uint sessionId, T payload)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(payload, Json);
        var buf = new byte[HeaderSize + json.Length];
        WriteHeader(buf, type, sessionId);
        json.CopyTo(buf.AsSpan(HeaderSize));
        return buf;
    }

    public static T? ParseJson<T>(ReadOnlySpan<byte> packet) where T : class
    {
        try { return JsonSerializer.Deserialize<T>(packet[HeaderSize..], Json); }
        catch (JsonException) { return null; }
    }

    public static bool TryReadVideo(ReadOnlySpan<byte> p, out VideoPacketHeader h)
    {
        h = default;
        if (p.Length < VideoHeaderSize) return false;
        h = new VideoPacketHeader(
            Seq: BinaryPrimitives.ReadUInt32LittleEndian(p[8..]),
            FrameId: BinaryPrimitives.ReadUInt32LittleEndian(p[12..]),
            FragIdx: BinaryPrimitives.ReadUInt16LittleEndian(p[16..]),
            FragCount: BinaryPrimitives.ReadUInt16LittleEndian(p[18..]),
            Flags: p[20],
            Codec: (VideoCodec)p[21],
            PtsUs: BinaryPrimitives.ReadInt64LittleEndian(p[24..]));
        return h.FragCount > 0 && h.FragIdx < h.FragCount;
    }

    public static int WriteVideo(Span<byte> p, uint sessionId, in VideoPacketHeader h, ReadOnlySpan<byte> payload)
    {
        WriteHeader(p, Video, sessionId);
        BinaryPrimitives.WriteUInt32LittleEndian(p[8..], h.Seq);
        BinaryPrimitives.WriteUInt32LittleEndian(p[12..], h.FrameId);
        BinaryPrimitives.WriteUInt16LittleEndian(p[16..], h.FragIdx);
        BinaryPrimitives.WriteUInt16LittleEndian(p[18..], h.FragCount);
        p[20] = h.Flags;
        p[21] = (byte)h.Codec;
        BinaryPrimitives.WriteUInt16LittleEndian(p[22..], 0);
        BinaryPrimitives.WriteInt64LittleEndian(p[24..], h.PtsUs);
        payload.CopyTo(p[VideoHeaderSize..]);
        return VideoHeaderSize + payload.Length;
    }

    public static byte[] BuildPong(uint sessionId, ReadOnlySpan<byte> ping)
    {
        var buf = new byte[HeaderSize + 12];
        WriteHeader(buf, Pong, sessionId);
        if (ping.Length >= HeaderSize + 12) ping.Slice(HeaderSize, 12).CopyTo(buf.AsSpan(HeaderSize));
        return buf;
    }

    public static byte[] BuildPing(uint sessionId, uint pingId, long phoneTimeUs)
    {
        var buf = new byte[HeaderSize + 12];
        WriteHeader(buf, Ping, sessionId);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(8), pingId);
        BinaryPrimitives.WriteInt64LittleEndian(buf.AsSpan(12), phoneTimeUs);
        return buf;
    }

    public static byte[] BuildNack(uint sessionId, ReadOnlySpan<uint> seqs)
    {
        int n = Math.Min(seqs.Length, MaxNackPerPacket);
        var buf = new byte[HeaderSize + 2 + 4 * n];
        WriteHeader(buf, Nack, sessionId);
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(8), (ushort)n);
        for (int i = 0; i < n; i++) BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(10 + 4 * i), seqs[i]);
        return buf;
    }

    public static uint[] ReadNack(ReadOnlySpan<byte> p)
    {
        if (p.Length < HeaderSize + 2) return Array.Empty<uint>();
        int n = BinaryPrimitives.ReadUInt16LittleEndian(p[8..]);
        n = Math.Min(n, (p.Length - 10) / 4);
        var r = new uint[n];
        for (int i = 0; i < n; i++) r[i] = BinaryPrimitives.ReadUInt32LittleEndian(p[(10 + 4 * i)..]);
        return r;
    }

    public static byte[] BuildKeyframeRequest(uint sessionId, uint reason)
    {
        var buf = new byte[HeaderSize + 4];
        WriteHeader(buf, KeyframeRequest, sessionId);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(8), reason);
        return buf;
    }

    public static byte[] BuildReport(uint sessionId, in ReportData r)
    {
        var buf = new byte[HeaderSize + 36];
        var s = buf.AsSpan();
        WriteHeader(s, Report, sessionId);
        BinaryPrimitives.WriteUInt32LittleEndian(s[8..], r.IntervalMs);
        BinaryPrimitives.WriteUInt32LittleEndian(s[12..], r.ExpectedPackets);
        BinaryPrimitives.WriteUInt32LittleEndian(s[16..], r.MissingPackets);
        BinaryPrimitives.WriteUInt32LittleEndian(s[20..], r.LostPackets);
        BinaryPrimitives.WriteUInt32LittleEndian(s[24..], r.FramesOutput);
        BinaryPrimitives.WriteUInt32LittleEndian(s[28..], r.FramesDropped);
        BinaryPrimitives.WriteUInt16LittleEndian(s[32..], (ushort)Math.Clamp(r.JitterMs * 10, 0, 65535));
        BinaryPrimitives.WriteUInt16LittleEndian(s[34..], (ushort)Math.Clamp(r.ReceiverLatencyMs * 10, 0, 65535));
        BinaryPrimitives.WriteUInt32LittleEndian(s[36..], r.SuggestedBitrateKbps);
        BinaryPrimitives.WriteUInt32LittleEndian(s[40..], r.RxBitrateKbps);
        return buf;
    }

    public static ReportData ReadReport(ReadOnlySpan<byte> s) => new()
    {
        IntervalMs = BinaryPrimitives.ReadUInt32LittleEndian(s[8..]),
        ExpectedPackets = BinaryPrimitives.ReadUInt32LittleEndian(s[12..]),
        MissingPackets = BinaryPrimitives.ReadUInt32LittleEndian(s[16..]),
        LostPackets = BinaryPrimitives.ReadUInt32LittleEndian(s[20..]),
        FramesOutput = BinaryPrimitives.ReadUInt32LittleEndian(s[24..]),
        FramesDropped = BinaryPrimitives.ReadUInt32LittleEndian(s[28..]),
        JitterMs = BinaryPrimitives.ReadUInt16LittleEndian(s[32..]) / 10.0,
        ReceiverLatencyMs = BinaryPrimitives.ReadUInt16LittleEndian(s[34..]) / 10.0,
        SuggestedBitrateKbps = BinaryPrimitives.ReadUInt32LittleEndian(s[36..]),
        RxBitrateKbps = BinaryPrimitives.ReadUInt32LittleEndian(s[40..]),
    };

    public static string Utf8(ReadOnlySpan<byte> b) => Encoding.UTF8.GetString(b);
}

public enum VideoCodec : byte { Unknown = 0, H264 = 1, Hevc = 2 }

public readonly record struct VideoPacketHeader(
    uint Seq, uint FrameId, ushort FragIdx, ushort FragCount, byte Flags, VideoCodec Codec, long PtsUs)
{
    public bool IsKeyframe => (Flags & Protocol.FlagKeyframe) != 0;
}

public struct ReportData
{
    public uint IntervalMs;
    public uint ExpectedPackets;
    public uint MissingPackets;
    public uint LostPackets;
    public uint FramesOutput;
    public uint FramesDropped;
    public double JitterMs;
    public double ReceiverLatencyMs;
    public uint SuggestedBitrateKbps;
    public uint RxBitrateKbps;
}

/// <summary>JSON de HELLO (celular -> receptor).</summary>
public sealed class HelloMessage
{
    public string DeviceId { get; set; } = "";
    public string Name { get; set; } = "";
    public string Model { get; set; } = "";
    public string Codec { get; set; } = "";
    public int Width { get; set; }
    public int Height { get; set; }
    public int Fps { get; set; }
    public int BitrateKbps { get; set; }
    public int MaxBitrateKbps { get; set; }
    public string LatencyMode { get; set; } = "minima";
    public bool Streaming { get; set; } = true;
    public int Thermal { get; set; }
    public string ThermalLevel { get; set; } = "ok";
    public int BatteryPct { get; set; } = -1;
    public double BatteryTempC { get; set; }
    public bool Charging { get; set; }
    public double EncodeMs { get; set; }
    public double CpuPct { get; set; }
    public double RttMs { get; set; }
    public string AppVersion { get; set; } = "";
    public string? Lens { get; set; }
    public double Zoom { get; set; }
}

public sealed class HelloAckMessage
{
    public string Receiver { get; set; } = "";
    public string NdiName { get; set; } = "";
    public int JitterMs { get; set; }
    public bool Accepted { get; set; } = true;
    public string Reason { get; set; } = "";
    /// <summary>Qué puede decodificar este PC (para que el celular elija códec/resolución).</summary>
    public DecodeCapsMessage? Decode { get; set; }
}

public sealed class DecodeCapsMessage
{
    /// <summary>false mientras el receptor todavía está probando la GPU.</summary>
    public bool Probed { get; set; }
    /// <summary>Altura máxima que la GPU decodifica en H.265 (0 = no puede).</summary>
    public int HevcGpuMaxHeight { get; set; }
    public int H264GpuMaxHeight { get; set; }
    /// <summary>Límite pedido porque el PC no alcanzó (0 = sin límite).</summary>
    public int MaxHeight { get; set; }
    /// <summary>Píxeles por segundo máximos (ancho × alto × fps) que este PC alcanzó a decodificar (0 = sin límite).</summary>
    public long MaxPixelRate { get; set; }
    public string Gpu { get; set; } = "";
}

/// <summary>
/// El PC no alcanza a decodificar lo que llega: el celular debe elegir un modo con
/// altura ≤ MaxHeight y ancho × alto × fps ≤ MaxPixelRate. Los dos en 0 = se quita el límite.
/// </summary>
public sealed class LimitMessage
{
    public int MaxHeight { get; set; }
    public long MaxPixelRate { get; set; }
    public string Reason { get; set; } = "";
}

public sealed class DiscoverMessage
{
    public string DeviceId { get; set; } = "";
    public string Name { get; set; } = "";
}

public sealed class DiscoverReplyMessage
{
    public string Receiver { get; set; } = "";
    public int Port { get; set; }
    public int Version { get; set; } = Protocol.Version;
}

public sealed class DisconnectMessage
{
    public string Reason { get; set; } = "";
}
