using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LootLogger.App.Localization;
using LootLogger.Core.Chest;
using LootLogger.Core.Data;
using LootLogger.Core.Import;
using LootLogger.Core.Tracking;

namespace LootLogger.App.ViewModels;

/// <summary>Comparar Baú: loot logs on the left, chest log on top, one card per player.</summary>
public sealed partial class MainViewModel
{
    private readonly List<ChestLogEntry> _chestEntries = [];
    private readonly List<string> _pickedFiles = [];
    private List<PlayerComparison> _chestResult = [];
    private bool _chestDirty;

    public ObservableCollection<LootSourceRow> LootSources { get; } = [];

    public ObservableCollection<ChestCard> ChestCards { get; } = [];

    public ObservableCollection<string> ChestGuilds { get; } = [];

    public ObservableCollection<FilterOption<int>> ChestTiers { get; } = [];

    public ObservableCollection<FilterOption<ItemKind>> ChestKinds { get; } = [];

    [ObservableProperty]
    private string _chestMessage = string.Empty;

    /// <summary>"2 logs juntos" when more than one log is ticked.</summary>
    [ObservableProperty]
    private string _chestMergedText = string.Empty;

    [ObservableProperty]
    private string _chestLooted = "0";

    [ObservableProperty]
    private string _chestKept = "—";

    [ObservableProperty]
    private string _chestMissing = "—";

    [ObservableProperty]
    private string _chestMissingValue = string.Empty;

    [ObservableProperty]
    private string _chestRate = "—";

    /// <summary>Items players were carrying when they died.</summary>
    [ObservableProperty]
    private string _chestLost = "0";

    [ObservableProperty]
    private double _chestRateFraction;

    [ObservableProperty]
    private string _chestPlayerSearch = string.Empty;

    [ObservableProperty]
    private int _chestGuildIndex;

    [ObservableProperty]
    private bool _onlyDebtors;

    public bool HasChestLog => _chestEntries.Count > 0;

    public bool HasChestCards => ChestCards.Count > 0;

    public string ChestEmptyText => HasChestLog ? L["ChestNoCards"] : L["ChestNoLoot"];

    private void InitChest()
    {
        foreach (var tier in new[] { 4, 5, 6, 7, 8 })
        {
            var option = new FilterOption<int>(tier, "T" + tier);
            option.PropertyChanged += (_, _) => ApplyChestFilter();
            ChestTiers.Add(option);
        }

        foreach (var kind in Enum.GetValues<ItemKind>())
        {
            var option = new FilterOption<ItemKind>(kind, L["Kind" + kind]);
            option.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(FilterOption<ItemKind>.IsOn))
                {
                    ApplyChestFilter();
                }
            };
            ChestKinds.Add(option);
        }

        var live = new LootSourceRow(null, L["LiveSession"]) { IsChecked = true };
        live.PropertyChanged += OnLootSourceChanged;
        LootSources.Add(live);
        ChestGuilds.Add(L["AllGuilds"]);
    }

    partial void OnChestPlayerSearchChanged(string value) => ApplyChestFilter();

    partial void OnChestGuildIndexChanged(int value) => ApplyChestFilter();

    partial void OnOnlyDebtorsChanged(bool value) => ApplyChestFilter();

    partial void OnPageChanged(Page value)
    {
        if (value == Page.Chest)
        {
            RefreshLootSources();
        }
    }

    // ---------- Loot logs ----------

    [RelayCommand]
    private void PickLootFiles()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "Loot (*.csv)|*.csv|*.*|*.*",
            Multiselect = true,
            InitialDirectory = EnsureFolder(_settings.ExportFolder)
        };
        if (dialog.ShowDialog() == true)
        {
            AddFiles(dialog.FileNames);
        }
    }

    /// <summary>Files picked or dropped: loot logs go to the list, chest logs to the comparison.</summary>
    public void AddFiles(IEnumerable<string> paths)
    {
        var chestText = new List<string>();
        var lootFiles = 0;
        foreach (var path in paths)
        {
            string text;
            try
            {
                text = File.ReadAllText(path);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            if (LootFile.Parse(text).Count > 0)
            {
                if (!_pickedFiles.Contains(path, StringComparer.OrdinalIgnoreCase))
                {
                    _pickedFiles.Add(path);
                }

                var row = LootSources.FirstOrDefault(s => string.Equals(s.Path, path, StringComparison.OrdinalIgnoreCase));
                if (row is not null)
                {
                    row.IsChecked = true;
                }

                _checkOnNextRefresh.Add(path);
                lootFiles++;
            }
            else
            {
                chestText.Add(text);
            }
        }

        if (lootFiles > 0)
        {
            RefreshLootSources();
        }

        if (chestText.Count > 0)
        {
            AddChestText(string.Join('\n', chestText));
        }
        else if (lootFiles == 0)
        {
            ChestMessage = L["NothingRead"];
        }
    }

    private readonly HashSet<string> _checkOnNextRefresh = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Lists the files picked or dropped by hand, newest first.</summary>
    private void RefreshLootSources()
    {
        var files = _pickedFiles.AsEnumerable().Reverse().Where(File.Exists).Select(p => new FileInfo(p)).ToList();

        // The live session's own file is the same loot as "Sessão atual".
        var ownFile = _session.AutosavePath is { } own ? Path.GetFullPath(own) : null;
        files.RemoveAll(f => string.Equals(f.FullName, ownFile, StringComparison.OrdinalIgnoreCase));

        var keep = new List<LootSourceRow> { LootSources[0] };
        foreach (var file in files)
        {
            var row = LootSources.FirstOrDefault(s => string.Equals(s.Path, file.FullName, StringComparison.OrdinalIgnoreCase));
            if (row is null)
            {
                row = new LootSourceRow(file.FullName, Path.GetFileNameWithoutExtension(file.Name));
                row.PropertyChanged += OnLootSourceChanged;
            }

            if (row.ReadAt < file.LastWriteTimeUtc)
            {
                try
                {
                    (row.Loot, row.Kills) = LootFile.ParseAll(File.ReadAllText(file.FullName));
                    row.ReadAt = file.LastWriteTimeUtc;
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    continue;
                }
            }

            if (row.Loot.Count == 0)
            {
                // Not a loot log (an export of something else).
                continue;
            }

            if (_checkOnNextRefresh.Remove(file.FullName))
            {
                row.IsChecked = true;
            }

            row.Detail = L.Format("SourceDetail", Format.Silver(row.Loot.Sum(l => l.Quantity)), file.LastWriteTime.ToString("dd/MM HH:mm"));
            keep.Add(row);
        }

        foreach (var gone in LootSources.Except(keep).ToList())
        {
            gone.PropertyChanged -= OnLootSourceChanged;
        }

        LootSources.Clear();
        foreach (var row in keep)
        {
            LootSources.Add(row);
        }

        RecomputeChest();
    }

    private void OnLootSourceChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(LootSourceRow.IsChecked))
        {
            RecomputeChest();
        }
    }

    // ---------- Chest log ----------

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

    // ---------- Comparison ----------

    private void RecomputeChest()
    {
        var live = LootSources.FirstOrDefault(s => s.IsLive);
        if (live is not null)
        {
            live.Title = L["LiveSession"];
            live.Detail = L.Format("LiveDetail", Format.Silver(_session.TotalItems));
        }

        var checkedSources = LootSources.Where(s => s.IsChecked).ToList();
        var logs = checkedSources.Select(s => s.IsLive ? _session.Loot : s.Loot).ToList();
        var loot = logs.Count == 1 ? logs[0] : LootFile.Merge(logs);
        var kills = LootFile.MergeKills(checkedSources.Select(s => s.IsLive ? _session.Kills : s.Kills));
        ChestMergedText = logs.Count > 1 ? L.Format("LogsMerged", logs.Count) : string.Empty;

        // Without a chest log everything counts as missing, which the cards show as "Pegou".
        _chestResult = ChestComparer.Compare(loot, _chestEntries, _service.Items, kills);
        OnPropertyChanged(nameof(HasChestLog));

        var guilds = _chestResult.Select(r => r.Guild).Where(g => g.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => g, StringComparer.OrdinalIgnoreCase).ToList();
        var selected = ChestGuildIndex > 0 && ChestGuildIndex < ChestGuilds.Count ? ChestGuilds[ChestGuildIndex] : null;
        if (!ChestGuilds.Skip(1).SequenceEqual(guilds) || ChestGuilds.FirstOrDefault() != L["AllGuilds"])
        {
            ChestGuilds.Clear();
            ChestGuilds.Add(L["AllGuilds"]);
            foreach (var guild in guilds)
            {
                ChestGuilds.Add(guild);
            }

            ChestGuildIndex = selected is null ? 0 : Math.Max(0, ChestGuilds.IndexOf(selected));
        }

        foreach (var kind in ChestKinds)
        {
            kind.Label = L["Kind" + kind.Value];
        }

        ApplyChestFilter();
    }

    /// <summary>Builds the player cards and tiles from the last comparison and the filters.</summary>
    private void ApplyChestFilter()
    {
        bool Shown(ItemAmount item)
        {
            var tier = ItemKinds.Tier(item.ItemId);
            var tierOn = tier == 0 || ChestTiers.Any(t => t.IsOn && (t.Value == tier || (t.Value == 4 && tier < 4) || (t.Value == 8 && tier > 8)));
            return tierOn && ChestKinds.Any(k => k.IsOn && k.Value == ItemKinds.Of(item.ItemId));
        }

        var guild = ChestGuildIndex > 0 && ChestGuildIndex < ChestGuilds.Count ? ChestGuilds[ChestGuildIndex] : null;
        var search = ChestPlayerSearch.Trim();

        var cards = new List<ChestCard>();
        foreach (var player in _chestResult)
        {
            if ((guild is not null && !string.Equals(player.Guild, guild, StringComparison.OrdinalIgnoreCase))
                || (search.Length > 0 && !player.Player.Contains(search, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            var missing = player.Missing.Where(Shown).Select(m => new ItemTile(m, _service.Items)).ToList();
            var deposited = player.Kept.Where(Shown).Select(m => new ItemTile(m, _service.Items)).ToList();
            var lost = player.LostOnDeath.Where(Shown).Select(m => new ItemTile(m, _service.Items)).ToList();
            var deathText = player.Death is { } d
                ? L.Format(d.KilledBy.Length > 0 ? "DiedLine" : "DiedLineNoKiller",
                    d.UtcTime.ToString("dd/MM HH:mm"), d.KilledByGuild.Length > 0 ? $"{d.KilledBy} ({d.KilledByGuild})" : d.KilledBy)
                : string.Empty;
            var card = HasChestLog
                ? new ChestCard(player.Player, player.Guild, missing, deposited, [], lost, deathText, compared: true)
                : new ChestCard(player.Player, player.Guild, [], [], missing, lost, deathText, compared: false);
            if (card.Looted == 0 || (HasChestLog && OnlyDebtors && card.IsOk))
            {
                continue;
            }

            cards.Add(card);
        }

        ChestCards.Clear();
        foreach (var card in cards)
        {
            ChestCards.Add(card);
        }

        OnPropertyChanged(nameof(HasChestCards));
        OnPropertyChanged(nameof(ChestEmptyText));

        ChestLost = Format.Silver(cards.Sum(c => c.LostCount));
        if (!HasChestLog)
        {
            ChestLooted = Format.Silver(cards.Sum(c => c.Looted));
            ChestKept = "—";
            ChestMissing = "—";
            ChestMissingValue = string.Empty;
            ChestRate = "—";
            ChestRateFraction = 0;
            return;
        }

        var looted = cards.Sum(c => c.Looted);
        var kept = cards.Sum(c => c.KeptCount);
        ChestLooted = Format.Silver(looted);
        ChestKept = Format.Silver(kept);
        ChestMissing = Format.Silver(cards.Sum(c => c.MissingCount));
        var missingValue = cards.Sum(c => c.MissingValue);
        ChestMissingValue = missingValue > 0 ? L.Format("MissingValueLine", Format.Compact(missingValue)) : string.Empty;
        ChestRateFraction = looted == 0 ? 0 : (double) kept / looted;
        ChestRate = looted == 0 ? "—" : Math.Round(ChestRateFraction * 100).ToString("0") + "%";
    }
}
