using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Data;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LootLogger.App.Localization;
using LootLogger.App.Services;
using LootLogger.Core.Export;
using LootLogger.Core.Session;
using LootLogger.Core.Network;
using LootLogger.Core.Tracking;

namespace LootLogger.App.ViewModels;

public enum Page
{
    Dashboard,
    LootLog,
    Chest,
    Combat,
    Settings,
    News,
    Help,
    About
}

/// <summary>State behind every screen. Runs on the UI thread; tracker events are marshalled here.</summary>
public sealed partial class MainViewModel : ObservableObject, IDisposable
{
    private const int MaxFeedItems = 200;

    private readonly CaptureService _service;
    private readonly AppSettings _settings;
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _clock;
    private LootSession _session;

    public MainViewModel(CaptureService service, AppSettings settings)
    {
        _service = service;
        _settings = settings;
        _dispatcher = Application.Current.Dispatcher;
        _session = new LootSession(DateTime.UtcNow, settings.ExportFolder);

        LootView = CollectionViewSource.GetDefaultView(LootRows);
        LootView.Filter = FilterLoot;
        CombatView = CollectionViewSource.GetDefaultView(KillRows);
        CombatView.Filter = FilterKill;
        InitChest();

        _service.Tracker.PartyOnly = settings.PartyOnly;
        _service.Tracker.LootAdded += e => _dispatcher.BeginInvoke(() => OnLoot(e));
        _service.Tracker.KillAdded += e => _dispatcher.BeginInvoke(() => OnKill(e));
        _service.Tracker.PlayerIdentified += p => _dispatcher.BeginInvoke(() => OnPlayer(p));
        _service.Tracker.ClusterChanged += (c, tier) => _dispatcher.BeginInvoke(() => OnCluster(c, tier));
        _service.ServerChanged += r => _dispatcher.BeginInvoke(() => Server = r);
        _service.Tracker.PartyChanged += () => _dispatcher.BeginInvoke(UpdateParty);
        _service.GameTrafficDetected += () => _dispatcher.BeginInvoke(() =>
        {
            IsGameDetected = true;
            AddInfo("FeedGameDetected");
        });

        Loc.Instance.PropertyChanged += (_, _) => Relocalize();

        NpcapMissing = CaptureService.NeedsNpcap(_settings.AdapterId) && !CaptureService.IsNpcapInstalled();
        LoadSettingsState();
        AddInfo("FeedSessionStarted");

        _clock = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _clock.Tick += (_, _) => RefreshTiles();
        _clock.Start();
        RefreshTiles();
    }

    public Loc L => Loc.Instance;

    public string Version { get; } = typeof(MainViewModel).Assembly.GetName().Version?.ToString(3) ?? "0.1.0";

    [ObservableProperty]
    private Page _page = Page.Dashboard;

    [ObservableProperty]
    private string? _message;

    // ---------- Status ----------

    [ObservableProperty]
    private bool _isCapturing;

    [ObservableProperty]
    private bool _isGameDetected;

    [ObservableProperty]
    private bool _npcapMissing;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPlayerIdentified), nameof(GuildText), nameof(InGameText))]
    private LocalPlayer? _player;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCluster))]
    private string _clusterName = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TierText), nameof(HasTier))]
    private int _clusterTier;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ServerText), nameof(IsServerKnown))]
    private ServerRegion _server;

    [ObservableProperty]
    private string _partyText = string.Empty;

    public bool IsPlayerIdentified => Player is not null;

    /// <summary>"PIVAS · OOPS", or just the guild, or nothing.</summary>
    public string GuildText => Player is null
        ? string.Empty
        : string.Join(" · ", new[] { Player.Guild, Player.Alliance }.Where(t => !string.IsNullOrEmpty(t)));

    public bool HasCluster => ClusterName.Length > 0;

    // Cities and starter zones are tier 1; only real tiers are worth a badge.
    public bool HasTier => ClusterTier >= 2;

    public string TierText => $"T{ClusterTier}";

    public string InGameText => IsPlayerIdentified ? L["InGame"] : L["OutOfGame"];

    public bool IsServerKnown => Server != ServerRegion.Unknown;

    public string ServerText => L["Server" + Server];

    // ---------- Dashboard ----------

    public ObservableCollection<FeedItem> Feed { get; } = [];

    [ObservableProperty]
    private string _totalItems = "0";

    [ObservableProperty]
    private string _lastHourItems = "0";

    [ObservableProperty]
    private string _activeTime = "00:00";

    [ObservableProperty]
    private string _activeSeconds = ":00";

    [ObservableProperty]
    private string _totalValue = "0";

    [ObservableProperty]
    private string _totalValueUnit = string.Empty;

    public bool HasFeed => Feed.Any(f => f.Kind != FeedKind.Info);

    public void StartCapture()
    {
        if (IsCapturing)
        {
            return;
        }

        NpcapMissing = CaptureService.NeedsNpcap(_settings.AdapterId) && !CaptureService.IsNpcapInstalled();
        if (NpcapMissing)
        {
            Page = Page.Dashboard;
            return;
        }

        try
        {
            var record = RecordCapture
                ? Path.Combine(AppPaths.DataFolder, $"captura-{DateTime.Now:yyyy-MM-dd-HH-mm-ss}.pcap")
                : null;
            _service.Start(_settings.AdapterId, record);
            IsCapturing = true;
            IsGameDetected = false;
            _session.MarkCaptureStarted(DateTime.UtcNow);
            AddInfo("FeedCaptureStarted");
            Message = _service.RecordingError is { } recordError ? L.Format("RecordError", recordError) : null;
        }
        catch (Exception e)
        {
            Message = L.Format("CaptureError", e.Message);
        }
    }

    public void StopCapture()
    {
        _service.Stop();
        IsCapturing = false;
        IsGameDetected = false;
        _session.MarkCaptureStopped(DateTime.UtcNow);
        _service.SaveValues();
        AddInfo("FeedCaptureStopped");
    }

    [RelayCommand]
    private void ExportCsv()
    {
        if (_session.Loot.Count == 0 && _session.Kills.Count == 0)
        {
            Message = L["NothingToExport"];
            return;
        }

        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            FileName = CsvExporter.DefaultFileName(DateTime.UtcNow, Player?.Name),
            DefaultExt = ".csv",
            Filter = "CSV (*.csv)|*.csv",
            InitialDirectory = EnsureFolder(_settings.ExportFolder)
        };

        if (dialog.ShowDialog() == true)
        {
            CsvExporter.Write(dialog.FileName, _session.Loot, _session.Kills);
            Message = L.Format("Exported", dialog.FileName);
        }
    }

    [RelayCommand]
    private void NewSession()
    {
        var wasCapturing = IsCapturing;
        _session.MarkCaptureStopped(DateTime.UtcNow);
        _session = new LootSession(DateTime.UtcNow, _settings.ExportFolder) { Owner = Player?.Name };
        if (wasCapturing)
        {
            _session.MarkCaptureStarted(DateTime.UtcNow);
        }

        Feed.Clear();
        LootRows.Clear();
        KillRows.Clear();
        _lootStatsDirty = true;
        if (ShowLootStats)
        {
            RefreshLootStats();
        }
        RecomputeChest();
        AddInfo("FeedSessionStarted");
        RefreshTiles();
        OnPropertyChanged(nameof(LootSummary));
    }

    [RelayCommand]
    private static void DownloadNpcap() => OpenUrl("https://npcap.com/#download");

    [RelayCommand]
    private void Navigate(Page page) => Page = page;

    private void OnLoot(LootEntry entry)
    {
        _session.Add(entry);
        var row = new LootRow(entry, _service.Items) { IsMine = IsMine(entry) };
        LootRows.Insert(0, row);
        _lootStatsDirty = true;

        var quantity = entry.Quantity > 1 ? $"{entry.Quantity}× " : string.Empty;
        AddFeed(new FeedItem(
            entry.UtcTime.ToLocalTime(),
            FeedKind.Loot,
            entry.LootedByName,
            Loc.Instance.Language == "en" ? " looted " : " pegou ",
            quantity + row.ItemName,
            (Loc.Instance.Language == "en" ? " from " : " de ") + row.LootedFrom,
            entry.TotalValue > 0 ? Format.Silver(entry.TotalValue) : string.Empty));

        UpdateLostItems(entry);
        _chestDirty = true;
        RefreshTiles();
        OnPropertyChanged(nameof(LootSummary));
    }

    private void OnKill(KillEntry entry)
    {
        _session.Add(entry);
        KillRows.Insert(0, new KillRow(entry));
        AddFeed(new FeedItem(entry.UtcTime.ToLocalTime(), FeedKind.Kill, string.Empty,
            L.Format("FeedKill", entry.Died, entry.DiedGuild, entry.KilledBy, entry.KilledByGuild), string.Empty, string.Empty, L["NavCombat"]));
        RefreshCombatTiles();
    }

    private void OnPlayer(LocalPlayer player)
    {
        Player = player;
        _session.Owner = player.Name;
        foreach (var row in LootRows)
        {
            row.IsMine = IsMine(row.Entry);
        }

        _lootStatsDirty = true;
        LootView.Refresh();
        OnPropertyChanged(nameof(LootSummary));
        UpdateParty();
        RefreshCombatTiles();
        CombatView.Refresh();
    }

    private void OnCluster(string cluster, int tier)
    {
        if (cluster.Length > 0 && cluster != ClusterName)
        {
            ClusterName = cluster;
            AddFeed(new FeedItem(DateTime.Now, FeedKind.Info, string.Empty, L.Format("FeedMapChanged", cluster), string.Empty, string.Empty, string.Empty));
        }

        ClusterTier = tier;
    }

    private void UpdateParty()
    {
        var count = _service.Tracker.PartyMembers.Count;
        PartyText = count > 1 ? L.Format("PartyCount", count) : L["Solo"];
    }

    private void AddInfo(string key) =>
        AddFeed(new FeedItem(DateTime.Now, FeedKind.Info, string.Empty, L[key], string.Empty, string.Empty, string.Empty));

    private void AddFeed(FeedItem item)
    {
        Feed.Insert(0, item);
        while (Feed.Count > MaxFeedItems)
        {
            Feed.RemoveAt(Feed.Count - 1);
        }

        OnPropertyChanged(nameof(HasFeed));
    }

    private void RefreshTiles()
    {
        var now = DateTime.UtcNow;
        TotalItems = Format.Silver(_session.TotalItems);
        LastHourItems = Format.Silver(_session.ItemsSince(now.AddHours(-1)));
        var active = _session.ActiveTime(now);
        ActiveTime = $"{(int) active.TotalHours:00}:{active.Minutes:00}";
        ActiveSeconds = $":{active.Seconds:00}";
        (TotalValue, TotalValueUnit) = Format.Short(_session.TotalValue);

        if (_chestDirty && Page == Page.Chest)
        {
            _chestDirty = false;
            RecomputeChest();
        }

        if (_lootStatsDirty && ShowLootStats)
        {
            RefreshLootStats();
        }
    }

    private void Relocalize()
    {
        OnPropertyChanged(nameof(InGameText));
        OnPropertyChanged(nameof(ServerText));
        OnPropertyChanged(nameof(LootSummary));
        foreach (var row in LootRows)
        {
            row.Relocalize();
        }

        _lootStatsDirty = true;
        UpdateParty();
        RecomputeChest();
        RefreshTiles();
    }

    private static string EnsureFolder(string folder)
    {
        try
        {
            Directory.CreateDirectory(folder);
            return folder;
        }
        catch (Exception)
        {
            return Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        }
    }

    internal static void OpenUrl(string url) => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });

    public void Dispose()
    {
        _clock.Stop();
        if (IsCapturing)
        {
            StopCapture();
        }

        _service.Dispose();
    }
}
