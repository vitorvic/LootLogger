using System.IO;
using System.Net.Http;
using LootLogger.Capture;
using LootLogger.Core.Data;
using LootLogger.Core.Network;
using LootLogger.Core.Protocol;
using LootLogger.Core.Tracking;

namespace LootLogger.App.Services;

/// <summary>Wires capture, parsing and tracking together. Events fire on the capture thread.</summary>
public sealed class CaptureService : IDisposable
{
    private readonly PacketCapture _capture = new();
    private readonly AlbionMessageParser _parser = new();
    private readonly Lock _parseLock = new();

    public CaptureService()
    {
        Codes = GameCodes.LoadOrDefault(AppPaths.CodesFile);
        if (!File.Exists(AppPaths.CodesFile))
        {
            // Written once so the numbers can be edited after a game patch.
            try
            {
                Codes.Save(AppPaths.CodesFile);
            }
            catch (IOException)
            {
            }
        }

        Items = ItemDatabase.LoadCachedOrBuiltIn(AppPaths.ItemsCache);
        Clusters = ClusterDatabase.LoadBuiltIn();
        Values = MarketValueCache.Load(AppPaths.MarketValues);
        Tracker = new LootTracker(Codes, Items, Clusters, Values);

        _parser.MessageReceived += Tracker.Handle;
        _capture.PayloadReceived += OnPayload;
        _capture.GameTrafficDetected += () => GameTrafficDetected?.Invoke();
        _capture.ServerAddressChanged += address => ServerChanged?.Invoke(GameServers.RegionOf(address));
    }

    public event Action? GameTrafficDetected;

    public event Action<ServerRegion>? ServerChanged;

    public GameCodes Codes { get; }
    public ItemDatabase Items { get; }
    public ClusterDatabase Clusters { get; }
    public MarketValueCache Values { get; }
    public LootTracker Tracker { get; }

    public bool IsRunning => _capture.IsRunning;

    public static bool IsNpcapInstalled() => PacketCapture.IsDriverAvailable(out _);

    /// <summary>Npcap is only needed for a chosen adapter, or when not running as administrator.</summary>
    public static bool NeedsNpcap(string? adapterId) => adapterId is not null || !PacketCapture.CanUseRawSockets;

    public static IReadOnlyList<NetworkAdapter> ListAdapters()
    {
        try
        {
            return PacketCapture.ListAdapters();
        }
        catch (Exception)
        {
            return [];
        }
    }

    public void Start(string? adapterId, string? recordPath = null) => _capture.Start(adapterId, recordPath);

    public void Stop() => _capture.Stop();

    public string? RecordingError => _capture.RecordingError;

    /// <summary>Plays a recorded .pcap through the tracker, as if it were live.</summary>
    public Task ReplayAsync(string pcapPath) => Task.Run(() => PacketCapture.Replay(pcapPath, OnPayload));

    public async Task<bool> RefreshItemsAsync()
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("LootLogger");
            return await Items.RefreshAsync(http, AppPaths.ItemsCache);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException or System.Text.Json.JsonException)
        {
            return false;
        }
    }

    public void SaveValues()
    {
        try
        {
            Values.Save(AppPaths.MarketValues);
        }
        catch (IOException)
        {
        }
    }

    public void Dispose()
    {
        _capture.Dispose();
        SaveValues();
    }

    private void OnPayload(byte[] payload)
    {
        lock (_parseLock)
        {
            try
            {
                _parser.ReceivePacket(payload);
            }
            catch (Exception)
            {
                // A malformed packet must never stop the capture.
            }
        }
    }
}
