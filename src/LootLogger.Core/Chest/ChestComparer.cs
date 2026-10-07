using System.Globalization;
using System.Text;
using LootLogger.Core.Data;
using LootLogger.Core.Tracking;

namespace LootLogger.Core.Chest;

/// <summary>One line of the guild chest log the game lets you copy.</summary>
public sealed record ChestLogEntry(DateTime UtcTime, string Player, string ItemName, int Enchantment, int Quality, int Amount);

/// <summary>Some amount of one item, with its estimated price each.</summary>
public sealed record ItemAmount(string ItemId, string ItemName, int Quantity, long UnitValue)
{
    public long TotalValue => Quantity * UnitValue;
}

public sealed record PlayerComparison(
    string Player,
    string Guild,
    int Looted,
    int Deposited,
    IReadOnlyList<ItemAmount> Missing,
    IReadOnlyList<ItemAmount> Kept,
    IReadOnlyList<ItemAmount> LostOnDeath,
    KillEntry? Death)
{
    public int LostCount => LostOnDeath.Sum(m => m.Quantity);
    public int MissingCount => Missing.Sum(m => m.Quantity);
    public long MissingValue => Missing.Sum(m => m.Quantity * m.UnitValue);
}

public static class ChestLogParser
{
    private static readonly string[] DateFormats =
    [
        "MM/dd/yyyy HH:mm:ss",
        "dd/MM/yyyy HH:mm:ss",
        "yyyy-MM-dd HH:mm:ss",
        "dd.MM.yyyy HH:mm:ss"
    ];

    /// <summary>
    /// Reads the text copied from the chest log: Date, Player, Item, Enchantment, Quality, Amount,
    /// tab or comma separated, values usually in quotes. Header and unreadable lines are skipped.
    /// </summary>
    public static List<ChestLogEntry> Parse(string text)
    {
        var result = new List<ChestLogEntry>();
        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.Trim().TrimStart('﻿');
            if (line.Length == 0)
            {
                continue;
            }

            var values = Split(line, line.Contains('\t') ? '\t' : ',');
            if (values.Count != 6
                || !DateTime.TryParseExact(values[0], DateFormats, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var time)
                || !int.TryParse(values[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out var enchantment)
                || !int.TryParse(values[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out var quality)
                || !int.TryParse(values[5], NumberStyles.Integer, CultureInfo.InvariantCulture, out var amount)
                || values[1].Length == 0
                || values[2].Length == 0)
            {
                continue;
            }

            result.Add(new ChestLogEntry(time, values[1], values[2], enchantment, quality, amount));
        }

        return result;
    }

    private static List<string> Split(string line, char delimiter)
    {
        var fields = new List<string>();
        var current = new StringBuilder();
        var inQuotes = false;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (c == '"')
            {
                if (inQuotes && i + 1 < line.Length && line[i + 1] == '"')
                {
                    current.Append('"');
                    i++;
                }
                else
                {
                    inQuotes = !inQuotes;
                }
            }
            else if (c == delimiter && !inQuotes)
            {
                fields.Add(current.ToString().Trim());
                current.Clear();
            }
            else
            {
                current.Append(c);
            }
        }

        fields.Add(current.ToString().Trim());
        return fields;
    }
}

public static class ChestComparer
{
    /// <summary>
    /// For each player who looted, counts what they picked up against what they deposited.
    /// Deposits are matched to loot by item id; withdrawals (negative amounts) are ignored.
    /// Items not deposited that were picked up before the player died are counted as lost on death, not missing.
    /// </summary>
    public static List<PlayerComparison> Compare(
        IEnumerable<LootEntry> loot, IEnumerable<ChestLogEntry> chest, ItemDatabase items, IEnumerable<KillEntry>? kills = null)
    {
        var lootList = loot as IReadOnlyCollection<LootEntry> ?? loot.ToList();
        var lastDeath = (kills ?? []).Concat(DeathsFromBodies(lootList))
            .GroupBy(k => k.Died, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => LatestDeath(g), StringComparer.OrdinalIgnoreCase);

        var deposits = new Dictionary<string, Dictionary<string, int>>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in chest.Where(c => c.Amount > 0))
        {
            var itemId = items.FindByName(entry.ItemName, entry.Enchantment)?.UniqueName ?? entry.ItemName;
            var byItem = deposits.TryGetValue(entry.Player, out var d) ? d : deposits[entry.Player] = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            byItem[itemId] = byItem.GetValueOrDefault(itemId) + entry.Amount;
        }

        var result = new List<PlayerComparison>();
        foreach (var player in lootList.GroupBy(l => l.LootedByName, StringComparer.OrdinalIgnoreCase))
        {
            var remaining = deposits.TryGetValue(player.Key, out var d) ? new Dictionary<string, int>(d, StringComparer.OrdinalIgnoreCase) : [];
            var deposited = 0;
            var missing = new List<ItemAmount>();
            var kept = new List<ItemAmount>();
            var lost = new List<ItemAmount>();
            var death = lastDeath.GetValueOrDefault(player.Key);

            foreach (var item in player.GroupBy(l => l.ItemId))
            {
                var looted = item.Sum(l => l.Quantity);
                var available = remaining.GetValueOrDefault(item.Key);
                var matched = Math.Min(looted, available);
                remaining[item.Key] = available - matched;
                deposited += matched;
                var first = item.OrderByDescending(l => l.UnitValue).First();
                if (matched > 0)
                {
                    kept.Add(new ItemAmount(item.Key, first.ItemNameEnglish, matched, first.UnitValue));
                }

                // Deposits go to the pickups after the death first: what was carried when dying was dropped.
                var notDeposited = looted - matched;
                var beforeDeath = death is null ? 0 : item.Where(l => l.UtcTime <= death.UtcTime).Sum(l => l.Quantity);
                var lostHere = Math.Min(notDeposited, beforeDeath);
                if (lostHere > 0)
                {
                    lost.Add(new ItemAmount(item.Key, first.ItemNameEnglish, lostHere, first.UnitValue));
                }

                if (notDeposited > lostHere)
                {
                    missing.Add(new ItemAmount(item.Key, first.ItemNameEnglish, notDeposited - lostHere, first.UnitValue));
                }
            }

            result.Add(new PlayerComparison(
                player.Key,
                player.Select(l => l.LootedByGuild).FirstOrDefault(g => g.Length > 0) ?? string.Empty,
                player.Sum(l => l.Quantity),
                deposited,
                missing.OrderByDescending(m => m.TotalValue).ToList(),
                kept.OrderByDescending(m => m.TotalValue).ToList(),
                lost.OrderByDescending(m => m.TotalValue).ToList(),
                lost.Count > 0 ? death : null));
        }

        return result.OrderByDescending(r => r.MissingValue).ThenByDescending(r => r.MissingCount).ThenByDescending(r => r.LostCount).ToList();
    }

    private static readonly TimeSpan DeathGap = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Logs without death rows still show deaths: someone looting a player's body means that player died.
    /// Each run of body loot is one death, placed at its first pickup.
    /// </summary>
    private static IEnumerable<KillEntry> DeathsFromBodies(IEnumerable<LootEntry> loot)
    {
        foreach (var body in loot
                     .Where(l => l.LootedFromName.Length > 0 && l.LootedFromName != LootTracker.MobName && l.LootedFromName != LootTracker.ChestName)
                     .GroupBy(l => l.LootedFromName, StringComparer.OrdinalIgnoreCase))
        {
            DateTime? last = null;
            foreach (var entry in body.OrderBy(l => l.UtcTime))
            {
                if (last is null || entry.UtcTime - last.Value > DeathGap)
                {
                    yield return new KillEntry(entry.UtcTime, body.Key, entry.LootedFromGuild, entry.LootedFromAlliance, string.Empty, string.Empty, string.Empty, entry.Cluster);
                }

                last = entry.UtcTime;
            }
        }
    }

    /// <summary>The last death; a real death row (with the killer) wins over one guessed from body loot.</summary>
    private static KillEntry LatestDeath(IEnumerable<KillEntry> deaths)
    {
        var latest = deaths.MaxBy(k => k.UtcTime)!;
        if (latest.KilledBy.Length > 0)
        {
            return latest;
        }

        return deaths.Where(k => k.KilledBy.Length > 0 && (latest.UtcTime - k.UtcTime).Duration() <= DeathGap)
            .MaxBy(k => k.UtcTime) ?? latest;
    }
}
