using System.IO;
using System.Net.Http;
using LootLogger.Capture;
using LootLogger.Core.Combat;
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
        Values = MarketValueCache.Load(AppPaths.MarketValues, AppPaths.ReservePrices);
        Tracker = new LootTracker(Codes, Items, Clusters, Values);

        Damage = new DamageMeter(Codes, Tracker.PartyMemberName, () => Tracker.ClusterName, () => Tracker.LocalPlayer?.Name);

        // The tracker goes first so the meter already knows who a new player or map is.
        _parser.MessageReceived += Tracker.Handle;
        _parser.MessageReceived += Damage.Handle;
        _capture.PayloadReceived += OnPayload;
        _capture.GameTrafficDetected += () => GameTrafficDetected?.Invoke();
        _capture.ServerAddressChanged += OnServerAddress;
    }

    public event Action? GameTrafficDetected;

    public event Action<ServerRegion>? ServerChanged;

    public GameCodes Codes { get; }
    public ItemDatabase Items { get; }
    public ClusterDatabase Clusters { get; }
    public MarketValueCache Values { get; }
    public LootTracker Tracker { get; }
    public DamageMeter Damage { get; }

    public bool IsRunning => _capture.IsRunning;

    /// <summary>Listens on every adapter (Windows sockets as administrator, so no Npcap and works with ExitLag).</summary>
    public void Start(string? recordPath = null) => _capture.Start(null, recordPath);

    public void Stop() => _capture.Stop();

    public string? RecordingError => _capture.RecordingError;

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

    /// <summary>Reserve prices for items the game never priced; null when the price list could not be reached.</summary>
    public async Task<Dictionary<string, long>?> FetchReservePricesAsync(ServerRegion region, IReadOnlyCollection<string> itemIds)
    {
        try
        {
            return await AlbionDataPrices.FetchAsync(PriceHttp, region, itemIds, DateTime.UtcNow);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            return null;
        }
    }

    private static readonly HttpClient PriceHttp = CreatePriceHttp();

    private static HttpClient CreatePriceHttp()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("LootLogger");
        return http;
    }

    public void SaveValues()
    {
        try
        {
            Values.Save(AppPaths.MarketValues, AppPaths.ReservePrices);
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

    // The game talks to the main server and the map server at once, so packets alternate between
    // two addresses. An address we don't know must not wipe a region we already found, or the
    // title bar flickers several times a second.
    private void OnServerAddress(uint address)
    {
        var region = GameServers.RegionOf(address);
        if (region != ServerRegion.Unknown)
        {
            ServerChanged?.Invoke(region);
        }
    }

    private void OnPayload(byte[] payload, bool fromServer)
    {
        lock (_parseLock)
        {
            try
            {
                _parser.ReceivePacket(payload, fromServer);
            }
            catch (Exception)
            {
                // A malformed packet must never stop the capture.
            }
        }
    }
}
