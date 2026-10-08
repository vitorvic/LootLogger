using System.Buffers.Binary;
using System.Text;

namespace LootLogger.Core.Tests;

/// <summary>Builds Photon packets with Protocol18 parameters, the way the game server sends them.</summary>
internal static class PhotonPackets
{
    private const byte EventMessage = 4;
    private const byte RequestMessage = 2;
    private const byte ResponseMessage = 3;

    public static byte[] Event(short albionCode, Dictionary<byte, object> parameters)
    {
        var body = new List<byte> { 1 };
        WriteParameters(body, With(parameters, 252, albionCode));
        return Packet(EventMessage, body);
    }

    /// <summary>An event whose parameter table is given byte for byte, to send malformed or hostile data.</summary>
    public static byte[] RawEvent(byte[] parameterTable)
    {
        var body = new List<byte> { 1 };
        body.AddRange(parameterTable);
        return Packet(EventMessage, body);
    }

    public static byte[] Request(short albionCode, Dictionary<byte, object> parameters)
    {
        var body = new List<byte> { 1 };
        WriteParameters(body, With(parameters, 253, albionCode));
        return Packet(RequestMessage, body);
    }

    public static byte[] Response(short albionCode, Dictionary<byte, object> parameters)
    {
        var body = new List<byte> { 1, 0, 0, 8 };
        WriteParameters(body, With(parameters, 253, albionCode));
        return Packet(ResponseMessage, body);
    }

    /// <summary>
    /// A copy of a packet from the builders above with another command number, channel or connection.
    /// The sender numbers each reliable command and sends it again, same number, until it is confirmed.
    /// </summary>
    public static byte[] Numbered(byte[] packet, int sequence, byte channel = 0, int challenge = 42)
    {
        var copy = packet.ToArray();
        BinaryPrimitives.WriteInt32BigEndian(copy.AsSpan(8), challenge);
        copy[13] = channel;
        BinaryPrimitives.WriteInt32BigEndian(copy.AsSpan(20), sequence);
        return copy;
    }

    /// <summary>An event too big for one command, split into fragments numbered from <paramref name="firstSequence"/>, one packet each.</summary>
    public static List<byte[]> FragmentedEvent(short albionCode, Dictionary<byte, object> parameters, int pieces, int firstSequence)
    {
        var body = new List<byte> { 1 };
        WriteParameters(body, With(parameters, 252, albionCode));
        var whole = new List<byte> { 0, EventMessage };
        whole.AddRange(body);

        var packets = new List<byte[]>();
        var size = (whole.Count + pieces - 1) / pieces;
        for (var i = 0; i < pieces; i++)
        {
            var piece = whole.Skip(i * size).Take(size).ToArray();
            var commandLength = 12 + 20 + piece.Length;
            var packet = new byte[12 + commandLength];
            BinaryPrimitives.WriteInt16BigEndian(packet.AsSpan(0), 0x1234);
            packet[3] = 1;
            BinaryPrimitives.WriteInt32BigEndian(packet.AsSpan(4), 1000);
            BinaryPrimitives.WriteInt32BigEndian(packet.AsSpan(8), 42);
            packet[12] = 8; // fragment
            BinaryPrimitives.WriteInt32BigEndian(packet.AsSpan(16), commandLength);
            BinaryPrimitives.WriteInt32BigEndian(packet.AsSpan(20), firstSequence + i);
            BinaryPrimitives.WriteInt32BigEndian(packet.AsSpan(24), firstSequence);
            BinaryPrimitives.WriteInt32BigEndian(packet.AsSpan(28), pieces);
            BinaryPrimitives.WriteInt32BigEndian(packet.AsSpan(32), i);
            BinaryPrimitives.WriteInt32BigEndian(packet.AsSpan(36), whole.Count);
            BinaryPrimitives.WriteInt32BigEndian(packet.AsSpan(40), i * size);
            piece.CopyTo(packet, 44);
            packets.Add(packet);
        }

        return packets;
    }

    private static Dictionary<byte, object> With(Dictionary<byte, object> parameters, byte key, short code)
    {
        var copy = new Dictionary<byte, object>(parameters) { [key] = (int) code };
        return copy;
    }

    private static byte[] Packet(byte messageType, List<byte> operation)
    {
        var commandPayload = new List<byte> { 0, messageType };
        commandPayload.AddRange(operation);

        var commandLength = 12 + commandPayload.Count;
        var packet = new byte[12 + commandLength];
        BinaryPrimitives.WriteInt16BigEndian(packet.AsSpan(0), 0x1234);
        packet[2] = 0;
        packet[3] = 1;
        BinaryPrimitives.WriteInt32BigEndian(packet.AsSpan(4), 1000);
        BinaryPrimitives.WriteInt32BigEndian(packet.AsSpan(8), 42);
        packet[12] = 6; // reliable
        packet[13] = 0;
        BinaryPrimitives.WriteInt32BigEndian(packet.AsSpan(16), commandLength);
        BinaryPrimitives.WriteInt32BigEndian(packet.AsSpan(20), 1);
        commandPayload.ToArray().CopyTo(packet, 24);
        return packet;
    }

    private static void WriteParameters(List<byte> target, Dictionary<byte, object> parameters)
    {
        target.Add((byte) parameters.Count);
        foreach (var (key, value) in parameters)
        {
            target.Add(key);
            WriteValue(target, value);
        }
    }

    private static void WriteValue(List<byte> target, object value)
    {
        switch (value)
        {
            case bool b:
                target.Add(b ? (byte) 28 : (byte) 27);
                break;
            case int i:
                target.Add(9);
                WriteCompressed(target, (uint) ((i << 1) ^ (i >> 31)));
                break;
            case long l:
                target.Add(10);
                WriteCompressed64(target, (ulong) ((l << 1) ^ (l >> 63)));
                break;
            case string s:
                target.Add(7);
                var bytes = Encoding.UTF8.GetBytes(s);
                WriteCompressed(target, (uint) bytes.Length);
                target.AddRange(bytes);
                break;
            case byte[] array:
                target.Add(67);
                WriteCompressed(target, (uint) array.Length);
                target.AddRange(array);
                break;
            case string[] strings:
                target.Add(71);
                WriteCompressed(target, (uint) strings.Length);
                foreach (var s in strings)
                {
                    var b = Encoding.UTF8.GetBytes(s);
                    WriteCompressed(target, (uint) b.Length);
                    target.AddRange(b);
                }

                break;
            case long[] longs:
                target.Add(74);
                WriteCompressed(target, (uint) longs.Length);
                foreach (var l in longs)
                {
                    WriteCompressed64(target, (ulong) ((l << 1) ^ (l >> 63)));
                }

                break;
            case object[] objects:
                target.Add(23);
                WriteCompressed(target, (uint) objects.Length);
                foreach (var o in objects)
                {
                    WriteValue(target, o);
                }

                break;
            default:
                throw new NotSupportedException(value.GetType().Name);
        }
    }

    private static void WriteCompressed(List<byte> target, uint value)
    {
        do
        {
            var current = (byte) (value & 0x7F);
            value >>= 7;
            if (value != 0)
            {
                current |= 0x80;
            }

            target.Add(current);
        }
        while (value != 0);
    }

    private static void WriteCompressed64(List<byte> target, ulong value)
    {
        do
        {
            var current = (byte) (value & 0x7F);
            value >>= 7;
            if (value != 0)
            {
                current |= 0x80;
            }

            target.Add(current);
        }
        while (value != 0);
    }
}
