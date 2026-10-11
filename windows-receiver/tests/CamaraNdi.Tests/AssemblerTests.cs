using CamaraNdi.Core;
using Xunit;

namespace CamaraNdi.Tests;

public class AssemblerTests
{
    private static VideoPacketHeader H(uint seq, uint frame, int idx, int count, bool key = false, long pts = 0) =>
        new(seq, frame, (ushort)idx, (ushort)count, key ? Protocol.FlagKeyframe : (byte)0, VideoCodec.H264, pts);

    private static byte[] P(byte v, int n = 10) => Enumerable.Repeat(v, n).ToArray();

    [Fact]
    public void DeliversInOrderAndJoinsFragments()
    {
        var a = new FrameAssembler { JitterMs = 50 };
        a.OnPacket(H(0, 0, 0, 2, key: true), P(1), 0);
        Assert.Null(a.NextFrame(0));
        a.OnPacket(H(1, 0, 1, 2, key: true), P(2), 1);
        var f = a.NextFrame(1);
        Assert.NotNull(f);
        Assert.True(f!.Keyframe);
        Assert.Equal(20, f.Length);
        Assert.Equal(1, f.Data[0]);
        Assert.Equal(2, f.Data[19]);
        Assert.False(a.NeedKeyframe);
    }

    [Fact]
    public void ReordersPackets()
    {
        var a = new FrameAssembler { JitterMs = 50 };
        a.OnPacket(H(0, 0, 0, 1, key: true), P(1), 0);
        Assert.NotNull(a.NextFrame(0));
        // frame 2 llega antes que frame 1
        a.OnPacket(H(2, 2, 0, 1), P(3), 1);
        Assert.Null(a.NextFrame(1));
        Assert.Equal(new uint[] { 1 }, a.CollectNacks(1));
        a.OnPacket(H(1, 1, 0, 1), P(2), 3);
        Assert.Equal(1u, a.NextFrame(3)!.FrameId);
        Assert.Equal(2u, a.NextFrame(3)!.FrameId);
        Assert.Equal(0, a.LostPackets);
        Assert.Equal(1, a.RecoveredPackets);
    }

    [Fact]
    public void NacksAreRetriedThenGiveUp()
    {
        var a = new FrameAssembler { JitterMs = 60, NackRetryMs = 20 };
        a.OnPacket(H(0, 0, 0, 1, key: true), P(1), 0);
        a.NextFrame(0);
        a.OnPacket(H(2, 1, 1, 2), P(2), 0); // falta seq 1 (frag 0 del frame 1)
        Assert.Single(a.CollectNacks(0));
        Assert.Empty(a.CollectNacks(10));
        Assert.Single(a.CollectNacks(25));
        Assert.Single(a.CollectNacks(50));
        Assert.Empty(a.CollectNacks(75)); // ya se pidió 3 veces
        a.CollectNacks(200);
        Assert.Equal(1, a.LostPackets);
    }

    [Fact]
    public void DropsIncompleteFrameAtDeadlineAndWaitsForKeyframe()
    {
        var a = new FrameAssembler { JitterMs = 50 };
        a.OnPacket(H(0, 0, 0, 1, key: true), P(1), 0);
        Assert.NotNull(a.NextFrame(0));
        a.OnPacket(H(1, 1, 0, 2), P(2), 10); // frame 1 incompleto para siempre
        a.OnPacket(H(3, 2, 0, 1), P(3), 20); // frame 2 (P) completo
        Assert.Null(a.NextFrame(30));
        Assert.Null(a.NextFrame(61)); // límite del frame 1 vencido -> se descarta, el 2 es P -> también
        Assert.True(a.NeedKeyframe);
        Assert.Equal(2, a.FramesDropped);
        a.OnPacket(H(4, 3, 0, 1), P(4), 70);  // P: se descarta
        a.OnPacket(H(5, 4, 0, 1, key: true), P(5), 80); // keyframe: se entrega
        var f = a.NextFrame(80);
        Assert.NotNull(f);
        Assert.Equal(4u, f!.FrameId);
        Assert.False(a.NeedKeyframe);
    }

    [Fact]
    public void JumpsDirectlyToCompleteKeyframeWhenBroken()
    {
        var a = new FrameAssembler { JitterMs = 1000 };
        a.OnPacket(H(10, 5, 0, 2), P(1), 0);           // frame 5 incompleto (P)
        a.OnPacket(H(12, 6, 0, 1, key: true), P(2), 1); // keyframe completo
        var f = a.NextFrame(2);
        Assert.NotNull(f);
        Assert.Equal(6u, f!.FrameId);
    }

    [Fact]
    public void LateDuplicateIsIgnored()
    {
        var a = new FrameAssembler();
        a.OnPacket(H(0, 0, 0, 1, key: true), P(1), 0);
        a.NextFrame(0);
        a.OnPacket(H(0, 0, 0, 1, key: true), P(1), 5);
        Assert.Null(a.NextFrame(5));
    }

    [Fact]
    public void ProtocolRoundTrip()
    {
        var buf = new byte[1300];
        var h = new VideoPacketHeader(77, 5, 2, 9, Protocol.FlagKeyframe, VideoCodec.Hevc, 123456789);
        int n = Protocol.WriteVideo(buf, 0xABCDEF, h, P(7, 100));
        Assert.Equal(132, n);
        Assert.True(Protocol.TryReadHeader(buf, out var type, out var sid));
        Assert.Equal(Protocol.Video, type);
        Assert.Equal(0xABCDEFu, sid);
        Assert.True(Protocol.TryReadVideo(buf.AsSpan(0, n), out var r));
        Assert.Equal(h, r);

        var nack = Protocol.BuildNack(1, new uint[] { 3, 4, 99 });
        Assert.Equal(new uint[] { 3, 4, 99 }, Protocol.ReadNack(nack));

        var rep = Protocol.BuildReport(1, new ReportData { ExpectedPackets = 10, MissingPackets = 2, JitterMs = 3.4, SuggestedBitrateKbps = 5000 });
        var back = Protocol.ReadReport(rep);
        Assert.Equal(10u, back.ExpectedPackets);
        Assert.Equal(2u, back.MissingPackets);
        Assert.Equal(3.4, back.JitterMs, 1);
        Assert.Equal(5000u, back.SuggestedBitrateKbps);
    }
}
