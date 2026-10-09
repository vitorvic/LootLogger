using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using LootLogger.App.Localization;
using LootLogger.App.Services;
using LootLogger.Core.Combat;
using LootLogger.Core.Data;

namespace LootLogger.App.ViewModels;

public enum DamageBarKind
{
    Damage,
    Heal,
    Taken
}

/// <summary>One player on the Dano page. Kept between refreshes so the weapon picture does not reload.</summary>
public sealed partial class DamageRow(string name, bool isLocal) : ObservableObject
{
    private int? _weaponIndex;

    public string Name { get; } = name;

    public bool IsLocal { get; } = isLocal;

    /// <summary>Shown in the weapon square until the picture arrives.</summary>
    public string Initial { get; } = name.Length > 0 ? name[..1].ToUpperInvariant() : "?";

    [ObservableProperty]
    private int _rank;

    [ObservableProperty]
    private ImageSource? _weapon;

    [ObservableProperty]
    private string _damage = "0";

    [ObservableProperty]
    private string _dps = "0";

    [ObservableProperty]
    private string _heal = "0";

    [ObservableProperty]
    private string _hps = "0";

    [ObservableProperty]
    private string _taken = "0";

    [ObservableProperty]
    private string _percent = "0%";

    [ObservableProperty]
    private double _barFraction;

    /// <summary>Second, thinner bar for healing; 0 hides it.</summary>
    [ObservableProperty]
    private double _healBarFraction;

    [ObservableProperty]
    private DamageBarKind _barKind;

    /// <summary>The column the list is sorted by, drawn in gold.</summary>
    [ObservableProperty]
    private DamageSort _sortedBy;

    public void Update(CombatantStats stats, int rank, DamageSort sort, DamageBarKind barKind, double bar, double healBar, string percent, ItemDatabase items)
    {
        Rank = rank;
        Damage = Format.Silver(stats.Damage);
        Dps = Format.Silver((long) stats.Dps);
        Heal = Format.Silver(stats.Heal);
        Hps = Format.Silver((long) stats.Hps);
        Taken = Format.Silver(stats.Taken);
        Percent = percent;
        BarFraction = bar;
        HealBarFraction = healBar;
        BarKind = barKind;
        SortedBy = sort;

        if (stats.WeaponIndex != _weaponIndex)
        {
            _weaponIndex = stats.WeaponIndex;
            Weapon = null;
            if (stats.WeaponIndex is { } index && items.Get(index) is { } item)
            {
                _ = LoadWeaponAsync(item.UniqueName, index);
            }
        }
    }

    private async Task LoadWeaponAsync(string itemId, int index)
    {
        var picture = await ItemIcons.GetAsync(itemId);
        if (_weaponIndex == index)
        {
            Weapon = picture;
        }
    }
}

/// <summary>A fight in the "Lutas salvas" list.</summary>
public sealed partial class SavedFightRow(FightSnapshot fight) : ObservableObject
{
    public FightSnapshot Fight { get; } = fight;

    public string Time => Fight.TakenUtc.ToLocalTime().ToString("dd/MM HH:mm");

    public string Title => Fight.Map.Length > 0 ? Fight.Map : Loc.Instance["DamageUnknownMap"];

    public string Total
    {
        get
        {
            var (number, unit) = Format.Short(Fight.TotalDamage);
            return unit.Length > 0 ? $"{number} {unit}" : number;
        }
    }

    [ObservableProperty]
    private bool _isSelected;

    public void Relocalize()
    {
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(Total));
    }
}

/// <summary>"Mais dano: Koreky, 612.400".</summary>
public sealed record HighlightCard(string Title, string Who, string Value, bool IsHeal, bool IsLocal);

public sealed record StatTile(string Title, string Value);

/// <summary>One bar of "Seu dano a cada 10 segundos"; Height is 0 to 1.</summary>
public sealed record TimelineBar(double Height, string Tip);
