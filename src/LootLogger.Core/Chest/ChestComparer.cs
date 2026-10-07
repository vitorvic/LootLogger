using System.Globalization;
using System.Text;
using LootLogger.Core.Data;
using LootLogger.Core.Tracking;

namespace LootLogger.Core.Chest;

/// <summary>One line of the guild chest log the game lets you copy.</summary>
public sealed record ChestLogEntry(DateTime UtcTime, string Player, string ItemName, int Enchantment, int Quality, int Amount)
{
    /// <summary>The date exactly as the game wrote it, so a line pasted twice is recognized however the date was read.</summary>
    public string DateText { get; init; } = string.Empty;

    /// <summary>What makes two chest lines the same line: the game's text, not how the date was read.</summary>
    public (string, string, string, int, int, int) Key =>
        (DateText.Length > 0 ? DateText : UtcTime.ToString("O"), Player, ItemName, Enchantment, Quality, Amount);
}

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

            result.Add(new ChestLogEntry(time, values[1], values[2], enchantment, quality, amount) { DateText = values[0] });
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
    /// does not count deposits twice. Lines are compared as the game wrote them (date text, player,
    /// item, enchantment, quality, amount), like the guild site does. Identical lines inside one paste are real repeats and stay;
    /// a tab copied again later only adds what is new.
    /// </summary>
    public static List<ChestLogEntry> NotYetPasted(IEnumerable<ChestLogEntry> existing, IReadOnlyList<ChestLogEntry> pasted)
    {
        var seen = existing.GroupBy(e => e.Key).ToDictionary(g => g.Key, g => g.Count());
        var result = new List<ChestLogEntry>();
        foreach (var entry in pasted)
        {
            if (seen.TryGetValue(entry.Key, out var count) && count > 0)
            {
                seen[entry.Key] = count - 1;
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
    /// Deposits are matched to loot by item id and only stand for what was picked up before them;
    /// withdrawals (negative amounts) are ignored, since only officers can take items out.
    /// Items not deposited that were picked up before the player died in the same fight are counted as lost on death, not missing.
    /// </summary>
    public static List<PlayerComparison> Compare(
        IEnumerable<LootEntry> loot, IEnumerable<ChestLogEntry> chest, ItemDatabase items, IEnumerable<KillEntry>? kills = null)
    {
        var lootList = loot as IReadOnlyCollection<LootEntry> ?? loot.ToList();
        var deaths = (kills ?? []).Concat(DeathsFromBodies(lootList)).ToList();

        // Several fights can be compared at once (a whole week, say): a death only covers what was picked up in its own fight.
        var fightStarts = FightStarts(lootList.Select(l => l.UtcTime).Concat(deaths.Select(k => k.UtcTime)));
        var lastDeath = deaths
            .GroupBy(k => k.Died, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                g => g.Key,
                g => g.GroupBy(k => FightOf(fightStarts, k.UtcTime)).ToDictionary(f => f.Key, f => LatestDeath(f)),
                StringComparer.OrdinalIgnoreCase);

        var deposits = new Dictionary<string, Dictionary<string, List<ChestLogEntry>>>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in chest.Where(c => c.Amount > 0))
        {
            var itemId = items.FindByName(entry.ItemName, entry.Enchantment)?.UniqueName ?? entry.ItemName;
            var byItem = deposits.TryGetValue(entry.Player, out var d) ? d : deposits[entry.Player] = new Dictionary<string, List<ChestLogEntry>>(StringComparer.OrdinalIgnoreCase);
            (byItem.TryGetValue(itemId, out var list) ? list : byItem[itemId] = []).Add(entry);
        }

        var result = new List<PlayerComparison>();
        foreach (var player in lootList.Where(l => !IsTrash(l.ItemId)).GroupBy(l => l.LootedByName, StringComparer.OrdinalIgnoreCase))
        {
            var playerDeposits = deposits.GetValueOrDefault(player.Key);
            var deposited = 0;
            var missing = new List<ItemAmount>();
            var kept = new List<ItemAmount>();
            var lost = new List<ItemAmount>();
            var playerDeaths = lastDeath.GetValueOrDefault(player.Key);
            KillEntry? DeathAfter(LootEntry pickup) =>
                playerDeaths is not null && playerDeaths.TryGetValue(FightOf(fightStarts, pickup.UtcTime), out var d) && pickup.UtcTime <= d.UtcTime ? d : null;
            KillEntry? shownDeath = null;

            foreach (var item in player.GroupBy(l => l.ItemId))
            {
                var itemDeposits = playerDeposits?.GetValueOrDefault(item.Key) ?? [];
                var dropped = item.Where(l => DeathAfter(l) is not null).ToList();
                var carried = item.Where(l => DeathAfter(l) is null).ToList();

                // What was carried when dying was dropped, so deposits go to the rest first: the rest
                // that no deposit covers is missing, and the dropped pickups no deposit covers are lost.
                var matched = Covered(item, itemDeposits);
                var carriedMatched = Covered(carried, itemDeposits);
                var missingHere = carried.Sum(l => l.Quantity) - carriedMatched;
                var lostHere = dropped.Sum(l => l.Quantity) - (matched - carriedMatched);
                deposited += matched;
                var first = item.OrderByDescending(l => l.UnitValue).First();
                if (matched > 0)
                {
                    kept.Add(new ItemAmount(item.Key, first.ItemNameEnglish, matched, first.UnitValue));
                }

                if (lostHere > 0)
                {
                    lost.Add(new ItemAmount(item.Key, first.ItemNameEnglish, lostHere, first.UnitValue));
                    var death = dropped.Select(DeathAfter).OfType<KillEntry>().MaxBy(k => k.UtcTime)!;
                    shownDeath = shownDeath is null || death.UtcTime > shownDeath.UtcTime ? death : shownDeath;
                }

                if (missingHere > 0)
                {
                    missing.Add(new ItemAmount(item.Key, first.ItemNameEnglish, missingHere, first.UnitValue));
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
                shownDeath));
        }

        return result.OrderByDescending(r => r.MissingValue).ThenByDescending(r => r.MissingCount).ThenByDescending(r => r.LostCount).ToList();
    }

    /// <summary>
    /// How many of the pickups the deposits can stand for. A deposit stands for what was picked up before it,
    /// so the same item deposited before the fight, or extra deposited after an earlier fight of the week,
    /// does not cover what was picked up later.
    /// </summary>
    private static int Covered(IEnumerable<LootEntry> pickups, IReadOnlyList<ChestLogEntry> deposits)
    {
        // Latest pickup first: every deposit it can use, all earlier pickups can use too.
        var latestFirst = deposits.OrderByDescending(d => d.UtcTime).ToList();
        var next = 0;
        var available = 0;
        var covered = 0;
        foreach (var pickup in pickups.OrderByDescending(l => l.UtcTime))
        {
            while (next < latestFirst.Count && latestFirst[next].UtcTime >= pickup.UtcTime - DepositClockSlack)
            {
                available += latestFirst[next++].Amount;
            }

            var take = Math.Min(pickup.Quantity, available);
            available -= take;
            covered += take;
        }

        return covered;
    }

    /// <summary>
    /// How much earlier than the pickup a deposit may look and still count. The chest log is in UTC
    /// (checked against a real deposit on 2026-10-07), but the loot log comes from players' PC clocks,
    /// which can be a few minutes off.
    /// </summary>
    public static readonly TimeSpan DepositClockSlack = TimeSpan.FromMinutes(30);

    private static readonly TimeSpan DeathGap = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Broken leftovers of destroyed gear: nobody deposits them. The app does not record them,
    /// but files from other loggers can have them.
    /// </summary>
    private static bool IsTrash(string itemId) => itemId.EndsWith("_TRASH", StringComparison.OrdinalIgnoreCase);

    /// <summary>A pause this long with no pickup or death ends a fight. In the real logs we have, a fight pauses for 13 minutes at most.</summary>
    private static readonly TimeSpan FightGap = TimeSpan.FromHours(1);

    /// <summary>When each fight starts, in order: events less than <see cref="FightGap"/> apart are one fight.</summary>
    private static List<DateTime> FightStarts(IEnumerable<DateTime> times)
    {
        var starts = new List<DateTime>();
        DateTime? last = null;
        foreach (var time in times.Order())
        {
            if (last is null || time - last.Value > FightGap)
            {
                starts.Add(time);
            }

            last = time;
        }

        return starts;
    }

    private static int FightOf(List<DateTime> starts, DateTime time)
    {
        var i = starts.BinarySearch(time);
        return i >= 0 ? i : ~i - 1;
    }

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
