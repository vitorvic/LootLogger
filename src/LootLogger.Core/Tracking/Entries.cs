namespace LootLogger.Core.Tracking;

/// <summary>Someone picked up an item from a body or bag.</summary>
/// <param name="IsReservePrice">UnitValue came from the Albion Data Project because the game never priced the item.</param>
public sealed record LootEntry(
    DateTime UtcTime,
    string LootedByName,
    string LootedByGuild,
    string LootedByAlliance,
    int ItemIndex,
    string ItemId,
    string ItemNameEnglish,
    int Quantity,
    string LootedFromName,
    string LootedFromGuild,
    string LootedFromAlliance,
    long UnitValue,
    string Cluster,
    bool IsReservePrice = false)
{
    public long TotalValue => UnitValue * Quantity;
}

/// <summary>A player died.</summary>
public sealed record KillEntry(
    DateTime UtcTime,
    string Died,
    string DiedGuild,
    string DiedAlliance,
    string KilledBy,
    string KilledByGuild,
    string KilledByAlliance,
    string Cluster);

/// <summary>The player running the program, known after they log in or change map.</summary>
public sealed record LocalPlayer(string Name, string Guild, string Alliance);
