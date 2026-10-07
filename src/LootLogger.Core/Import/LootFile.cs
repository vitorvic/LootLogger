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
    public static List<LootEntry> Parse(string text)
    {
        var result = new List<LootEntry>();
        var lines = text.Split('\n');
        if (lines.Length == 0)
        {
            return result;
        }

        var header = lines[0].Trim().TrimStart('﻿').Split(';');
        var col = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < header.Length; i++)
        {
            col[header[i].Trim()] = i;
        }

        if (!col.ContainsKey("timestamp_utc") || !col.ContainsKey("looted_by__name") || !col.ContainsKey("item_id"))
        {
            return result;
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
            if (looter.Length == 0 || itemId.Length == 0
                || !DateTime.TryParse(Get("timestamp_utc"), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var time))
            {
                // Death rows have no looter.
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

        return result;
    }

    /// <summary>
    /// Joins logs of the same fight from several people. A pickup that more than one log saw
    /// (same looter, item, amount and body, close in time) is counted once.
    /// </summary>
    public static List<LootEntry> Merge(IEnumerable<IReadOnlyList<LootEntry>> logs)
    {
        var merged = new List<LootEntry>();
        foreach (var log in logs)
        {
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

    private static bool IsSamePickup(LootEntry a, LootEntry b) =>
        a.Quantity == b.Quantity
        && string.Equals(a.LootedByName, b.LootedByName, StringComparison.OrdinalIgnoreCase)
        && string.Equals(a.ItemId, b.ItemId, StringComparison.OrdinalIgnoreCase)
        && string.Equals(a.LootedFromName, b.LootedFromName, StringComparison.OrdinalIgnoreCase)
        && (a.UtcTime - b.UtcTime).Duration() <= SameEventWindow;
}
