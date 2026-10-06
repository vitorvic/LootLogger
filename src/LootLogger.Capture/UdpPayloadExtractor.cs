using System.Buffers.Binary;
using PacketDotNet;

namespace LootLogger.Capture;

/// <summary>
/// Pulls Albion UDP payloads out of captured frames. Large game messages can be split by IP,
/// so IPv4 fragments are put back together before the UDP header is read.
/// </summary>
public sealed class UdpPayloadExtractor
{
    public static readonly IReadOnlySet<ushort> GamePorts = new HashSet<ushort> { 5055, 5056, 5058 };

    private static readonly TimeSpan FragmentTimeout = TimeSpan.FromSeconds(10);
    private const int MaxPendingFragments = 512;

    private readonly Dictionary<(uint Src, uint Dst, ushort Id), FragmentBuffer> _fragments = new();

    /// <summary>IPv4 address of the game server in the last game packet, as a big-endian number; 0 if unknown.</summary>
    public uint LastServerAddress { get; private set; }

    /// <summary>Returns the UDP payload when the frame (or the fragment it completes) is game traffic.</summary>
    public byte[]? Extract(LinkLayers linkLayer, byte[] frame, DateTime now)
    {
        Packet packet;
        try
        {
            packet = Packet.ParsePacket(linkLayer, frame);
        }
        catch (Exception)
        {
            return null;
        }

        if (packet.Extract<IPv4Packet>() is { } ipv4)
        {
            return ExtractIpv4(ipv4, now);
        }

        if (packet.Extract<UdpPacket>() is { } udp && IsGame(udp.SourcePort, udp.DestinationPort))
        {
            return udp.PayloadData;
        }

        return null;
    }

    private byte[]? ExtractIpv4(IPv4Packet ip, DateTime now)
    {
        // Read the header by hand: it is stable and avoids depending on how the library splits fragments.
        var raw = ip.Bytes;
        if (raw.Length < 20 || raw[9] != 17)
        {
            return null;
        }

        var headerLength = (raw[0] & 0x0F) * 4;
        var totalLength = Math.Min(BinaryPrimitives.ReadUInt16BigEndian(raw.AsSpan(2, 2)), raw.Length);
        if (headerLength < 20 || totalLength <= headerLength)
        {
            return null;
        }

        var id = BinaryPrimitives.ReadUInt16BigEndian(raw.AsSpan(4, 2));
        var flagsAndOffset = BinaryPrimitives.ReadUInt16BigEndian(raw.AsSpan(6, 2));
        var moreFragments = (flagsAndOffset & 0x2000) != 0;
        var offset = (flagsAndOffset & 0x1FFF) * 8;
        var body = raw[headerLength..totalLength];
        var source = BinaryPrimitives.ReadUInt32BigEndian(raw.AsSpan(12, 4));
        var destination = BinaryPrimitives.ReadUInt32BigEndian(raw.AsSpan(16, 4));

        if (!moreFragments && offset == 0)
        {
            return ReadUdp(body, source, destination);
        }

        CleanupFragments(now);
        var key = (source, destination, id);
        if (!_fragments.TryGetValue(key, out var buffer))
        {
            if (_fragments.Count >= MaxPendingFragments)
            {
                return null;
            }

            _fragments[key] = buffer = new FragmentBuffer(now);
        }

        buffer.Add(offset, body, isLast: !moreFragments);
        if (!buffer.IsComplete)
        {
            return null;
        }

        _fragments.Remove(key);
        return ReadUdp(buffer.Assemble(), source, destination);
    }

    private byte[]? ReadUdp(byte[] udp, uint sourceAddress, uint destinationAddress)
    {
        if (udp.Length < 8)
        {
            return null;
        }

        var src = BinaryPrimitives.ReadUInt16BigEndian(udp.AsSpan(0, 2));
        var dst = BinaryPrimitives.ReadUInt16BigEndian(udp.AsSpan(2, 2));
        if (!IsGame(src, dst))
        {
            return null;
        }

        // The server is the side using the game port.
        LastServerAddress = GamePorts.Contains(src) ? sourceAddress : destinationAddress;
        return udp[8..];
    }

    private static bool IsGame(ushort src, ushort dst) => GamePorts.Contains(src) || GamePorts.Contains(dst);

    private void CleanupFragments(DateTime now)
    {
        foreach (var key in _fragments.Where(f => now - f.Value.Created > FragmentTimeout).Select(f => f.Key).ToList())
        {
            _fragments.Remove(key);
        }
    }

    private sealed class FragmentBuffer(DateTime created)
    {
        private readonly SortedDictionary<int, byte[]> _parts = new();
        private int? _totalLength;

        public DateTime Created { get; } = created;

        public bool IsComplete
        {
            get
            {
                if (_totalLength is null)
                {
                    return false;
                }

                var expected = 0;
                foreach (var (offset, data) in _parts)
                {
                    if (offset > expected)
                    {
                        return false;
                    }

                    expected = Math.Max(expected, offset + data.Length);
                }

                return expected >= _totalLength;
            }
        }

        public void Add(int offset, byte[] data, bool isLast)
        {
            _parts[offset] = data;
            if (isLast)
            {
                _totalLength = offset + data.Length;
            }
        }

        public byte[] Assemble()
        {
            var result = new byte[_totalLength!.Value];
            foreach (var (offset, data) in _parts)
            {
                var length = Math.Min(data.Length, result.Length - offset);
                if (length > 0)
                {
                    data.AsSpan(0, length).CopyTo(result.AsSpan(offset));
                }
            }

            return result;
        }
    }
}
