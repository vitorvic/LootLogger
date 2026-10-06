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

    public ObservableCollection<string> Guilds { get; } = [Loc.Instance["AllGuilds"]];

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private int _selectedGuildIndex;

    public string LootSummary => L.Format("LootSummary",
        Format.Silver(LootRows.Sum(r => r.Quantity)),
        LootRows.Select(r => r.LootedBy).Distinct(StringComparer.OrdinalIgnoreCase).Count());

    partial void OnSearchTextChanged(string value) => LootView.Refresh();

    partial void OnSelectedGuildIndexChanged(int value) => LootView.Refresh();

    private bool FilterLoot(object o)
    {
        if (o is not LootRow row)
        {
            return false;
        }

        if (SelectedGuildIndex > 0 && SelectedGuildIndex < Guilds.Count
            && !string.Equals(row.LootedByGuild, Guilds[SelectedGuildIndex], StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var q = SearchText.Trim();
        return q.Length == 0
               || row.LootedBy.Contains(q, StringComparison.OrdinalIgnoreCase)
               || row.LootedFrom.Contains(q, StringComparison.OrdinalIgnoreCase)
               || row.ItemName.Contains(q, StringComparison.OrdinalIgnoreCase)
               || row.Entry.ItemId.Contains(q, StringComparison.OrdinalIgnoreCase);
    }

    private void AddGuild(string guild)
    {
        if (guild.Length > 0 && !Guilds.Skip(1).Contains(guild, StringComparer.OrdinalIgnoreCase))
        {
            Guilds.Add(guild);
        }
    }

    // ---------- Chest ----------

    private readonly List<ChestLogEntry> _chestEntries = [];
    private bool _chestDirty;

    public ObservableCollection<ChestRow> ChestRows { get; } = [];

    [ObservableProperty]
    private string _chestMessage = string.Empty;

    [ObservableProperty]
    private string _chestLoot = "0";

    [ObservableProperty]
    private string _chestDeposited = "0";

    [ObservableProperty]
    private string _chestMissing = "0";

    [ObservableProperty]
    private string _chestMissingValue = "0";

    [ObservableProperty]
    private string _chestMissingValueUnit = string.Empty;

    public bool HasChestLog => _chestEntries.Count > 0;

    [RelayCommand]
    private void PasteChestLog()
    {
        string text;
        try
        {
            text = Clipboard.GetText();
        }
        catch (Exception)
        {
            text = string.Empty;
        }

        AddChestText(text);
    }

    [RelayCommand]
    private void OpenChestFile()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "Texto (*.txt;*.csv;*.tsv)|*.txt;*.csv;*.tsv|*.*|*.*", Multiselect = true };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        AddChestText(string.Join('\n', dialog.FileNames.Select(File.ReadAllText)));
    }

    [RelayCommand]
    private void ClearChest()
    {
        _chestEntries.Clear();
        ChestMessage = string.Empty;
        RecomputeChest();
    }

    private void AddChestText(string text)
    {
        var entries = ChestLogParser.Parse(text);
        if (entries.Count == 0)
        {
            ChestMessage = L["ChestNothingRead"];
            return;
        }

        _chestEntries.AddRange(entries);
        ChestMessage = L.Format("ChestLinesRead", _chestEntries.Count);
        RecomputeChest();
    }

    private void RecomputeChest()
    {
        ChestRows.Clear();
        OnPropertyChanged(nameof(HasChestLog));
        var loot = _session.Loot;
        ChestLoot = Format.Silver(loot.Sum(l => l.Quantity));
        if (_chestEntries.Count == 0)
        {
            ChestDeposited = "—";
            ChestMissing = "—";
            ChestMissingValue = "—";
            ChestMissingValueUnit = string.Empty;
            return;
        }

        var result = ChestComparer.Compare(loot, _chestEntries, _service.Items);
        foreach (var row in result)
        {
            ChestRows.Add(new ChestRow(row, _service.Items));
        }

        ChestDeposited = Format.Silver(result.Sum(r => r.Deposited));
        ChestMissing = Format.Silver(result.Sum(r => r.MissingCount));
        (ChestMissingValue, ChestMissingValueUnit) = Format.Short(result.Sum(r => r.MissingValue));
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
    private bool _startWithWindows;

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
        StartWithWindows = _settings.StartWithWindows;
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

    partial void OnStartWithWindowsChanged(bool value)
    {
        if (_loadingSettings)
        {
            return;
        }

        try
        {
            StartupRegistration.Apply(value);
            SaveSetting(s => s.StartWithWindows = value);
        }
        catch (Exception e)
        {
            Message = e.Message;
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
