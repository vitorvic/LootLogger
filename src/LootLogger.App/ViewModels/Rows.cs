using CommunityToolkit.Mvvm.ComponentModel;
using LootLogger.App.Localization;
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

public sealed class LootRow(LootEntry entry, ItemDatabase items) : ObservableObject
{
    public LootEntry Entry { get; } = entry;

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
    public string Tier => items.Get(Entry.ItemIndex)?.TierLabel is { Length: > 0 } t ? t : "—";
    public string ItemName => items.Get(Entry.ItemIndex)?.NameFor(Loc.Instance.Language) ?? Entry.ItemNameEnglish;
    public string Value => Format.Silver(Entry.TotalValue);

    public void Relocalize()
    {
        OnPropertyChanged(nameof(ItemName));
        OnPropertyChanged(nameof(LootedFrom));
        OnPropertyChanged(nameof(Value));
    }
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
