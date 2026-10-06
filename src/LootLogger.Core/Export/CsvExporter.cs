using System.Globalization;
using System.Text;
using LootLogger.Core.Tracking;

namespace LootLogger.Core.Export;

/// <summary>
/// Writes the session in the 16-column layout guild tools already read:
/// UTF-8 with BOM, ';' separated, one row per loot or death, ordered by time.
/// </summary>
public static class CsvExporter
{
    public const string Header =
        "timestamp_utc;looted_by__alliance;looted_by__guild;looted_by__name;item_id;item_name;quantity;" +
        "looted_from__alliance;looted_from__guild;looted_from__name;died;died_player_guild;killed_by;killed_by_guild;" +
        "average_est_market_value;cluster";

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

    public static void Write(string path, IEnumerable<LootEntry> loot, IEnumerable<KillEntry> kills)
    {
        File.WriteAllText(path, ToCsv(loot, kills), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
    }

    public static string DefaultFileName(DateTime utcNow) =>
        $"log-{utcNow.ToString("yyyy-MM-dd-HH-mm-ss", CultureInfo.InvariantCulture)}utc.csv";

    public static string LootRow(LootEntry l) => string.Join(';',
        Timestamp(l.UtcTime),
        Clean(l.LootedByAlliance),
        Clean(l.LootedByGuild),
        Clean(l.LootedByName),
        Clean(l.ItemId),
        Clean(l.ItemNameEnglish),
        l.Quantity.ToString(CultureInfo.InvariantCulture),
        Clean(l.LootedFromAlliance),
        Clean(l.LootedFromGuild),
        Clean(l.LootedFromName),
        string.Empty,
        string.Empty,
        string.Empty,
        string.Empty,
        l.UnitValue.ToString(CultureInfo.InvariantCulture),
        Clean(l.Cluster));

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
        Clean(k.Died),
        Clean(k.DiedGuild),
        Clean(k.KilledBy),
        Clean(k.KilledByGuild),
        string.Empty,
        Clean(k.Cluster));

    // Same shape as the reference file: 2026-10-06T02:49:41.6627237Z
    private static string Timestamp(DateTime utc) =>
        DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToString("O", CultureInfo.InvariantCulture);

    // The format has no quoting, so separators inside names are replaced.
    private static string Clean(string? value) =>
        string.IsNullOrEmpty(value) ? string.Empty : value.Replace(';', ',').Replace('\r', ' ').Replace('\n', ' ');
}
