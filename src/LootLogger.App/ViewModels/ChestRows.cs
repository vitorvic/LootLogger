using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using LootLogger.App.Localization;
using LootLogger.App.Services;
using LootLogger.Core.Chest;
using LootLogger.Core.Data;
using LootLogger.Core.Tracking;

namespace LootLogger.App.ViewModels;

/// <summary>One loot log in the Comparar Baú list: the live session or a CSV file.</summary>
public sealed partial class LootSourceRow : ObservableObject
{
    public LootSourceRow(string? path, string title)
    {
        Path = path;
        Title = title;
    }

    /// <summary>Null for the live session.</summary>
    public string? Path { get; }

    public bool IsLive => Path is null;

    [ObservableProperty]
    private string _title;

    [ObservableProperty]
    private string _detail = string.Empty;

    [ObservableProperty]
    private bool _isChecked;

    /// <summary>The pickups read from the file.</summary>
    public IReadOnlyList<LootEntry> Loot { get; set; } = [];

    /// <summary>When the file was read, to read it again after it changes.</summary>
    public DateTime ReadAt { get; set; }
}

/// <summary>An on/off filter chip like "T6" or "Bolsa".</summary>
public sealed partial class FilterOption<T>(T value, string label) : ObservableObject
{
    public T Value { get; } = value;

    [ObservableProperty]
    private string _label = label;

    [ObservableProperty]
    private bool _isOn = true;
}

/// <summary>An item picture with tier and amount, as in the game's inventory.</summary>
public sealed class ItemTile : ObservableObject
{
    private ImageSource? _icon;

    public ItemTile(ItemAmount item, ItemDatabase items)
    {
        Item = item;
        Name = items.GetByUniqueName(item.ItemId)?.NameFor(Loc.Instance.Language) ?? item.ItemName;
        _ = LoadIconAsync();
    }

    public ItemAmount Item { get; }

    public string Name { get; }
    public string Tier => ItemKinds.TierLabel(Item.ItemId);
    public bool HasTier => Tier.Length > 0;
    public int Quantity => Item.Quantity;
    public bool ShowQuantity => Item.Quantity > 1;

    /// <summary>"2x Elmo de Guardião · 134,6K".</summary>
    public string ToolTip => Item.TotalValue > 0
        ? $"{Item.Quantity}x {Name} · {Format.Compact(Item.TotalValue)}"
        : $"{Item.Quantity}x {Name}";

    public ImageSource? Icon
    {
        get => _icon;
        private set => SetProperty(ref _icon, value);
    }

    private async Task LoadIconAsync() => Icon = await ItemIcons.GetAsync(Item.ItemId);
}

/// <summary>
/// One player in Comparar Baú: what they still owe and what they already put in the chest.
/// Before a chest log is pasted it only lists what they picked up.
/// </summary>
public sealed partial class ChestCard(string player, string guild, IReadOnlyList<ItemTile> missing, IReadOnlyList<ItemTile> kept, IReadOnlyList<ItemTile> picked)
    : ObservableObject
{
    /// <summary>Closed cards show one row of items per group.</summary>
    [ObservableProperty]
    private bool _isExpanded;

    public string Player => player;
    public string Guild => guild;
    public bool HasGuild => guild.Length > 0;
    public string Initial => player.Length > 0 ? char.ToUpperInvariant(player[0]).ToString() : "?";

    public IReadOnlyList<ItemTile> Missing => missing;
    public IReadOnlyList<ItemTile> Kept => kept;
    public IReadOnlyList<ItemTile> Picked => picked;

    public int MissingCount => missing.Sum(t => t.Quantity);
    public int KeptCount => kept.Sum(t => t.Quantity);
    public int PickedCount => picked.Sum(t => t.Quantity);
    public int Looted => MissingCount + KeptCount + PickedCount;
    public long MissingValue => missing.Sum(t => t.Item.TotalValue);
    public long PickedValue => picked.Sum(t => t.Item.TotalValue);

    public bool HasMissing => missing.Count > 0;
    public bool HasKept => kept.Count > 0;
    public bool HasPicked => picked.Count > 0;

    /// <summary>Compared with a chest log (otherwise only the pickups are known).</summary>
    public bool IsCompared => !HasPicked;

    public bool IsOk => IsCompared && MissingCount == 0;
    public bool IsNone => KeptCount == 0 && MissingCount > 0;
    public bool IsPartial => KeptCount > 0 && MissingCount > 0;

    /// <summary>"3/5": items in the chest out of items picked up; "7 itens" before comparing.</summary>
    public string CountText => IsCompared ? $"{KeptCount}/{Looted}" : Loc.Instance.Format("ItemsCount", PickedCount);
    public double KeptFraction => Looted == 0 ? 0 : (double) KeptCount / Looted;

    public string MissingValueText => MissingValue > 0 ? "≈ " + Format.Compact(MissingValue) : string.Empty;
    public string PickedValueText => PickedValue > 0 ? "≈ " + Format.Compact(PickedValue) : string.Empty;
}
