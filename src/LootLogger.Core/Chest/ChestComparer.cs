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
    // Slash dates are read month first or day first, whichever fits the whole paste (see ReadDates).
    private static readonly string[] MonthFirst = ["M/d/yyyy H:mm:ss", "yyyy-MM-dd H:mm:ss", "d.M.yyyy H:mm:ss"];
    private static readonly string[] DayFirst = ["d/M/yyyy H:mm:ss", "yyyy-MM-dd H:mm:ss", "d.M.yyyy H:mm:ss"];

    /// <summary>
    /// Reads the text copied from the chest log: Date, Player, Item, Enchantment, Quality, Amount,
    /// tab or comma separated, values usually in quotes. Header and unreadable lines are skipped.
    /// Times are taken as UTC, like the game shows them.
    /// </summary>
    /// <param name="now">Current time, to tell 07/10 (October 7) from July 10 when both fit.</param>
    public static List<ChestLogEntry> Parse(string text, DateTime? now = null)
    {
        var rows = new List<List<string>>();
        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.Trim().TrimStart('\uFEFF');
            if (line.Length == 0)
            {
                continue;
            }

            var values = Split(line, line.Contains('\t') ? '\t' : ',');
            if (values.Count == 6 && values[1].Length > 0 && values[2].Length > 0)
            {
                rows.Add(values);
            }
        }

        var formats = PickDateFormats(rows.Select(r => r[0]).ToList(), now ?? DateTime.UtcNow);
        var result = new List<ChestLogEntry>();
        foreach (var values in rows)
        {
            if (!TryDate(values[0], formats, out var time)
                || !int.TryParse(values[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out var enchantment)
                || !int.TryParse(values[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out var quality)
                || !int.TryParse(values[5], NumberStyles.Integer, CultureInfo.InvariantCulture, out var amount))
            {
                continue;
            }

            result.Add(new ChestLogEntry(time, values[1], values[2], enchantment, quality, amount));
        }

        return result;
    }

    /// <summary>
    /// Month first unless the paste only makes sense day first (a "25/10" somewhere), or both make
    /// sense and day first puts the newest line closer to now without going into the future.
    /// </summary>
    private static string[] PickDateFormats(List<string> dates, DateTime now)
    {
        var monthFirst = dates.Select(d => TryDate(d, MonthFirst, out var t) ? t : (DateTime?)null).ToList();
        var dayFirst = dates.Select(d => TryDate(d, DayFirst, out var t) ? t : (DateTime?)null).ToList();
        var monthCount = monthFirst.Count(t => t is not null);
        var dayCount = dayFirst.Count(t => t is not null);
        if (monthCount != dayCount)
        {
            return dayCount > monthCount ? DayFirst : MonthFirst;
        }

        if (monthCount == 0)
        {
            return MonthFirst;
        }

        return Distance(dayFirst.Max()!.Value, now) < Distance(monthFirst.Max()!.Value, now) ? DayFirst : MonthFirst;
    }

    // How far a log's newest line is from now; a date in the future is very unlikely.
    private static TimeSpan Distance(DateTime newest, DateTime now) =>
        newest > now.AddDays(1) ? TimeSpan.MaxValue : (now - newest).Duration();

    private static bool TryDate(string value, string[] formats, out DateTime time) =>
        DateTime.TryParseExact(value, formats, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out time);

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

    /// <summary>
    /// The lines of a new paste that were not pasted before, so the same chest tab pasted twice
    /// does not count deposits twice. Identical lines inside one paste are real repeats and stay;
    /// a tab copied again later only adds what is new.
    /// </summary>
    public static List<ChestLogEntry> NotYetPasted(IEnumerable<ChestLogEntry> existing, IReadOnlyList<ChestLogEntry> pasted)
    {
        var seen = existing.GroupBy(e => e).ToDictionary(g => g.Key, g => g.Count());
        var result = new List<ChestLogEntry>();
        foreach (var entry in pasted)
        {
            if (seen.TryGetValue(entry, out var count) && count > 0)
            {
                seen[entry] = count - 1;
            }
            else
            {
                result.Add(entry);
            }
        }

        return result;
    }
}

public static class ChestComparer
{
    /// <summary>
    /// For each player who looted, counts what they picked up against what they deposited.
    /// Deposits are matched to loot by item id and only count when made after the item was picked up;
    /// withdrawals (negative amounts) are ignored, since only officers can take items out.
    /// Items not deposited that were picked up before the player died are counted as lost on death, not missing.
    /// </summary>
    public static List<PlayerComparison> Compare(
        IEnumerable<LootEntry> loot, IEnumerable<ChestLogEntry> chest, ItemDatabase items, IEnumerable<KillEntry>? kills = null)
    {
        var lootList = loot as IReadOnlyCollection<LootEntry> ?? loot.ToList();
        var lastDeath = (kills ?? []).Concat(DeathsFromBodies(lootList))
            .GroupBy(k => k.Died, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => LatestDeath(g), StringComparer.OrdinalIgnoreCase);

        var deposits = new Dictionary<string, Dictionary<string, List<ChestLogEntry>>>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in chest.Where(c => c.Amount > 0))
        {
            var itemId = items.FindByName(entry.ItemName, entry.Enchantment)?.UniqueName ?? entry.ItemName;
            var byItem = deposits.TryGetValue(entry.Player, out var d) ? d : deposits[entry.Player] = new Dictionary<string, List<ChestLogEntry>>(StringComparer.OrdinalIgnoreCase);
            (byItem.TryGetValue(itemId, out var list) ? list : byItem[itemId] = []).Add(entry);
        }

        var result = new List<PlayerComparison>();
        foreach (var player in lootList.GroupBy(l => l.LootedByName, StringComparer.OrdinalIgnoreCase))
        {
            // Only deposits made after the player first picked the item up count, so the same item
            // deposited before the fight (yesterday, say) does not cover today's loot.
            var remaining = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            if (deposits.TryGetValue(player.Key, out var d))
            {
                foreach (var item in player.GroupBy(l => l.ItemId))
                {
                    var from = item.Min(l => l.UtcTime) - DepositClockSlack;
                    remaining[item.Key] = d.TryGetValue(item.Key, out var list) ? list.Where(e => e.UtcTime >= from).Sum(e => e.Amount) : 0;
                }
            }

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

    /// <summary>
    /// How much earlier than the pickup a deposit may look and still count. Covers PC clocks being off
    /// and a chest log shown in Brazil time (UTC-3) instead of UTC, which is not confirmed yet; a
    /// deposit from the day before is still left out.
    /// </summary>
    public static readonly TimeSpan DepositClockSlack = TimeSpan.FromHours(4);

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
