using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LootLogger.App.Localization;
using LootLogger.App.Services;
using LootLogger.Core.Combat;

namespace LootLogger.App.ViewModels;

public enum DamageTab
{
    Group,
    Highlights,
    You
}

public enum DamageSort
{
    Damage,
    Dps,
    Name,
    Heal,
    Hps,
    Taken
}

/// <summary>Dano: live damage, healing and damage taken by us and our party, and fights saved earlier.</summary>
public sealed partial class MainViewModel
{
    private const int ShortSummaryCount = 5;
    private static readonly JsonSerializerOptions FightJson = new() { WriteIndented = false };

    private readonly Dictionary<string, DamageRow> _damageRowsByName = new(StringComparer.OrdinalIgnoreCase);
    private bool _savedFightsLoaded;

    public ObservableCollection<DamageRow> DamageRows { get; } = [];

    public ObservableCollection<SavedFightRow> SavedFights { get; } = [];

    public ObservableCollection<HighlightCard> DamageHighlights { get; } = [];

    public ObservableCollection<StatTile> YourDamageTiles { get; } = [];

    public ObservableCollection<TimelineBar> YourTimeline { get; } = [];

    public ObservableCollection<FilterOption<DamageSort>> DamageSortOptions { get; } = [];

    [ObservableProperty]
    private DamageTab _damageTab;

    [ObservableProperty]
    private FilterOption<DamageSort>? _damageSortOption;

    /// <summary>False shows the fight as it happens; true shows the saved fight picked on the right.</summary>
    [ObservableProperty]
    private bool _showSavedFight;

    [ObservableProperty]
    private SavedFightRow? _selectedSavedFight;

    [ObservableProperty]
    private string _damageTotal = "0";

    [ObservableProperty]
    private string _damageTotalUnit = string.Empty;

    [ObservableProperty]
    private string _damageGroupDps = "0";

    [ObservableProperty]
    private string _damageHealTotal = "0";

    [ObservableProperty]
    private string _damageHealUnit = string.Empty;

    [ObservableProperty]
    private string _damageFightTime = "0:00";

    [ObservableProperty]
    private string _damageStatus = string.Empty;

    [ObservableProperty]
    private bool _damageInCombat;

    [ObservableProperty]
    private bool _hasDamage;

    [ObservableProperty]
    private string _damageEmptyText = string.Empty;

    [ObservableProperty]
    private bool _damageResetOnMap;

    [ObservableProperty]
    private bool _damageResetBeforeCombat;

    [ObservableProperty]
    private bool _damageSaveBeforeReset;

    [ObservableProperty]
    private bool _damageCopyShort;

    private DamageSort CurrentDamageSort => DamageSortOption?.Value ?? DamageSort.Damage;

    private void InitDamage()
    {
        var meter = _service.Damage;
        _loadingSettings = true;
        DamageResetOnMap = meter.ResetOnMapChange = _settings.DamageResetOnMap;
        DamageResetBeforeCombat = meter.ResetBeforeCombat = _settings.DamageResetBeforeCombat;
        DamageSaveBeforeReset = meter.SaveBeforeReset = _settings.DamageSaveBeforeReset;
        DamageCopyShort = _settings.DamageCopyShort;
        _loadingSettings = false;
        meter.FightSaved += f => _dispatcher.BeginInvoke(() => OnFightSaved(f));
        FillDamageSortOptions();
    }

    private void FillDamageSortOptions()
    {
        var current = CurrentDamageSort;
        DamageSortOptions.Clear();
        foreach (var sort in Enum.GetValues<DamageSort>())
        {
            DamageSortOptions.Add(new FilterOption<DamageSort>(sort, L["DamageSort" + sort]));
        }

        DamageSortOption = DamageSortOptions.First(o => o.Value == current);
    }

    partial void OnDamageTabChanged(DamageTab value) => RefreshDamage();

    partial void OnDamageSortOptionChanged(FilterOption<DamageSort>? value) => RefreshDamage();

    partial void OnShowSavedFightChanged(bool value)
    {
        if (value && SelectedSavedFight is null && SavedFights.Count > 0)
        {
            SelectedSavedFight = SavedFights[0];
        }

        RefreshDamage();
    }

    partial void OnSelectedSavedFightChanged(SavedFightRow? value)
    {
        if (value is not null)
        {
            ShowSavedFight = true;
        }

        RefreshDamage();
    }

    partial void OnDamageResetOnMapChanged(bool value)
    {
        _service.Damage.ResetOnMapChange = value;
        SaveSetting(s => s.DamageResetOnMap = value);
    }

    partial void OnDamageResetBeforeCombatChanged(bool value)
    {
        _service.Damage.ResetBeforeCombat = value;
        SaveSetting(s => s.DamageResetBeforeCombat = value);
    }

    partial void OnDamageSaveBeforeResetChanged(bool value)
    {
        _service.Damage.SaveBeforeReset = value;
        SaveSetting(s => s.DamageSaveBeforeReset = value);
    }

    partial void OnDamageCopyShortChanged(bool value) => SaveSetting(s => s.DamageCopyShort = value);

    [RelayCommand]
    private void ShowLiveDamage()
    {
        ShowSavedFight = false;
        SelectedSavedFight = null;
    }

    [RelayCommand]
    private void SelectSavedFight(SavedFightRow? row)
    {
        if (row is not null)
        {
            SelectedSavedFight = row;
        }
    }

    [RelayCommand]
    private void ResetDamage()
    {
        _service.Damage.Reset();
        ShowLiveDamage();
        Message = DamageSaveBeforeReset ? L["DamageResetSaved"] : L["DamageResetDone"];
    }

    [RelayCommand]
    private void SaveFight()
    {
        Message = _service.Damage.SaveNow() is null ? L["DamageNothingToSave"] : L["DamageFightSaved"];
    }

    [RelayCommand]
    private void CopyDamage()
    {
        var fight = ShownFight();
        if (fight.Players.Count == 0)
        {
            Message = L["DamageNothingToCopy"];
            return;
        }

        var players = Sorted(fight.Players).ToList();
        if (DamageCopyShort)
        {
            players = players.Take(ShortSummaryCount).ToList();
        }

        var text = new StringBuilder();
        text.AppendLine(L.Format("DamageCopyTitle", FightLabel(fight), Clock(fight.Duration)));
        for (var i = 0; i < players.Count; i++)
        {
            var p = players[i];
            text.AppendLine(L.Format("DamageCopyLine", i + 1, p.Name, Format.Silver(p.Damage), Format.Silver((long) p.Dps),
                Percent(p.Damage, fight.TotalDamage), Format.Silver(p.Heal)));
        }

        try
        {
            Clipboard.SetText(text.ToString().TrimEnd());
            Message = L["DamageCopied"];
        }
        catch (Exception e) when (e is System.Runtime.InteropServices.COMException or InvalidOperationException)
        {
            Message = L["DamageCopyFailed"];
        }
    }

    [RelayCommand]
    private void DeleteSavedFight(SavedFightRow? row)
    {
        if (row is null)
        {
            return;
        }

        if (SelectedSavedFight == row)
        {
            ShowLiveDamage();
        }

        SavedFights.Remove(row);
        WriteSavedFights();
    }

    private void OnFightSaved(FightSnapshot fight)
    {
        SavedFights.Insert(0, new SavedFightRow(fight));
        while (SavedFights.Count > DamageMeter.MaxSavedFights)
        {
            SavedFights.RemoveAt(SavedFights.Count - 1);
        }

        WriteSavedFights();
    }

    private FightSnapshot ShownFight()
    {
        if (!ShowSavedFight)
        {
            return _service.Damage.Snapshot();
        }

        // On "Lutas salvas" with nothing picked, show nothing rather than the live fight.
        return SelectedSavedFight?.Fight ?? new FightSnapshot(DateTime.UtcNow, DateTime.UtcNow, string.Empty, TimeSpan.Zero, []);
    }

    private void RefreshDamage()
    {
        LoadSavedFightsOnce();
        var fight = ShownFight();
        var live = !ShowSavedFight;

        (DamageTotal, DamageTotalUnit) = Format.Short(fight.TotalDamage);
        DamageGroupDps = Format.Silver((long) fight.GroupDps);
        (DamageHealTotal, DamageHealUnit) = Format.Short(fight.TotalHeal);
        DamageFightTime = Clock(fight.Duration);
        DamageInCombat = live && _service.Damage.IsInCombat;
        DamageStatus = SelectedSavedFight is { } picked && !live ? L.Format("DamageSavedAt", picked.Time)
            : !live ? L["DamagePickFight"]
            : DamageInCombat ? L["DamageInCombat"]
            : L["DamageOutOfCombat"];

        HasDamage = fight.Players.Count > 0;
        DamageEmptyText = ShowSavedFight && SelectedSavedFight is null
            ? (SavedFights.Count == 0 ? L["DamageNoSavedFights"] : L["DamagePickFight"])
            : L["DamageEmpty"];

        switch (DamageTab)
        {
            case DamageTab.Group:
                RefreshDamageRows(fight);
                break;
            case DamageTab.Highlights:
                RefreshHighlights(fight);
                break;
            case DamageTab.You:
                RefreshYourStats(fight);
                break;
        }

        foreach (var row in SavedFights)
        {
            row.IsSelected = ShowSavedFight && row == SelectedSavedFight;
        }
    }

    private void RefreshDamageRows(FightSnapshot fight)
    {
        var sort = CurrentDamageSort;
        var players = Sorted(fight.Players).ToList();
        Func<CombatantStats, double> barValue = sort switch
        {
            DamageSort.Dps => p => p.Dps,
            DamageSort.Heal => p => p.Heal,
            DamageSort.Hps => p => p.Hps,
            DamageSort.Taken => p => p.Taken,
            _ => p => p.Damage
        };
        var maxBar = Math.Max(1, players.Count == 0 ? 0 : players.Max(barValue));
        var maxHeal = Math.Max(1, players.Count == 0 ? 0 : players.Max(p => p.Heal));
        var barKind = sort switch
        {
            DamageSort.Heal or DamageSort.Hps => DamageBarKind.Heal,
            DamageSort.Taken => DamageBarKind.Taken,
            _ => DamageBarKind.Damage
        };

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < players.Count; i++)
        {
            var p = players[i];
            seen.Add(p.Name);
            if (!_damageRowsByName.TryGetValue(p.Name, out var row))
            {
                row = new DamageRow(p.Name, p.IsLocal);
                _damageRowsByName[p.Name] = row;
            }

            row.Update(p, i + 1, sort, barKind, barValue(p) / maxBar, sort is DamageSort.Heal or DamageSort.Hps ? 0 : p.Heal / (double) maxHeal,
                Percent(p.Damage, fight.TotalDamage), _service.Items);

            var at = DamageRows.IndexOf(row);
            if (at < 0)
            {
                DamageRows.Insert(i, row);
            }
            else if (at != i)
            {
                DamageRows.Move(at, i);
            }
        }

        for (var i = DamageRows.Count - 1; i >= 0; i--)
        {
            if (!seen.Contains(DamageRows[i].Name))
            {
                _damageRowsByName.Remove(DamageRows[i].Name);
                DamageRows.RemoveAt(i);
            }
        }
    }

    private void RefreshHighlights(FightSnapshot fight)
    {
        DamageHighlights.Clear();
        if (fight.Players.Count == 0)
        {
            return;
        }

        Add("DamageTopDamage", p => p.Damage, p => Format.Silver(p.Damage), false);
        Add("DamageTopDps", p => p.Dps, p => L.Format("PerSecond", Format.Silver((long) p.Dps)), false);
        Add("DamageTopHeal", p => p.Heal, p => Format.Silver(p.Heal), true);
        Add("DamageTopHps", p => p.Hps, p => L.Format("PerSecond", Format.Silver((long) p.Hps)), true);
        Add("DamageTopHit", p => p.MaxHit, p => Format.Silver(p.MaxHit), false);
        Add("DamageTopTaken", p => p.Taken, p => Format.Silver(p.Taken), false);

        void Add(string key, Func<CombatantStats, double> by, Func<CombatantStats, string> text, bool isHeal)
        {
            var best = fight.Players.MaxBy(by)!;
            var has = by(best) > 0;
            DamageHighlights.Add(new HighlightCard(L[key], has ? best.Name : "—", has ? text(best) : "0", isHeal, has && best.IsLocal));
        }
    }

    private void RefreshYourStats(FightSnapshot fight)
    {
        var me = fight.Players.FirstOrDefault(p => p.IsLocal);
        YourDamageTiles.Clear();
        YourTimeline.Clear();
        if (me is null)
        {
            return;
        }

        YourDamageTiles.Add(new StatTile(L["DamageYourDamage"], Format.Silver(me.Damage)));
        YourDamageTiles.Add(new StatTile(L["DamageYourDps"], Format.Silver((long) me.Dps)));
        YourDamageTiles.Add(new StatTile(L["DamageTopHit"], Format.Silver(me.MaxHit)));
        YourDamageTiles.Add(new StatTile(L["DamageYourHeal"], Format.Silver(me.Heal)));
        YourDamageTiles.Add(new StatTile(L["DamageYourHps"], Format.Silver((long) me.Hps)));
        YourDamageTiles.Add(new StatTile(L["DamageYourTaken"], Format.Silver(me.Taken)));

        // The last few minutes; older slots scroll off to the left.
        var slots = me.Timeline.TakeLast(36).ToList();
        var max = Math.Max(1, slots.DefaultIfEmpty(0).Max());
        foreach (var amount in slots)
        {
            YourTimeline.Add(new TimelineBar(Math.Max(0.02, amount / (double) max), Format.Silver(amount)));
        }
    }

    private IEnumerable<CombatantStats> Sorted(IEnumerable<CombatantStats> players) => CurrentDamageSort switch
    {
        DamageSort.Dps => players.OrderByDescending(p => p.Dps),
        DamageSort.Name => players.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase),
        DamageSort.Heal => players.OrderByDescending(p => p.Heal),
        DamageSort.Hps => players.OrderByDescending(p => p.Hps),
        DamageSort.Taken => players.OrderByDescending(p => p.Taken),
        _ => players.OrderByDescending(p => p.Damage)
    };

    private string FightLabel(FightSnapshot fight) => fight.Map.Length > 0 ? fight.Map : L["DamageUnknownMap"];

    private static string Percent(long part, long total) => total <= 0 ? "0%" : $"{Math.Round(part * 100d / total):0}%";

    private static string Clock(TimeSpan time) =>
        time.TotalHours >= 1 ? $"{(int) time.TotalHours}:{time.Minutes:00}:{time.Seconds:00}" : $"{time.Minutes}:{time.Seconds:00}";

    private void RelocalizeDamage()
    {
        FillDamageSortOptions();
        foreach (var row in SavedFights)
        {
            row.Relocalize();
        }

        RefreshDamage();
    }

    // ---------- Saved fights on disk ----------

    private void LoadSavedFightsOnce()
    {
        if (_savedFightsLoaded)
        {
            return;
        }

        _savedFightsLoaded = true;
        try
        {
            if (File.Exists(AppPaths.SavedFightsFile)
                && JsonSerializer.Deserialize<List<FightSnapshot>>(File.ReadAllText(AppPaths.SavedFightsFile), FightJson) is { } fights)
            {
                foreach (var fight in fights.Take(DamageMeter.MaxSavedFights))
                {
                    SavedFights.Add(new SavedFightRow(fight));
                }
            }
        }
        catch (Exception e) when (e is IOException or JsonException or NotSupportedException)
        {
            // A damaged file only loses the old fights.
        }
    }

    private void WriteSavedFights()
    {
        LoadSavedFightsOnce();
        try
        {
            Directory.CreateDirectory(AppPaths.DataFolder);
            File.WriteAllText(AppPaths.SavedFightsFile, JsonSerializer.Serialize(SavedFights.Select(f => f.Fight).ToList(), FightJson));
        }
        catch (IOException)
        {
        }
    }
}
