namespace LootLogger.Capture;

/// <summary>
/// Writes packets to a classic .pcap file without Npcap, so recording works in the Windows sockets mode.
/// </summary>
public sealed class PcapFileWriter : IDisposable
{
    private readonly BinaryWriter _writer;

    /// <param name="linkType">pcap DLT number of the packets (1 Ethernet, 12 raw IP).</param>
    public PcapFileWriter(string path, uint linkType)
    {
        _writer = new BinaryWriter(new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read));
        _writer.Write(0xA1B2C3D4u); // magic, microsecond timestamps
        _writer.Write((ushort)2);
        _writer.Write((ushort)4);
        _writer.Write(0); // time zone
        _writer.Write(0u); // accuracy
        _writer.Write(65535u); // snap length
        _writer.Write(linkType);
    }

    public void Write(DateTime utcTime, byte[] data)
    {
        var micros = (utcTime - DateTime.UnixEpoch).Ticks / 10;
        _writer.Write((uint)(micros / 1_000_000));
        _writer.Write((uint)(micros % 1_000_000));
        _writer.Write((uint)data.Length);
        _writer.Write((uint)data.Length);
        _writer.Write(data);
    }

    public void Dispose() => _writer.Dispose();
}
