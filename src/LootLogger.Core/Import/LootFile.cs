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

    // Resends: when a PC's connection stutters, the game sends the same message again, waiting twice as long
    // each time (0.2 s, then 0.4, 0.8, 1.6, 3.2 s), and some loggers write every resend down as another pickup.
    // Checked on the killboard (30/09 00:07 UTC): ByBlex died with 1 shield and one logger had 6.

    /// <summary>Shortest and longest first wait before a resend (it follows each PC's ping).</summary>
    private static readonly TimeSpan MinResendWait = TimeSpan.FromMilliseconds(120), MaxResendWait = TimeSpan.FromMilliseconds(700);

    /// <summary>
    /// A repeat between <see cref="MinResendWait"/> and this long after may be a resend or two equal pickups taken together.
    /// Longer than the first wait because resends sometimes drift off the beat (29/09: copies 0.76 s and 1.04 s later).
    /// </summary>
    private static readonly TimeSpan QuickRepeat = TimeSpan.FromSeconds(1.5);

    /// <summary>How far the resends of one message go (6 resends at the longest wait, with some slack).</summary>
    private static readonly TimeSpan ResendHorizon = MaxResendWait * 63 * 1.1;

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
        var all = logs.ToList();
        var (loot, offsets) = MergeLinedUp(all.Select(l => l.Loot));
        var kills = all.Select((l, i) => (IReadOnlyList<KillEntry>) l.Kills.Select(k => k with { UtcTime = k.UtcTime + offsets[i] }).ToList());
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
    /// (same looter, item, amount and body, close in time) is counted once. Game resends are taken out first.
    /// </summary>
    public static List<LootEntry> Merge(IEnumerable<IReadOnlyList<LootEntry>> logs) => MergeLinedUp(logs).Loot;

    /// <summary>
    /// Takes out the copies of a pickup that fall on the resend beat (wait w, then 3w, 7w, 15w... after it).
    /// It needs at least two copies on the beat with no other copy in between: a single repeat, or copies
    /// at any other pace, may be real equal pickups.
    /// </summary>
    public static List<LootEntry> RemoveResends(IReadOnlyList<LootEntry> log)
    {
        var resent = new HashSet<int>();
        foreach (var group in Enumerable.Range(0, log.Count).GroupBy(i => Key(log[i])))
        {
            var rest = group.OrderBy(i => log[i].UtcTime).ToList();
            while (rest.Count >= 3)
            {
                var start = log[rest[0]].UtcTime;
                var near = rest.Skip(1).Where(i => log[i].UtcTime - start <= ResendHorizon).ToList();
                var best = new List<int>();
                if (near.Count >= 2)
                {
                    // The wait comes from one of the later copies, which can be the 1st or the 2nd resend (the 1st is sometimes lost).
                    foreach (var basis in near)
                    {
                        foreach (var resend in new[] { 1, 2 })
                        {
                            var wait = (log[basis].UtcTime - start) / ((1 << resend) - 1);
                            if (wait < MinResendWait || wait > MaxResendWait)
                            {
                                continue;
                            }

                            var onBeat = OnBeat(log, near, start, wait);
                            if (onBeat.Count > best.Count)
                            {
                                best = onBeat;
                            }
                        }
                    }
                }

                List<int> removed = best.Count >= 2 ? best : [];
                resent.UnionWith(removed);
                rest = rest.Skip(1).Where(i => !removed.Contains(i)).ToList();
            }
        }

        return log.Where((_, i) => !resent.Contains(i)).ToList();
    }

    // The copies that fit the beat of this wait; none when another copy falls in between.
    private static List<int> OnBeat(IReadOnlyList<LootEntry> log, List<int> near, DateTime start, TimeSpan wait)
    {
        var taken = new HashSet<int>();
        var onBeat = new List<int>();
        foreach (var i in near)
        {
            var gap = (log[i].UtcTime - start).TotalMilliseconds;
            for (var resend = 1; resend <= 6; resend++)
            {
                var expected = wait.TotalMilliseconds * ((1 << resend) - 1);
                if (!taken.Contains(resend) && Math.Abs(gap - expected) <= 0.08 * expected + 25)
                {
                    taken.Add(resend);
                    onBeat.Add(i);
                    break;
                }
            }
        }

        var end = onBeat.Count > 0 ? log[onBeat[^1]].UtcTime : start;
        return near.All(i => onBeat.Contains(i) || log[i].UtcTime > end) ? onBeat : [];
    }

    private static (List<LootEntry> Loot, List<TimeSpan> Offsets) MergeLinedUp(IEnumerable<IReadOnlyList<LootEntry>> logs)
    {
        var merged = new List<Seen>();
        var offsets = new List<TimeSpan>();
        foreach (var rawLog in logs)
        {
            var number = offsets.Count;
            var cleanLog = RemoveResends(rawLog);

            // Each PC's clock can be off by a minute or more: line this log up with what is merged so far.
            var offset = ClockOffset(merged.ConvertAll(s => s.Entry), cleanLog);
            offsets.Add(offset);

            var byKey = merged.Select((s, i) => (Key: Key(s.Entry), Index: i)).ToLookup(x => x.Key, x => x.Index);
            // Each earlier pickup can stand in for one pickup of this log only.
            var used = new HashSet<int>();
            var added = new List<Seen>();
            var previous = new Dictionary<(string, string, int, string), (DateTime Time, Seen Seen)>();
            foreach (var entry in cleanLog.Select(e => e with { UtcTime = e.UtcTime + offset }).OrderBy(e => e.UtcTime))
            {
                var key = Key(entry);
                var gap = previous.TryGetValue(key, out var last) ? entry.UtcTime - last.Time : TimeSpan.MaxValue;
                var quick = gap >= MinResendWait && gap <= QuickRepeat;

                // A quick repeat only matches a quick repeat of the other log, and a plain pickup a plain one.
                var match = byKey[key].FirstOrDefault(i => !used.Contains(i) && merged[i].IsQuick == quick && IsSameMoment(merged[i].Entry, entry), -1);
                Seen seen;
                if (match >= 0)
                {
                    used.Add(match);
                    seen = merged[match];
                    seen.Logs.Add(number);
                }
                else
                {
                    seen = new Seen(entry, number, quick ? last.Seen.Repeats ?? last.Seen : null);
                    added.Add(seen);
                }

                previous[key] = (entry.UtcTime, seen);
            }

            merged.AddRange(added);
        }

        // A quick repeat that only its own logger wrote down, of a pickup another logger saw too, was a resend.
        // When no other logger was there it stays: it may be two equal pickups.
        var loot = merged.Where(s => !(s.Repeats is { Logs.Count: > 1 } && s.Logs.Count == 1)).Select(s => s.Entry).OrderBy(e => e.UtcTime).ToList();
        return (loot, offsets);
    }

    /// <summary>
    /// How much to add to the times of <paramref name="log"/> so they match <paramref name="merged"/>:
    /// the time gap shared by the most pickups both logs saw (same looter, item, amount and body).
    /// Zero when the logs have too little in common to tell (two different fights, say).
    /// </summary>
    public static TimeSpan ClockOffset(IReadOnlyList<LootEntry> merged, IReadOnlyList<LootEntry> log)
    {
        if (merged.Count == 0 || log.Count == 0)
        {
            return TimeSpan.Zero;
        }

        var byKey = merged.Select((e, i) => (Entry: e, Index: i)).ToLookup(x => Key(x.Entry));
        var gaps = new List<(double Seconds, int Log, int Merged)>();
        for (var j = 0; j < log.Count; j++)
        {
            foreach (var other in byKey[Key(log[j])])
            {
                gaps.Add(((other.Entry.UtcTime - log[j].UtcTime).TotalSeconds, j, other.Index));
            }
        }

        if (gaps.Count == 0)
        {
            return TimeSpan.Zero;
        }

        // The gap most pickups agree with (within the same-event window). Each pickup counts once,
        // however many look-alikes (three equal potions from one body, say) the other log has.
        // The players who took them are counted too.
        gaps.Sort((a, b) => a.Seconds.CompareTo(b.Seconds));
        var window = SameEventWindow.TotalSeconds;
        var logUses = new Dictionary<int, int>();
        var mergedUses = new Dictionary<int, int>();
        var looters = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var windows = new List<(double Seconds, int Count, int Looters)>();
        var start = 0;
        for (var end = 0; end < gaps.Count; end++)
        {
            Use(logUses, gaps[end].Log, 1);
            Use(mergedUses, gaps[end].Merged, 1);
            Use(looters, merged[gaps[end].Merged].LootedByName, 1);
            while (gaps[end].Seconds - gaps[start].Seconds > window)
            {
                Use(logUses, gaps[start].Log, -1);
                Use(mergedUses, gaps[start].Merged, -1);
                Use(looters, merged[gaps[start].Merged].LootedByName, -1);
                start++;
            }

            windows.Add((gaps[(start + end) / 2].Seconds, Math.Min(logUses.Count, mergedUses.Count), looters.Count));
        }

        var best = windows[0];
        foreach (var w in windows)
        {
            if (w.Count > best.Count)
            {
                best = w;
            }
        }

        var runnerUp = windows.Where(w => Math.Abs(w.Seconds - best.Seconds) > 2 * window).Select(w => w.Count).DefaultIfEmpty(0).Max();

        // A few chance matches are not enough to move a whole log: two logs of one fight share most
        // of their pickups (84% to 98% in real logs), two different fights (a week of logs, say) almost none.
        // A tenth of the smaller log, rounded up as the guild site does. Or pickups by at least three players
        // when no other gap comes close: a logger that spent most of the fight on another map shares only a few
        // pickups, but all with the same gap. Different fights don't line up like that; at most one player
        // takes the same potions from the same enemy in both.
        var needed = Math.Max(3, (Math.Min(merged.Count, log.Count) + 9) / 10);
        var clear = best.Looters >= 3 && best.Count >= 3 * runnerUp;
        return (best.Count >= needed || clear) && Math.Abs(best.Seconds) > window / 2 ? TimeSpan.FromSeconds(best.Seconds) : TimeSpan.Zero;

        static void Use<T>(Dictionary<T, int> uses, T index, int change)
            where T : notnull
        {
            var count = uses.GetValueOrDefault(index) + change;
            if (count == 0)
            {
                uses.Remove(index);
            }
            else
            {
                uses[index] = count;
            }
        }
    }

    private static (string, string, int, string) Key(LootEntry e) =>
        (e.LootedByName.ToUpperInvariant(), e.ItemId.ToUpperInvariant(), e.Quantity, e.LootedFromName.ToUpperInvariant());

    private static bool IsSameMoment(LootEntry a, LootEntry b) => (a.UtcTime - b.UtcTime).Duration() <= SameEventWindow;

    /// <summary>A pickup of the merged result and the logs that saw it.</summary>
    private sealed class Seen(LootEntry entry, int log, Seen? repeats)
    {
        public LootEntry Entry { get; } = entry;

        public HashSet<int> Logs { get; } = [log];

        /// <summary>For a quick repeat (a resend, or two equal pickups taken together), the pickup it repeats.</summary>
        public Seen? Repeats { get; } = repeats;

        public bool IsQuick => Repeats is not null;
    }
}
