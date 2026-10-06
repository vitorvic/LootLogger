using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Principal;

namespace LootLogger.Capture;

/// <summary>
/// Reads IP packets straight from Windows with raw sockets, one per local address.
/// Unlike Npcap this also sees traffic that tools such as ExitLag route through their own drivers,
/// but Windows only allows it to administrators.
/// </summary>
/// <remarks>Approach based on SocketsPacketProvider from AlbionOnline-StatisticsAnalysis by Triky313 (GPL-3.0).</remarks>
public sealed class RawSocketCapture
{
    private readonly List<Socket> _sockets = [];
    private readonly List<Thread> _threads = [];
    private volatile bool _stopping;

    public static bool IsAdministrator()
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    public bool IsRunning => _sockets.Count > 0;

    /// <summary>Opens one socket per local IPv4 address and calls <paramref name="onPacket"/> with each IP packet.</summary>
    public void Start(Action<byte[]> onPacket)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException();
        }

        Stop();
        _stopping = false;

        foreach (var address in LocalAddresses())
        {
            var socket = new Socket(AddressFamily.InterNetwork, SocketType.Raw, ProtocolType.IP);
            try
            {
                socket.Bind(new IPEndPoint(address, 0));
                socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.HeaderIncluded, true);
                socket.IOControl(IOControlCode.ReceiveAll, [1, 0, 0, 0], new byte[4]);
                socket.ReceiveBufferSize = 4 * 1024 * 1024;
                _sockets.Add(socket);
            }
            catch (SocketException)
            {
                // An adapter that refuses raw access is skipped; the others keep working.
                socket.Dispose();
            }
        }

        if (_sockets.Count == 0)
        {
            throw new InvalidOperationException("Nenhuma placa de rede pôde ser aberta. Abra o LootLogger como administrador.");
        }

        foreach (var socket in _sockets)
        {
            var thread = new Thread(() => ReceiveLoop(socket, onPacket)) { IsBackground = true, Name = $"Captura {socket.LocalEndPoint}" };
            _threads.Add(thread);
            thread.Start();
        }
    }

    public void Stop()
    {
        _stopping = true;
        foreach (var socket in _sockets)
        {
            socket.Dispose();
        }

        foreach (var thread in _threads)
        {
            thread.Join(TimeSpan.FromSeconds(2));
        }

        _sockets.Clear();
        _threads.Clear();
    }

    private void ReceiveLoop(Socket socket, Action<byte[]> onPacket)
    {
        var buffer = new byte[65536];
        while (!_stopping)
        {
            int length;
            try
            {
                length = socket.Receive(buffer);
            }
            catch (Exception e) when (e is SocketException or ObjectDisposedException)
            {
                // Closed by Stop, or the adapter went away.
                return;
            }

            if (length > 0 && IsGamePacket(buffer.AsSpan(0, length)))
            {
                onPacket(buffer[..length]);
            }
        }
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

    private static IEnumerable<IPAddress> LocalAddresses()
    {
        return NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .SelectMany(n => n.GetIPProperties().UnicastAddresses)
            .Select(a => a.Address)
            .Where(a => a.AddressFamily == AddressFamily.InterNetwork)
            .Distinct();
    }
}
