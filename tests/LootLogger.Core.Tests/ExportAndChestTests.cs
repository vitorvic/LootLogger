using System.Text;
using LootLogger.Core.Chest;
using LootLogger.Core.Data;
using LootLogger.Core.Export;
using LootLogger.Core.Session;
using LootLogger.Core.Tracking;

namespace LootLogger.Core.Tests;

public class ExportAndChestTests
{
    private static readonly ItemDatabase Items = ItemDatabase.LoadBuiltIn();

    private static LootEntry Loot(string by, string itemId, string name, int qty, long value, int second = 41) => new(
        new DateTime(2026, 10, 6, 2, 49, second, DateTimeKind.Utc).AddTicks(6627237),
        by, "Take Care", "", 0, itemId, name, qty, "XAgiota", "PlVAS", "", value, "Sunfang Cliffs");

    [Fact]
    public void Header_MatchesTheGuildFormat()
    {
        // Header of the CSV the guild uses today.
        const string reference = "timestamp_utc;looted_by__alliance;looted_by__guild;looted_by__name;item_id;item_name;quantity;looted_from__alliance;looted_from__guild;looted_from__name;died;died_player_guild;killed_by;killed_by_guild;average_est_market_value;cluster";
        Assert.Equal(reference, CsvExporter.Header);
    }

    [Fact]
    public void LootRow_HasSixteenColumnsInTheGuildLayout()
    {
        var entry = new LootEntry(new DateTime(2026, 10, 6, 2, 49, 41, DateTimeKind.Utc).AddTicks(6627237),
            "Pandas139", "Take Care", "", 0, "T6_HEAD_LEATHER_SET3@2", "Master's Assassin Hood", 1,
            "XAgiota", "PlVAS", "", 134608, "Sunfang Cliffs");

        Assert.Equal(
            "2026-10-06T02:49:41.6627237Z;;Take Care;Pandas139;T6_HEAD_LEATHER_SET3@2;Master's Assassin Hood;1;;PlVAS;XAgiota;;;;;134608;Sunfang Cliffs",
            CsvExporter.LootRow(entry));
    }

    [Fact]
    public void KillRow_FillsOnlyDeathColumns()
    {
        var kill = new KillEntry(new DateTime(2026, 10, 6, 3, 0, 0, DateTimeKind.Utc), "XAgiota", "PlVAS", "", "Pandas139", "Take Care", "", "Sunfang Cliffs");
        var columns = CsvExporter.KillRow(kill).Split(';');
        Assert.Equal(16, columns.Length);
        Assert.Equal(["XAgiota", "PlVAS", "Pandas139", "Take Care"], columns[10..14]);
        Assert.Equal("", columns[14]);
        Assert.Equal("Sunfang Cliffs", columns[15]);
    }

    [Fact]
    public void Write_UsesUtf8BomAndSortsByTime()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".csv");
        try
        {
            var later = new LootEntry(new DateTime(2026, 10, 6, 3, 0, 0, DateTimeKind.Utc), "B", "", "", 0, "X", "X", 1, "", "", "", 0, "");
            var earlier = later with { UtcTime = later.UtcTime.AddMinutes(-5), LootedByName = "A" };
            CsvExporter.Write(path, [later, earlier], []);

            var bytes = File.ReadAllBytes(path);
            Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, bytes[..3]);
            var lines = Encoding.UTF8.GetString(bytes[3..]).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
            Assert.Equal(3, lines.Length);
            Assert.Equal("A", lines[1].Split(';')[3]);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Session_AutosavesEachRow()
    {
        var folder = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        try
        {
            var session = new LootSession(new DateTime(2026, 10, 6, 21, 56, 0, DateTimeKind.Utc), folder) { Owner = "NillBlack" };
            session.Add(new LootEntry(DateTime.UtcNow, "A", "", "", 0, "X", "X", 2, "", "", "", 10, ""));
            session.Add(new LootEntry(DateTime.UtcNow, "B", "", "", 0, "Y", "Y", 1, "", "", "", 5, ""));

            var lines = File.ReadAllLines(session.AutosavePath!);
            Assert.Equal(3, lines.Length);
            var local = new DateTime(2026, 10, 6, 21, 56, 0, DateTimeKind.Utc).ToLocalTime();
            Assert.Equal($"Loot {local:dd-MM-yyyy} {local:HH}h{local:mm} NillBlack.csv", Path.GetFileName(session.AutosavePath));
            Assert.Equal(3, session.TotalItems);
            Assert.Equal(25, session.TotalValue);
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    [Fact]
    public void ChestLog_ParsesCopiedTextInEnglishAndPortuguese()
    {
        const string text = "\"Date\"\t\"Player\"\t\"Item\"\t\"Enchantment\"\t\"Quality\"\t\"Amount\"\n" +
                            "\"10/06/2026 03:10:22\"\t\"Pandas139\"\t\"Master's Demon Armor\"\t\"3\"\t\"2\"\t\"1\"\n" +
                            "\"10/06/2026 03:11:00\"\t\"Valniaa\"\t\"Poção de Crescimento Maior\"\t\"0\"\t\"0\"\t\"-2\"\n" +
                            "lixo que não é do log\n";

        var entries = ChestLogParser.Parse(text);

        Assert.Equal(2, entries.Count);
        Assert.Equal("Pandas139", entries[0].Player);
        Assert.Equal(3, entries[0].Enchantment);
        Assert.Equal(-2, entries[1].Amount);
    }

    [Fact]
    public void Compare_FindsWhatEachPlayerDidNotDeposit()
    {
        var loot = new[]
        {
            Loot("Pandas139", "T6_ARMOR_PLATE_HELL@3", "Master's Demon Armor", 1, 840692),
            Loot("Pandas139", "T7_POTION_REVIVE", "Major Gigantify Potion", 4, 12486),
            Loot("Valniaa", "T7_POTION_REVIVE", "Major Gigantify Potion", 2, 12486)
        };
        var chest = new[]
        {
            new ChestLogEntry(DateTime.UtcNow, "Pandas139", "Master's Demon Armor", 3, 2, 1),
            new ChestLogEntry(DateTime.UtcNow, "Pandas139", "Poção de Crescimento Maior", 0, 0, 1),
            new ChestLogEntry(DateTime.UtcNow, "Valniaa", "Major Gigantify Potion", 0, 0, 2),
            new ChestLogEntry(DateTime.UtcNow, "Valniaa", "Major Gigantify Potion", 0, 0, -2)
        };

        var result = ChestComparer.Compare(loot, chest, Items);

        var pandas = result.Single(r => r.Player == "Pandas139");
        Assert.Equal(5, pandas.Looted);
        Assert.Equal(2, pandas.Deposited);
        var missing = Assert.Single(pandas.Missing);
        Assert.Equal("T7_POTION_REVIVE", missing.ItemId);
        Assert.Equal(3, missing.Quantity);

        var valniaa = result.Single(r => r.Player == "Valniaa");
        Assert.Empty(valniaa.Missing);
        Assert.Equal("Pandas139", result[0].Player);
    }
}
