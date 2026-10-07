using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using LootLogger.App.Localization;
using LootLogger.App.Services;
using LootLogger.Core.Chest;
using LootLogger.Core.Data;
using LootLogger.Core.Tracking;

namespace LootLogger.App.ViewModels;

public enum FeedKind
{
    Loot,
    Kill,
    Info
}

/// <summary>One line of "Eventos recentes".</summary>
public sealed record FeedItem(DateTime LocalTime, FeedKind Kind, string Who, string Middle, string Highlight, string Suffix, string Value)
{
    public string Time => LocalTime.ToString("HH:mm:ss");
    public bool IsLoot => Kind == FeedKind.Loot;
    public bool IsKill => Kind == FeedKind.Kill;
    public bool IsInfo => Kind == FeedKind.Info;
}

public sealed class LootRow : ObservableObject
{
    private readonly ItemDatabase _items;
    private ImageSource? _icon;
    private bool _isMine;

    public LootRow(LootEntry entry, ItemDatabase items)
    {
        Entry = entry;
        _items = items;
        if (items.Get(entry.ItemIndex) is { } item)
        {
            _ = LoadIconAsync(item.UniqueName);
        }
    }

    public LootEntry Entry { get; }

    public string Time => Entry.UtcTime.ToLocalTime().ToString("HH:mm:ss");
    public string LootedBy => Entry.LootedByName;
    public string LootedByGuild => Entry.LootedByGuild;
    public string LootedFrom => Entry.LootedFromName switch
    {
        LootTracker.MobName => Loc.Instance["Mob"],
        LootTracker.ChestName => Loc.Instance["Chest"],
        var name => name
    };
    public string LootedFromGuild => Entry.LootedFromGuild;
    public string Map => Entry.Cluster;
    public int Quantity => Entry.Quantity;
    public string Tier => _items.Get(Entry.ItemIndex)?.TierLabel ?? string.Empty;
    public string ItemName => _items.Get(Entry.ItemIndex)?.NameFor(Loc.Instance.Language) ?? Entry.ItemNameEnglish;

    /// <summary>"2x Poção de Gigantismo".</summary>
    public string QuantityAndName => $"{Quantity}x {ItemName}";

    /// <summary>"de NillBlack · PIVAS · Bridgewatch".</summary>
    public string FromLine => Loc.Instance.Format("LootedFromLine",
        string.Join(" · ", new[] { LootedFrom, LootedFromGuild, Map }.Where(t => t.Length > 0)));

    public bool HasValue => Entry.TotalValue > 0;
    public string Value => HasValue ? Format.Compact(Entry.TotalValue) : Loc.Instance["NoPrice"];

    public ImageSource? Icon
    {
        get => _icon;
        private set => SetProperty(ref _icon, value);
    }

    /// <summary>Picked up by the player running the program.</summary>
    public bool IsMine
    {
        get => _isMine;
        set => SetProperty(ref _isMine, value);
    }

    public void Relocalize()
    {
        OnPropertyChanged(nameof(ItemName));
        OnPropertyChanged(nameof(QuantityAndName));
        OnPropertyChanged(nameof(LootedFrom));
        OnPropertyChanged(nameof(FromLine));
        OnPropertyChanged(nameof(Value));
    }

    private async Task LoadIconAsync(string itemId) => Icon = await ItemIcons.GetAsync(itemId);
}

public enum LootFilter
{
    All,
    Mine,
    MyGuild
}

/// <summary>One line of a "who looted most" ranking.</summary>
public sealed class RankRow(int rank, string name, string guild, bool isMine, long amount, string amountText)
{
    public int Rank => rank;
    public string Name => name;
    public string Guild => guild;
    public bool IsMine => isMine;
    public long Amount => amount;
    public string AmountText => amountText;

    /// <summary>Bar length, 0 to 1, compared with first place.</summary>
    public double BarFraction { get; set; }
}

public sealed class KillRow(KillEntry entry) : ObservableObject
{
    private string _lost = Loc.Instance["NothingLost"];
    private long _lostValue;

    public KillEntry Entry { get; } = entry;

    public string Time => Entry.UtcTime.ToLocalTime().ToString("HH:mm:ss");
    public string Died => Entry.Died;
    public string DiedGuild => Entry.DiedGuild;
    public string Killer => Entry.KilledBy;
    public string KillerGuild => Entry.KilledByGuild;
    public string Map => Entry.Cluster;

    public string Lost
    {
        get => _lost;
        set => SetProperty(ref _lost, value);
    }

    public long LostValue
    {
        get => _lostValue;
        set
        {
            if (SetProperty(ref _lostValue, value))
            {
                OnPropertyChanged(nameof(LostValueText));
            }
        }
    }

    public string LostValueText => LostValue > 0 ? Format.Silver(LostValue) : string.Empty;
}

public sealed class ChestRow(PlayerComparison comparison, ItemDatabase items)
{
    public string Player => comparison.Player;
    public string Guild => comparison.Guild;
    public int Looted => comparison.Looted;
    public int Deposited => comparison.Deposited;
    public int Missing => comparison.MissingCount;
    public bool IsOk => comparison.MissingCount == 0;
    public bool IsPartial => comparison.MissingCount > 0 && comparison.Deposited > 0;
    public bool IsPending => comparison.MissingCount > 0 && comparison.Deposited == 0;

    public string Status => IsOk ? Loc.Instance["StatusOk"] : IsPartial ? Loc.Instance["StatusPartial"] : Loc.Instance["StatusPending"];

    public string MissingItems => comparison.Missing.Count == 0
        ? "—"
        : string.Join(", ", comparison.Missing.Select(m =>
        {
            var name = items.GetByUniqueName(m.ItemId)?.NameFor(Loc.Instance.Language) ?? m.ItemName;
            return m.Quantity > 1 ? $"{m.Quantity}× {name}" : name;
        }));
}

public static class Format
{
    /// <summary>Full number with thousands separators in the chosen language: 840.692 or 840,692.</summary>
    public static string Silver(long value) =>
        value.ToString("#,0", Culture);

    /// <summary>Short form for lists: 433,9K, 31,53M or 950.</summary>
    public static string Compact(long value) => value switch
    {
        >= 1_000_000 => (value / 1_000_000d).ToString("0.##", Culture) + "M",
        >= 1_000 => (value / 1_000d).ToString("0.#", Culture) + "K",
        _ => value.ToString(Culture)
    };

    /// <summary>Short form for tiles: 48,2 mi or 48.2M.</summary>
    public static (string Number, string Unit) Short(long value)
    {
        if (value >= 1_000_000)
        {
            return ((value / 1_000_000d).ToString("0.0", Culture), Loc.Instance["Million"]);
        }

        if (value >= 10_000)
        {
            return ((value / 1_000d).ToString("0", Culture), Loc.Instance["Thousand"]);
        }

        return (Silver(value), string.Empty);
    }

    private static System.Globalization.CultureInfo Culture =>
        System.Globalization.CultureInfo.GetCultureInfo(Loc.Instance.Language == "en" ? "en-US" : "pt-BR");
}
