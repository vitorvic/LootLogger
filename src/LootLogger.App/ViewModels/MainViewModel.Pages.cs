using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LootLogger.App.Localization;
using LootLogger.App.Services;
using LootLogger.Capture;
using LootLogger.Core.Chest;
using LootLogger.Core.Tracking;

namespace LootLogger.App.ViewModels;

public sealed partial class MainViewModel
{
    // ---------- Loot log ----------

    public ObservableCollection<LootRow> LootRows { get; } = [];

    public ICollectionView LootView { get; }

    [ObservableProperty]
    private string _searchText = string.Empty;

    /// <summary>Which loot the list shows: everyone's, only ours, or only our guild's.</summary>
    [ObservableProperty]
    private LootFilter _lootFilter;

    /// <summary>True on the "Estatísticas" tab, false on "Lista".</summary>
    [ObservableProperty]
    private bool _showLootStats;

    public string LootSummary
    {
        get
        {
            var mine = LootRows.Where(r => r.IsMine).ToList();
            var text = L.Format("LootSummary",
                Format.Silver(LootRows.Sum(r => r.Quantity)),
                Format.Compact(LootRows.Sum(r => r.Entry.TotalValue)));
            return mine.Count == 0
                ? text
                : text + " · " + L.Format("LootSummaryMine", Format.Silver(mine.Sum(r => r.Quantity)), Format.Compact(mine.Sum(r => r.Entry.TotalValue)));
        }
    }

    partial void OnSearchTextChanged(string value) => LootView.Refresh();

    partial void OnLootFilterChanged(LootFilter value) => LootView.Refresh();

    partial void OnShowLootStatsChanged(bool value)
    {
        if (value)
        {
            RefreshLootStats();
        }
    }

    private bool IsMine(LootEntry entry) =>
        Player is { Name.Length: > 0 } p && string.Equals(entry.LootedByName, p.Name, StringComparison.OrdinalIgnoreCase);

    private bool FilterLoot(object o)
    {
        if (o is not LootRow row)
        {
            return false;
        }

        switch (LootFilter)
        {
            case LootFilter.Mine when !row.IsMine:
            case LootFilter.MyGuild when Player is not { Guild.Length: > 0 } p
                                         || !string.Equals(row.LootedByGuild, p.Guild, StringComparison.OrdinalIgnoreCase):
                return false;
        }

        var q = SearchText.Trim();
        return q.Length == 0
               || row.LootedBy.Contains(q, StringComparison.OrdinalIgnoreCase)
               || row.LootedFrom.Contains(q, StringComparison.OrdinalIgnoreCase)
               || row.ItemName.Contains(q, StringComparison.OrdinalIgnoreCase)
               || row.Entry.ItemId.Contains(q, StringComparison.OrdinalIgnoreCase);
    }

    // ---------- Loot statistics ----------

    private const int TopCount = 5;
    private bool _lootStatsDirty = true;

    public ObservableCollection<RankRow> TopByItems { get; } = [];

    public ObservableCollection<RankRow> TopByValue { get; } = [];

    public ObservableCollection<LootRow> TopItems { get; } = [];

    [ObservableProperty]
    private string _statsItems = "0";

    [ObservableProperty]
    private string _statsValue = "0";

    [ObservableProperty]
    private string _statsPlayers = "0";

    [ObservableProperty]
    private string _statsMine = "0";

    private void RefreshLootStats()
    {
        _lootStatsDirty = false;
        var players = LootRows
            .GroupBy(r => r.LootedBy, StringComparer.OrdinalIgnoreCase)
            .Select(g => (Name: g.First().LootedBy, Guild: g.First().LootedByGuild, Mine: g.First().IsMine,
                Items: g.Sum(r => (long) r.Quantity), Value: g.Sum(r => r.Entry.TotalValue)))
            .ToList();

        StatsItems = Format.Silver(LootRows.Sum(r => r.Quantity));
        StatsValue = Format.Compact(LootRows.Sum(r => r.Entry.TotalValue));
        StatsPlayers = Format.Silver(players.Count);
        StatsMine = Format.Compact(LootRows.Where(r => r.IsMine).Sum(r => r.Entry.TotalValue));

        Fill(TopByItems, players.OrderByDescending(p => p.Items).Take(TopCount)
            .Select((p, i) => new RankRow(i + 1, p.Name, p.Guild, p.Mine, p.Items, Format.Silver(p.Items))));
        Fill(TopByValue, players.Where(p => p.Value > 0).OrderByDescending(p => p.Value).Take(TopCount)
            .Select((p, i) => new RankRow(i + 1, p.Name, p.Guild, p.Mine, p.Value, Format.Compact(p.Value))));
        Fill(TopItems, LootRows.Where(r => r.HasValue).OrderByDescending(r => r.Entry.TotalValue).Take(TopCount));
    }

    private static void Fill<T>(ObservableCollection<T> target, IEnumerable<T> rows)
    {
        target.Clear();
        foreach (var row in rows)
        {
            target.Add(row);
        }

        if (target is ObservableCollection<RankRow> ranks && ranks.Count > 0)
        {
            var max = Math.Max(1, ranks[0].Amount);
            foreach (var rank in ranks)
            {
                rank.BarFraction = (double) rank.Amount / max;
            }
        }
    }

    // ---------- Combat ----------

    private static readonly TimeSpan LootAfterDeathWindow = TimeSpan.FromMinutes(10);

    public ObservableCollection<KillRow> KillRows { get; } = [];

    public ICollectionView CombatView { get; }

    [ObservableProperty]
    private bool _onlyMyGuild = true;

    [ObservableProperty]
    private int _kills;

    [ObservableProperty]
    private int _deaths;

    partial void OnOnlyMyGuildChanged(bool value)
    {
        CombatView.Refresh();
        RefreshCombatTiles();
    }

    private string MyGuild => Player?.Guild ?? string.Empty;

    private bool FilterKill(object o)
    {
        if (o is not KillRow row || !OnlyMyGuild || MyGuild.Length == 0)
        {
            return o is KillRow;
        }

        return string.Equals(row.DiedGuild, MyGuild, StringComparison.OrdinalIgnoreCase)
               || string.Equals(row.KillerGuild, MyGuild, StringComparison.OrdinalIgnoreCase);
    }

    private void RefreshCombatTiles()
    {
        if (MyGuild.Length == 0)
        {
            Kills = KillRows.Count;
            Deaths = 0;
            return;
        }

        Kills = KillRows.Count(k => string.Equals(k.KillerGuild, MyGuild, StringComparison.OrdinalIgnoreCase));
        Deaths = KillRows.Count(k => string.Equals(k.DiedGuild, MyGuild, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>What a dead player "lost" is whatever was looted from them shortly after.</summary>
    private void UpdateLostItems(LootEntry loot)
    {
        var death = KillRows.FirstOrDefault(k =>
            string.Equals(k.Died, loot.LootedFromName, StringComparison.OrdinalIgnoreCase)
            && loot.UtcTime >= k.Entry.UtcTime
            && loot.UtcTime - k.Entry.UtcTime <= LootAfterDeathWindow);
        if (death is null)
        {
            return;
        }

        var taken = LootRows
            .Where(r => string.Equals(r.Entry.LootedFromName, death.Died, StringComparison.OrdinalIgnoreCase)
                        && r.Entry.UtcTime >= death.Entry.UtcTime
                        && r.Entry.UtcTime - death.Entry.UtcTime <= LootAfterDeathWindow)
            .OrderByDescending(r => r.Entry.TotalValue)
            .ToList();
        var names = taken.Take(3).Select(r => r.ItemName).ToList();
        death.Lost = taken.Count > 3 ? $"{string.Join(", ", names)} +{taken.Count - 3}" : string.Join(", ", names);
        death.LostValue = taken.Sum(r => r.Entry.TotalValue);
    }

    // ---------- Settings ----------

    public ObservableCollection<NetworkAdapter> Adapters { get; } = [];

    [ObservableProperty]
    private NetworkAdapter? _selectedAdapter;

    [ObservableProperty]
    private bool _partyOnly;

    [ObservableProperty]
    private bool _isPortuguese = true;

    [ObservableProperty]
    private string _exportFolder = string.Empty;

    [ObservableProperty]
    private bool _recordCapture;

    [ObservableProperty]
    private string _itemListText = string.Empty;

    private bool _loadingSettings;

    private void LoadSettingsState()
    {
        _loadingSettings = true;
        PartyOnly = _settings.PartyOnly;
        IsPortuguese = Loc.Instance.Language != "en";
        ExportFolder = _settings.ExportFolder;

        Adapters.Clear();
        Adapters.Add(new NetworkAdapter(string.Empty, L["AdapterAuto"]));
        foreach (var adapter in CaptureService.ListAdapters())
        {
            Adapters.Add(adapter);
        }

        SelectedAdapter = Adapters.FirstOrDefault(a => a.Id == _settings.AdapterId) ?? Adapters[0];
        ItemListText = L.Format("ItemListHelp", Format.Silver(_service.Items.Count));
        _loadingSettings = false;
    }

    partial void OnPartyOnlyChanged(bool value)
    {
        _service.Tracker.PartyOnly = value;
        SaveSetting(s => s.PartyOnly = value);
    }

    // Capture starts on its own, so restart it for the recording to begin or end right away.
    partial void OnRecordCaptureChanged(bool value)
    {
        if (IsCapturing)
        {
            StopCapture();
            StartCapture();
        }
    }

    partial void OnIsPortugueseChanged(bool value)
    {
        if (_loadingSettings)
        {
            return;
        }

        var language = value ? "pt-BR" : "en";
        Loc.Instance.SetLanguage(language);
        SaveSetting(s => s.Language = language);
        Adapters[0] = new NetworkAdapter(string.Empty, L["AdapterAuto"]);
        ItemListText = L.Format("ItemListHelp", Format.Silver(_service.Items.Count));
    }

    partial void OnSelectedAdapterChanged(NetworkAdapter? value)
    {
        if (_loadingSettings || value is null)
        {
            return;
        }

        SaveSetting(s => s.AdapterId = value.Id.Length == 0 ? null : value.Id);
        NpcapMissing = CaptureService.NeedsNpcap(_settings.AdapterId) && !CaptureService.IsNpcapInstalled();
        if (IsCapturing)
        {
            StopCapture();
            StartCapture();
        }
    }

    [RelayCommand]
    private void ChangeExportFolder()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { InitialDirectory = EnsureFolder(ExportFolder) };
        if (dialog.ShowDialog() == true)
        {
            ExportFolder = dialog.FolderName;
            SaveSetting(s => s.ExportFolder = dialog.FolderName);
        }
    }

    [RelayCommand]
    private void OpenExportFolder() => OpenUrl(EnsureFolder(ExportFolder));

    [RelayCommand]
    private async Task UpdateItemsAsync()
    {
        var ok = await _service.RefreshItemsAsync();
        ItemListText = L.Format("ItemListHelp", Format.Silver(_service.Items.Count));
        Message = ok ? L["ItemListUpdated"] : L["ItemListFailed"];
    }

    /// <summary>Refreshes the item list at most once a day, without messages.</summary>
    public async Task UpdateItemsSilentlyAsync()
    {
        if (File.Exists(AppPaths.ItemsCache) && DateTime.UtcNow - File.GetLastWriteTimeUtc(AppPaths.ItemsCache) < TimeSpan.FromDays(1))
        {
            return;
        }

        if (await _service.RefreshItemsAsync())
        {
            await _dispatcher.InvokeAsync(() => ItemListText = L.Format("ItemListHelp", Format.Silver(_service.Items.Count)));
        }
    }

    [RelayCommand]
    private async Task ReplayRecordingAsync()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "pcap (*.pcap)|*.pcap", InitialDirectory = AppPaths.DataFolder };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        try
        {
            await _service.ReplayAsync(dialog.FileName);
        }
        catch (Exception e)
        {
            Message = e.Message;
        }
    }

    [RelayCommand]
    private static void OpenDataFolder() => OpenUrl(EnsureFolder(AppPaths.DataFolder));

    [RelayCommand]
    private static void OpenProjectPage() => OpenUrl("https://github.com/vitorvic/LootLogger");

    private void SaveSetting(Action<AppSettings> change)
    {
        if (_loadingSettings)
        {
            return;
        }

        change(_settings);
        try
        {
            _settings.Save();
        }
        catch (IOException)
        {
        }
    }
}
