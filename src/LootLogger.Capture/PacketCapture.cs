using PacketDotNet;
using SharpPcap;
using SharpPcap.LibPcap;

namespace LootLogger.Capture;

public sealed record NetworkAdapter(string Id, string Name);

/// <summary>
/// Listens to the game's UDP traffic through Npcap and hands each payload to <see cref="PayloadReceived"/>.
/// Only reads; nothing is ever sent to the game or its servers.
/// </summary>
public sealed class PacketCapture : IDisposable
{
    // Game ports plus IPv4 fragments, which carry no UDP header after the first piece.
    public const string Filter = "(ip and ((udp and (port 5055 or port 5056 or port 5058)) or (ip[6:2] & 0x3fff != 0))) or (ip6 and udp and (port 5055 or port 5056 or port 5058))";

    private readonly List<LibPcapLiveDevice> _devices = [];
    private readonly UdpPayloadExtractor _extractor = new();
    private readonly Lock _lock = new();
    private CaptureFileWriterDevice? _recorder;

    public event Action<byte[]>? PayloadReceived;

    /// <summary>Raised the first time game traffic is seen after starting.</summary>
    public event Action? GameTrafficDetected;

    public bool IsRunning => _devices.Count > 0;

    private bool _trafficSeen;

    /// <summary>True when the Npcap driver is installed and usable.</summary>
    public static bool IsDriverAvailable(out string? error)
    {
        try
        {
            _ = LibPcapLiveDeviceList.Instance.Count;
            error = null;
            return true;
        }
        catch (Exception e) when (e is DllNotFoundException or PcapException or TypeInitializationException)
        {
            error = e.Message;
            return false;
        }
    }

    public static IReadOnlyList<NetworkAdapter> ListAdapters()
    {
        return LibPcapLiveDeviceList.Instance
            .Where(d => !d.Loopback)
            .Select(d => new NetworkAdapter(d.Name, string.IsNullOrWhiteSpace(d.Interface?.FriendlyName) ? d.Description ?? d.Name : d.Interface.FriendlyName))
            .ToList();
    }

    /// <param name="adapterId">Adapter to use, or null to listen on every adapter.</param>
    /// <param name="recordPath">Optional .pcap file that receives a copy of the game traffic, for troubleshooting.</param>
    public void Start(string? adapterId, string? recordPath = null)
    {
        Stop();
        _trafficSeen = false;

        var candidates = LibPcapLiveDeviceList.Instance
            .Where(d => !d.Loopback && (adapterId is null || d.Name == adapterId))
            .ToList();

        foreach (var device in candidates)
        {
            try
            {
                device.Open(new DeviceConfiguration { Mode = DeviceModes.None, ReadTimeout = 500 });
                device.Filter = Filter;
                device.OnPacketArrival += OnPacketArrival;
                device.StartCapture();
                _devices.Add(device);
            }
            catch (Exception)
            {
                // Some virtual adapters refuse to open; the others keep working.
                SafeClose(device);
            }
        }

        if (_devices.Count == 0)
        {
            throw new InvalidOperationException("Nenhuma placa de rede pôde ser aberta pelo Npcap.");
        }

        if (recordPath is not null)
        {
            _recorder = new CaptureFileWriterDevice(recordPath);
            _recorder.Open(new DeviceConfiguration { LinkLayerType = _devices[0].LinkType });
        }
    }

    public void Stop()
    {
        foreach (var device in _devices)
        {
            device.OnPacketArrival -= OnPacketArrival;
            SafeClose(device);
        }

        _devices.Clear();
        lock (_lock)
        {
            _recorder?.Close();
            _recorder = null;
        }
    }

    /// <summary>Feeds a recorded .pcap file through the same path as live traffic.</summary>
    public static void Replay(string pcapPath, Action<byte[]> onPayload)
    {
        using var reader = new CaptureFileReaderDevice(pcapPath);
        reader.Open();
        var extractor = new UdpPayloadExtractor();
        while (reader.GetNextPacket(out var capture) == GetPacketStatus.PacketRead)
        {
            var raw = capture.GetPacket();
            if (extractor.Extract(raw.LinkLayerType, raw.Data, raw.Timeval.Date) is { } payload)
            {
                onPayload(payload);
            }
        }
    }

    public void Dispose() => Stop();

    private void OnPacketArrival(object sender, SharpPcap.PacketCapture e)
    {
        var raw = e.GetPacket();
        byte[]? payload;
        lock (_lock)
        {
            // Several adapters may deliver at once; the extractor keeps fragment state, so serialize.
            payload = _extractor.Extract(raw.LinkLayerType, raw.Data, DateTime.UtcNow);
            if (payload is not null && _recorder is not null && raw.LinkLayerType == _devices.FirstOrDefault()?.LinkType)
            {
                _recorder.Write(raw);
            }
        }

        if (payload is null || payload.Length == 0)
        {
            return;
        }

        if (!_trafficSeen)
        {
            _trafficSeen = true;
            GameTrafficDetected?.Invoke();
        }

        PayloadReceived?.Invoke(payload);
    }

    private static void SafeClose(ICaptureDevice device)
    {
        try
        {
            if (device.Started)
            {
                device.StopCapture();
            }

            device.Close();
        }
        catch (Exception)
        {
        }
    }
}
