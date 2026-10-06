using System.Buffers.Binary;
using LootLogger.Capture;
using LootLogger.Core.Data;
using LootLogger.Core.Network;
using PacketDotNet;

namespace LootLogger.Core.Tests;

public class CaptureTests
{
    [Fact]
    public void Extract_ReturnsPayloadForGamePort()
    {
        var payload = Enumerable.Range(0, 50).Select(i => (byte) i).ToArray();
        var datagram = Udp(5056, 61000, payload);
        var frames = Ipv4Fragments(datagram, maxFragmentData: 4000);

        var result = new UdpPayloadExtractor().Extract(LinkLayers.Raw, frames.Single(), DateTime.UtcNow);

        Assert.Equal(payload, result);
    }

    [Fact]
    public void Extract_IgnoresOtherPorts()
    {
        var frames = Ipv4Fragments(Udp(443, 61000, [1, 2, 3]), maxFragmentData: 4000);
        Assert.Null(new UdpPayloadExtractor().Extract(LinkLayers.Raw, frames.Single(), DateTime.UtcNow));
    }

    [Fact]
    public void Extract_RemembersTheServerSide()
    {
        var extractor = new UdpPayloadExtractor();
        extractor.Extract(LinkLayers.Raw, Ipv4Fragments(Udp(61000, 5056, [1, 2, 3]), maxFragmentData: 4000).Single(), DateTime.UtcNow);
        Assert.Equal(0x05000009u, extractor.LastServerAddress);

        extractor.Extract(LinkLayers.Raw, Ipv4Fragments(Udp(5056, 61000, [1, 2, 3]), maxFragmentData: 4000).Single(), DateTime.UtcNow);
        Assert.Equal(0x0A000002u, extractor.LastServerAddress);
    }

    [Theory]
    [InlineData(5, 188, 125, 30, ServerRegion.Americas)]
    [InlineData(193, 169, 238, 7, ServerRegion.Europe)]
    [InlineData(5, 45, 187, 200, ServerRegion.Asia)]
    [InlineData(8, 8, 8, 8, ServerRegion.Unknown)]
    public void GameServers_FindsRegionByAddress(byte a, byte b, byte c, byte d, ServerRegion expected)
    {
        Assert.Equal(expected, GameServers.RegionOf((uint)(a << 24 | b << 16 | c << 8 | d)));
    }

    [Fact]
    public void Clusters_KnowNameAndTier()
    {
        var clusters = ClusterDatabase.LoadBuiltIn();
        Assert.Equal("Razorrock Gulch", clusters.DisplayName("3356"));
        Assert.Equal(8, clusters.Tier("3356"));
        Assert.Equal(8, clusters.Tier("3356@some-instance"));
        Assert.Equal(0, clusters.Tier("nope"));
    }

    [Fact]
    public void RawSocketFilter_KeepsGamePortsAndFragmentsOnly()
    {
        Assert.True(RawSocketCapture.IsGamePacket(Ipv4Fragments(Udp(5056, 61000, [1, 2, 3]), maxFragmentData: 4000).Single()));
        Assert.True(RawSocketCapture.IsGamePacket(Ipv4Fragments(Udp(61000, 5055, [1, 2, 3]), maxFragmentData: 4000).Single()));
        Assert.False(RawSocketCapture.IsGamePacket(Ipv4Fragments(Udp(443, 61000, [1, 2, 3]), maxFragmentData: 4000).Single()));

        var fragments = Ipv4Fragments(Udp(5056, 61000, new byte[3000]), maxFragmentData: 1480);
        Assert.All(fragments, f => Assert.True(RawSocketCapture.IsGamePacket(f)));
    }

    [Fact]
    public void Extract_ReassemblesIpFragmentsInAnyOrder()
    {
        var payload = Enumerable.Range(0, 3000).Select(i => (byte) (i % 251)).ToArray();
        var frames = Ipv4Fragments(Udp(5056, 61000, payload), maxFragmentData: 1200);
        Assert.True(frames.Count > 2);

        var extractor = new UdpPayloadExtractor();
        byte[]? result = null;
        foreach (var frame in frames.AsEnumerable().Reverse())
        {
            result ??= extractor.Extract(LinkLayers.Raw, frame, DateTime.UtcNow);
        }

        Assert.Equal(payload, result);
    }

    private static byte[] Udp(ushort src, ushort dst, byte[] payload)
    {
        var udp = new byte[8 + payload.Length];
        BinaryPrimitives.WriteUInt16BigEndian(udp.AsSpan(0), src);
        BinaryPrimitives.WriteUInt16BigEndian(udp.AsSpan(2), dst);
        BinaryPrimitives.WriteUInt16BigEndian(udp.AsSpan(4), (ushort) udp.Length);
        payload.CopyTo(udp, 8);
        return udp;
    }

    // Raw IPv4 frames (no Ethernet header), split like a router would.
    private static List<byte[]> Ipv4Fragments(byte[] datagram, int maxFragmentData)
    {
        var frames = new List<byte[]>();
        for (var offset = 0; offset < datagram.Length; offset += maxFragmentData)
        {
            var length = Math.Min(maxFragmentData, datagram.Length - offset);
            var more = offset + length < datagram.Length;
            var frame = new byte[20 + length];
            frame[0] = 0x45;
            BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(2), (ushort) frame.Length);
            BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(4), 0xBEEF);
            BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(6), (ushort) ((more ? 0x2000 : 0) | (offset / 8)));
            frame[8] = 64;
            frame[9] = 17;
            frame[12] = 10; frame[15] = 2;
            frame[16] = 5; frame[19] = 9;
            datagram.AsSpan(offset, length).CopyTo(frame.AsSpan(20));
            frames.Add(frame);
        }

        return frames;
    }
}
