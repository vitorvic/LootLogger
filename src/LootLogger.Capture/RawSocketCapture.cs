using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Principal;

namespace LootLogger.Capture;

/// <summary>
/// Reads IP packets straight from Windows with raw sockets, one per local address.
/// Unlike Npcap this also sees traffic that tools such as ExitLag route through their own drivers,
/// but Windows only allows it to administrators.
/// The addresses are checked again when Windows reports a change, and every few seconds anyway, so ExitLag
/// or a VPN turned on after the app, or a change of network, is picked up without restarting the capture.
/// </summary>
/// <remarks>Approach based on SocketsPacketProvider from AlbionOnline-StatisticsAnalysis by Triky313 (GPL-3.0).</remarks>
public sealed class RawSocketCapture
{
    private static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(5);

    private readonly Dictionary<IPAddress, Listener> _listeners = new();
    private readonly Lock _lock = new();
    private Action<byte[]>? _onPacket;
    private Timer? _checkTimer;
    private bool _checkSoon;

    public static bool IsAdministrator()
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    public bool IsRunning
    {
        get
        {
            lock (_lock)
            {
                return _onPacket is not null;
            }
        }
    }

    /// <summary>The local addresses being read right now.</summary>
    public IReadOnlyList<IPAddress> Addresses
    {
        get
        {
            lock (_lock)
            {
                return [.. _listeners.Keys];
            }
        }
    }

    /// <summary>Opens one socket per local IPv4 address and calls <paramref name="onPacket"/> with each IP packet.</summary>
    public void Start(Action<byte[]> onPacket)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException();
        }

        Stop();
        try
        {
            lock (_lock)
            {
                _onPacket = onPacket;
                SyncListeners();
                if (_listeners.Count == 0)
                {
                    throw new InvalidOperationException("Nenhuma placa de rede pôde ser aberta. Abra o LootLogger como administrador.");
                }

                _checkTimer = new Timer(_ => CheckNetwork(), null, CheckInterval, Timeout.InfiniteTimeSpan);
            }
        }
        catch (Exception)
        {
            // Nothing half-open is left behind.
            Stop();
            throw;
        }

        try
        {
            NetworkChange.NetworkAddressChanged += OnAddressChanged;
        }
        catch (Exception)
        {
            // Without the notice, the regular check still finds new adapters.
        }
    }

    public void Stop()
    {
        try
        {
            NetworkChange.NetworkAddressChanged -= OnAddressChanged;
        }
        catch (Exception)
        {
        }

        List<Listener> closing;
        lock (_lock)
        {
            _checkTimer?.Dispose();
            _checkTimer = null;
            _checkSoon = false;
            _onPacket = null;
            closing = [.. _listeners.Values];
            _listeners.Clear();
        }

        foreach (var listener in closing)
        {
            listener.Close();
        }

        foreach (var listener in closing)
        {
            listener.Join(TimeSpan.FromSeconds(2));
        }
    }

    /// <summary>
    /// Compares what is being read with the addresses the PC has now. A listener whose address is gone,
    /// or whose socket stopped, is closed; new addresses, and stopped ones that are still there, are opened.
    /// </summary>
    /// <param name="listening">Each address being read, and whether its socket is still receiving.</param>
    /// <param name="current">The local IPv4 addresses right now.</param>
    public static (IReadOnlyList<IPAddress> Close, IReadOnlyList<IPAddress> Open) PlanChanges(
        IReadOnlyDictionary<IPAddress, bool> listening, IEnumerable<IPAddress> current)
    {
        var now = current.ToHashSet();
        var close = listening.Where(l => !l.Value || !now.Contains(l.Key)).Select(l => l.Key).ToList();
        var open = now.Where(a => !listening.TryGetValue(a, out var receiving) || !receiving).ToList();
        return (close, open);
    }

    /// <summary>
    /// Raw sockets see all traffic, so this does what the Npcap filter does: keep UDP on the game ports
    /// and IPv4 fragments (only the first one carries the ports).
    /// </summary>
    public static bool IsGamePacket(ReadOnlySpan<byte> ip)
    {
        if (ip.Length < 20 || ip[0] >> 4 != 4 || ip[9] != 17)
        {
            return false;
        }

        var fragmented = ((ip[6] & 0x3F) | ip[7]) != 0;
        if (fragmented)
        {
            return true;
        }

        var headerLength = (ip[0] & 0x0F) * 4;
        if (ip.Length < headerLength + 8)
        {
            return false;
        }

        var source = (ushort)(ip[headerLength] << 8 | ip[headerLength + 1]);
        var destination = (ushort)(ip[headerLength + 2] << 8 | ip[headerLength + 3]);
        return UdpPayloadExtractor.GamePorts.Contains(source) || UdpPayloadExtractor.GamePorts.Contains(destination);
    }

    // A new adapter often gets its address in steps, so look again in a second instead of waiting for the next round.
    private void OnAddressChanged(object? sender, EventArgs e)
    {
        lock (_lock)
        {
            if (_checkTimer is not null && !_checkSoon)
            {
                _checkSoon = true;
                _checkTimer.Change(TimeSpan.FromSeconds(1), Timeout.InfiniteTimeSpan);
            }
        }
    }

    // The next check is scheduled only when this one is done, so a slow one never piles up behind itself.
    private void CheckNetwork()
    {
        lock (_lock)
        {
            if (_checkTimer is null)
            {
                // Stopped meanwhile.
                return;
            }

            _checkSoon = false;
            try
            {
                SyncListeners();
            }
            catch (Exception)
            {
                // Reading the adapters failed this time; the next check tries again.
            }

            _checkTimer.Change(CheckInterval, Timeout.InfiniteTimeSpan);
        }
    }

    // Called with _lock held.
    private void SyncListeners()
    {
        var listening = _listeners.ToDictionary(l => l.Key, l => l.Value.IsReceiving);
        var (close, open) = PlanChanges(listening, LocalAddresses());
        foreach (var address in close)
        {
            if (_listeners.Remove(address, out var listener))
            {
                listener.Close();
            }
        }

        foreach (var address in open)
        {
            if (Listener.TryOpen(address, _onPacket!) is { } listener)
            {
                _listeners[address] = listener;
            }
        }
    }

    private static IEnumerable<IPAddress> LocalAddresses()
    {
        return NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .SelectMany(n => n.GetIPProperties().UnicastAddresses)
            .Select(a => a.Address)
            .Where(a => a.AddressFamily == AddressFamily.InterNetwork)
            .Distinct();
    }

    /// <summary>One raw socket and the thread that reads it.</summary>
    private sealed class Listener
    {
        private readonly Socket _socket;
        private readonly Thread _thread;
        private volatile bool _closed;

        private Listener(Socket socket, Action<byte[]> onPacket)
        {
            _socket = socket;
            _thread = new Thread(() => ReceiveLoop(onPacket)) { IsBackground = true, Name = $"Captura {socket.LocalEndPoint}" };
            _thread.Start();
        }

        /// <summary>False once the thread stopped on its own, for example when the adapter went away.</summary>
        public bool IsReceiving => _thread.IsAlive;

        public static Listener? TryOpen(IPAddress address, Action<byte[]> onPacket)
        {
            if (!OperatingSystem.IsWindows())
            {
                return null;
            }

            var socket = new Socket(AddressFamily.InterNetwork, SocketType.Raw, ProtocolType.IP);
            try
            {
                socket.Bind(new IPEndPoint(address, 0));
                socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.HeaderIncluded, true);
                socket.IOControl(IOControlCode.ReceiveAll, [1, 0, 0, 0], new byte[4]);
                socket.ReceiveBufferSize = 4 * 1024 * 1024;
            }
            catch (SocketException)
            {
                // An adapter that refuses raw access is skipped (and tried again at the next check);
                // the others keep working.
                socket.Dispose();
                return null;
            }

            return new Listener(socket, onPacket);
        }

        public void Close()
        {
            _closed = true;
            _socket.Dispose();
        }

        public void Join(TimeSpan timeout) => _thread.Join(timeout);

        private void ReceiveLoop(Action<byte[]> onPacket)
        {
            var buffer = new byte[65536];
            while (!_closed)
            {
                int length;
                try
                {
                    length = _socket.Receive(buffer);
                }
                catch (Exception e) when (e is SocketException or ObjectDisposedException)
                {
                    // Closed, or the adapter went away. If its address is still there, the next check reopens it.
                    return;
                }

                if (length > 0 && IsGamePacket(buffer.AsSpan(0, length)))
                {
                    try
                    {
                        onPacket(buffer[..length]);
                    }
                    catch (Exception)
                    {
                        // One bad packet must not end this thread: that would stop the capture for good.
                    }
                }
            }
        }
    }
}
