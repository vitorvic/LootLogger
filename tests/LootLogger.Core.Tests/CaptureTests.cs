using System.Buffers.Binary;
using System.Net;
using LootLogger.Capture;
using LootLogger.Core.Data;
using LootLogger.Core.Network;
using PacketDotNet;

namespace LootLogger.Core.Tests;

public class CaptureTests
{
    [Fact]
    public void PcapFileWriter_WritesStandardPcap()
    {
        var path = Path.GetTempFileName();
        var time = new DateTime(2026, 10, 7, 12, 0, 1, DateTimeKind.Utc).AddTicks(2_500);
        using (var writer = new PcapFileWriter(path, 12))
        {
            writer.Write(time, [1, 2, 3]);
        }

        var bytes = File.ReadAllBytes(path);
        File.Delete(path);
        Assert.Equal(24 + 16 + 3, bytes.Length);
        Assert.Equal(0xA1B2C3D4u, BinaryPrimitives.ReadUInt32LittleEndian(bytes));
        Assert.Equal(12u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(20)));
        Assert.Equal((uint)(time - DateTime.UnixEpoch).TotalSeconds, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(24)));
        Assert.Equal(250u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(28)));
        Assert.Equal(3u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(32)));
        Assert.Equal(new byte[] { 1, 2, 3 }, bytes[^3..]);
    }

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
        Assert.False(extractor.LastFromServer);

        extractor.Extract(LinkLayers.Raw, Ipv4Fragments(Udp(5056, 61000, [1, 2, 3]), maxFragmentData: 4000).Single(), DateTime.UtcNow);
        Assert.Equal(0x0A000002u, extractor.LastServerAddress);
        Assert.True(extractor.LastFromServer);
    }

    [Theory]
    [InlineData(5, 188, 125, 30, ServerRegion.Americas)]
    [InlineData(85, 234, 70, 76, ServerRegion.Americas)]
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
    public void RawSockets_OpenAdaptersThatAppearAfterStarting()
    {
        // Wi-Fi was there when the app started; then ExitLag (or a VPN) brought up its own adapter.
        var wifi = IPAddress.Parse("192.168.0.10");
        var exitLag = IPAddress.Parse("10.8.0.2");
        var listening = new Dictionary<IPAddress, bool> { [wifi] = true };

        var (close, open) = RawSocketCapture.PlanChanges(listening, [wifi, exitLag]);

        Assert.Empty(close);
        Assert.Equal([exitLag], open);
    }

    [Fact]
    public void RawSockets_FollowAChangeOfNetwork()
    {
        // From Wi-Fi to cable: the old address is gone and a new one came up.
        var wifi = IPAddress.Parse("192.168.0.10");
        var cable = IPAddress.Parse("192.168.1.20");
        var listening = new Dictionary<IPAddress, bool> { [wifi] = true };

        var (close, open) = RawSocketCapture.PlanChanges(listening, [cable]);

        Assert.Equal([wifi], close);
        Assert.Equal([cable], open);
    }

    [Fact]
    public void RawSockets_ReopenASocketThatStopped()
    {
        // The adapter blinked and its socket stopped, but the address is still there.
        var wifi = IPAddress.Parse("192.168.0.10");
        var cable = IPAddress.Parse("192.168.1.20");
        var listening = new Dictionary<IPAddress, bool> { [wifi] = false, [cable] = true };

        var (close, open) = RawSocketCapture.PlanChanges(listening, [wifi, cable, wifi]);

        Assert.Equal([wifi], close);
        Assert.Equal([wifi], open);
    }

    [Fact]
    public void RawSockets_LeaveEverythingAloneWhenNothingChanged()
    {
        var wifi = IPAddress.Parse("192.168.0.10");
        var exitLag = IPAddress.Parse("10.8.0.2");
        var listening = new Dictionary<IPAddress, bool> { [wifi] = true, [exitLag] = true };

        var (close, open) = RawSocketCapture.PlanChanges(listening, [exitLag, wifi]);

        Assert.Empty(close);
        Assert.Empty(open);
    }

    [Fact]
    public void RawSockets_KeepListeningThroughTheNetworkCheck()
    {
        // Real raw sockets need Windows and administrator rights, like the app; the GitHub runner has both.
        if (!RawSocketCapture.IsAdministrator())
        {
            return;
        }

        var capture = new RawSocketCapture();
        capture.Start(_ => { });
        var addresses = capture.Addresses;
        Assert.NotEmpty(addresses);

        // One round of the network check: everything that was being read still is.
        Thread.Sleep(TimeSpan.FromSeconds(6));
        Assert.True(capture.IsRunning);
        Assert.Superset(addresses.ToHashSet(), capture.Addresses.ToHashSet());

        capture.Stop();
        Assert.False(capture.IsRunning);
        Assert.Empty(capture.Addresses);
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

    [Fact]
    public void Extract_DropsBrokenFramesWithoutThrowing()
    {
        // Capture reads anything that reaches the game ports, so the headers can say anything.
        var random = new Random(7);
        var extractor = new UdpPayloadExtractor();
        var valid = Ipv4Fragments(Udp(5056, 61000, new byte[40]), maxFragmentData: 4000).Single();
        for (var i = 0; i < 20_000; i++)
        {
            var frame = valid.ToArray();
            for (var changes = random.Next(1, 4); changes > 0; changes--)
            {
                frame[random.Next(0, 28)] = (byte) random.Next(256);
            }

            frame = frame[..random.Next(1, frame.Length + 1)];
            Assert.Null(Record.Exception(() => extractor.Extract(LinkLayers.Raw, frame, DateTime.UtcNow)));
        }
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
