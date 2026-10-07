using System.Globalization;
using LootLogger.Core.Tracking;

namespace LootLogger.Core.Import;

/// <summary>
/// Reads loot files in the 16-column layout LootLogger writes (and other loggers share):
/// ';' separated, header first, one row per pickup or death. Only pickups are returned.
/// </summary>
public static class LootFile
{
    /// <summary>Pickups seen by two loggers within this time are the same pickup (PC clocks differ a little).</summary>
    public static readonly TimeSpan SameEventWindow = TimeSpan.FromSeconds(10);

    public static List<LootEntry> Read(string path) => Parse(File.ReadAllText(path));

    /// <summary>The pickups in the text; empty when it isn't a loot file.</summary>
    public static List<LootEntry> Parse(string text) => ParseAll(text).Loot;

    /// <summary>Pickups and deaths in the text.</summary>
    public static (List<LootEntry> Loot, List<KillEntry> Kills) ParseAll(string text)
    {
        var result = new List<LootEntry>();
        var kills = new List<KillEntry>();
        var lines = text.Split('\n');
        if (lines.Length == 0)
        {
            return (result, kills);
        }

        var header = lines[0].Trim().TrimStart('﻿').Split(';');
        var col = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < header.Length; i++)
        {
            col[header[i].Trim()] = i;
        }

        if (!col.ContainsKey("timestamp_utc") || !col.ContainsKey("looted_by__name") || !col.ContainsKey("item_id"))
        {
            return (result, kills);
        }

        foreach (var rawLine in lines.Skip(1))
        {
            var line = rawLine.TrimEnd('\r');
            if (line.Length == 0)
            {
                continue;
            }

            var values = line.Split(';');
            string Get(string name) => col.TryGetValue(name, out var i) && i < values.Length ? values[i].Trim() : string.Empty;

            var looter = Get("looted_by__name");
            var itemId = Get("item_id");
            if (!DateTime.TryParse(Get("timestamp_utc"), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var time))
            {
                continue;
            }

            if (looter.Length == 0 || itemId.Length == 0)
            {
                // Death rows have no looter.
                var died = Get("died");
                if (died.Length > 0)
                {
                    kills.Add(new KillEntry(time, died, Get("died_player_guild"), string.Empty, Get("killed_by"), Get("killed_by_guild"), string.Empty, Get("cluster")));
                }

                continue;
            }

            _ = int.TryParse(Get("quantity"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var quantity);
            _ = double.TryParse(Get("average_est_market_value"), NumberStyles.Float, CultureInfo.InvariantCulture, out var value);

            result.Add(new LootEntry(
                time,
                looter,
                Get("looted_by__guild"),
                Get("looted_by__alliance"),
                0,
                itemId,
                Get("item_name"),
                Math.Max(1, quantity),
                Get("looted_from__name"),
                Get("looted_from__guild"),
                Get("looted_from__alliance"),
                (long) Math.Round(value),
                Get("cluster")));
        }

        return (result, kills);
    }

    /// <summary>
    /// Joins pickups and deaths of several logs of one fight, lining up PC clocks first,
    /// so each pickup and each death counts once.
    /// </summary>
    public static (List<LootEntry> Loot, List<KillEntry> Kills) MergeAll(
        IEnumerable<(IReadOnlyList<LootEntry> Loot, IReadOnlyList<KillEntry> Kills)> logs)
    {
        var loot = new List<LootEntry>();
        var kills = new List<IReadOnlyList<KillEntry>>();
        foreach (var (logLoot, logKills) in logs)
        {
            var offset = ClockOffset(loot, logLoot);
            var shiftedLoot = logLoot.Select(e => e with { UtcTime = e.UtcTime + offset }).ToList();
            kills.Add(logKills.Select(k => k with { UtcTime = k.UtcTime + offset }).ToList());
            loot = loot.Count == 0 ? shiftedLoot : Merge([loot, shiftedLoot]);
        }

        return (loot, MergeKills(kills));
    }

    /// <summary>Joins the deaths of several logs; one death seen by two loggers counts once.</summary>
    public static List<KillEntry> MergeKills(IEnumerable<IReadOnlyList<KillEntry>> logs)
    {
        var merged = new List<KillEntry>();
        foreach (var log in logs)
        {
            var before = merged.Count;
            foreach (var kill in log)
            {
                var seen = merged.Take(before).Any(k =>
                    string.Equals(k.Died, kill.Died, StringComparison.OrdinalIgnoreCase) && (k.UtcTime - kill.UtcTime).Duration() <= SameEventWindow);
                if (!seen)
                {
                    merged.Add(kill);
                }
            }
        }

        return merged.OrderBy(k => k.UtcTime).ToList();
    }

    /// <summary>
    /// Joins logs of the same fight from several people. A pickup that more than one log saw
    /// (same looter, item, amount and body, close in time) is counted once.
    /// </summary>
    public static List<LootEntry> Merge(IEnumerable<IReadOnlyList<LootEntry>> logs)
    {
        var merged = new List<LootEntry>();
        foreach (var rawLog in logs)
        {
            // Each PC's clock can be off by a minute or more: line this log up with what is merged so far.
            var offset = ClockOffset(merged, rawLog);
            var log = offset == TimeSpan.Zero ? rawLog : rawLog.Select(e => e with { UtcTime = e.UtcTime + offset }).ToList();

            // Each earlier pickup can stand in for one pickup of this log only.
            var used = new HashSet<int>();
            var before = merged.Count;
            foreach (var entry in log)
            {
                var match = -1;
                for (var i = 0; i < before; i++)
                {
                    if (!used.Contains(i) && IsSamePickup(merged[i], entry))
                    {
                        match = i;
                        break;
                    }
                }

                if (match >= 0)
                {
                    used.Add(match);
                }
                else
                {
                    merged.Add(entry);
                }
            }
        }

        return merged.OrderBy(e => e.UtcTime).ToList();
    }

    /// <summary>
    /// How much to add to the times of <paramref name="log"/> so they match <paramref name="merged"/>:
    /// the time gap shared by the most pickups both logs saw (same looter, item, amount and body).
    /// Zero when the logs have nothing in common.
    /// </summary>
    public static TimeSpan ClockOffset(IReadOnlyList<LootEntry> merged, IReadOnlyList<LootEntry> log)
    {
        if (merged.Count == 0 || log.Count == 0)
        {
            return TimeSpan.Zero;
        }

        var byKey = merged.ToLookup(Key);
        var gaps = new List<double>();
        foreach (var entry in log)
        {
            foreach (var other in byKey[Key(entry)])
            {
                gaps.Add((other.UtcTime - entry.UtcTime).TotalSeconds);
            }
        }

        if (gaps.Count == 0)
        {
            return TimeSpan.Zero;
        }

        // The gap most other gaps agree with (within the same-event window).
        gaps.Sort();
        var window = SameEventWindow.TotalSeconds;
        var best = 0.0;
        var bestCount = 0;
        var start = 0;
        for (var end = 0; end < gaps.Count; end++)
        {
            while (gaps[end] - gaps[start] > window)
            {
                start++;
            }

            if (end - start + 1 > bestCount)
            {
                bestCount = end - start + 1;
                best = gaps[(start + end) / 2];
            }
        }

        // A couple of chance matches are not enough to move a whole log.
        return bestCount >= 3 && Math.Abs(best) > window / 2 ? TimeSpan.FromSeconds(best) : TimeSpan.Zero;
    }

    private static (string, string, int, string) Key(LootEntry e) =>
        (e.LootedByName.ToUpperInvariant(), e.ItemId.ToUpperInvariant(), e.Quantity, e.LootedFromName.ToUpperInvariant());

    private static bool IsSamePickup(LootEntry a, LootEntry b) =>
        a.Quantity == b.Quantity
        && string.Equals(a.LootedByName, b.LootedByName, StringComparison.OrdinalIgnoreCase)
        && string.Equals(a.ItemId, b.ItemId, StringComparison.OrdinalIgnoreCase)
        && string.Equals(a.LootedFromName, b.LootedFromName, StringComparison.OrdinalIgnoreCase)
        && (a.UtcTime - b.UtcTime).Duration() <= SameEventWindow;
}
