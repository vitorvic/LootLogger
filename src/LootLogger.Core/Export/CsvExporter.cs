using System.Globalization;
using System.Text;
using LootLogger.Core.Tracking;

namespace LootLogger.Core.Export;

/// <summary>
/// Writes the session with the 16 columns guild tools already read: UTF-8 with BOM, ';' separated,
/// one row per loot or death. The tools find each column by its name, so the order is free.
/// </summary>
public static class CsvExporter
{
    // The columns people read in Excel come first; the rest is for the tools (Vitor, 12/10).
    public const string Header =
        "timestamp_utc;looted_by__name;item_name;quantity;average_est_market_value;looted_from__name;looted_from__guild;" +
        "looted_by__guild;looted_by__alliance;item_id;looted_from__alliance;cluster;" +
        "died;died_player_guild;killed_by;killed_by_guild";

    /// <summary>Every row in time order, deaths among the pickups: the backup copy, which only ever appends.</summary>
    public static string ToCsv(IEnumerable<LootEntry> loot, IEnumerable<KillEntry> kills)
    {
        var rows = loot.Select(l => (l.UtcTime, Row: LootRow(l)))
            .Concat(kills.Select(k => (k.UtcTime, Row: KillRow(k))))
            .OrderBy(r => r.UtcTime);

        var sb = new StringBuilder();
        sb.Append(Header).Append("\r\n");
        foreach (var (_, row) in rows)
        {
            sb.Append(row).Append("\r\n");
        }

        return sb.ToString();
    }

    /// <summary>
    /// The file people open in Excel to delete or hide loot: the pickups in time order, then a blank line,
    /// a line with <paramref name="deathsTitle"/> and the deaths. Readers skip both lines, as they have no time.
    /// </summary>
    public static string ToExportCsv(IEnumerable<LootEntry> loot, IEnumerable<KillEntry> kills, string deathsTitle)
    {
        var sb = new StringBuilder();
        sb.Append(Header).Append("\r\n");
        foreach (var entry in loot.OrderBy(l => l.UtcTime))
        {
            sb.Append(LootRow(entry)).Append("\r\n");
        }

        var deaths = kills.OrderBy(k => k.UtcTime).ToList();
        if (deaths.Count > 0)
        {
            sb.Append("\r\n").Append(Clean(deathsTitle)).Append("\r\n");
            foreach (var death in deaths)
            {
                sb.Append(KillRow(death)).Append("\r\n");
            }
        }

        return sb.ToString();
    }

    public static void Write(string path, IEnumerable<LootEntry> loot, IEnumerable<KillEntry> kills, string deathsTitle)
    {
        File.WriteAllText(path, ToExportCsv(loot, kills, deathsTitle), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
    }

    /// <summary>A name people can read: "Loot 06-10-2026 21h56 UTC NillBlack.csv". UTC because guilds schedule fights by it.</summary>
    public static string DefaultFileName(DateTime utcNow, string? player = null)
    {
        var when = utcNow.ToString("dd-MM-yyyy HH'h'mm 'UTC'", CultureInfo.InvariantCulture);
        var who = string.Concat((player ?? string.Empty).Where(c => !Path.GetInvalidFileNameChars().Contains(c))).Trim();
        return who.Length > 0 ? $"Loot {when} {who}.csv" : $"Loot {when}.csv";
    }

    public static string LootRow(LootEntry l) => string.Join(';',
        Timestamp(l.UtcTime),
        Clean(l.LootedByName),
        Clean(l.ItemNameEnglish),
        l.Quantity.ToString(CultureInfo.InvariantCulture),
        l.UnitValue.ToString(CultureInfo.InvariantCulture),
        Clean(l.LootedFromName),
        Clean(l.LootedFromGuild),
        Clean(l.LootedByGuild),
        Clean(l.LootedByAlliance),
        Clean(l.ItemId),
        Clean(l.LootedFromAlliance),
        Clean(l.Cluster),
        string.Empty,
        string.Empty,
        string.Empty,
        string.Empty);

    public static string KillRow(KillEntry k) => string.Join(';',
        Timestamp(k.UtcTime),
        string.Empty,
        string.Empty,
        string.Empty,
        string.Empty,
        string.Empty,
        string.Empty,
        string.Empty,
        string.Empty,
        string.Empty,
        string.Empty,
        Clean(k.Cluster),
        Clean(k.Died),
        Clean(k.DiedGuild),
        Clean(k.KilledBy),
        Clean(k.KilledByGuild));

    // Same shape as the reference file: 2026-10-06T02:49:41.6627237Z
    private static string Timestamp(DateTime utc) =>
        DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToString("O", CultureInfo.InvariantCulture);

    // The format has no quoting, so separators inside names are replaced.
    private static string Clean(string? value) =>
        string.IsNullOrEmpty(value) ? string.Empty : value.Replace(';', ',').Replace('\r', ' ').Replace('\n', ' ');
}
