using System.IO.Compression;
using System.Xml.Linq;
using LootLogger.Core.Export;
using LootLogger.Core.Tracking;

namespace LootLogger.Core.Tests;

public class XlsxExportTests
{
    private static readonly XlsxExporter.Labels Labels = new("Loot", "Hora", "Quem pegou", "Guild", "Item", "Qtd", "De quem", "Guild de quem", "Valor", "Total");

    private static LootEntry Loot(int second, string by, string item, int qty, long unit, string from = "Fulano") => new(
        new DateTime(2026, 10, 11, 22, 17, second, DateTimeKind.Utc), by, "PlVAS", "OOPS", 1, item, item, qty, from, "Take Care", "", unit, "Razorrock Passage");

    private static Dictionary<string, XDocument> Open(byte[] file)
    {
        using var zip = new ZipArchive(new MemoryStream(file));
        return zip.Entries.ToDictionary(e => e.FullName, e =>
        {
            using var s = e.Open();
            return XDocument.Load(s);
        });
    }

    private static byte[] Export(IEnumerable<LootEntry> loot)
    {
        using var ms = new MemoryStream();
        XlsxExporter.Write(ms, loot, Labels, l => l.ItemNameEnglish + " <&>", l => l.LootedFromName == LootTracker.MobName ? "monstro" : l.LootedFromName,
            TimeZoneInfo.CreateCustomTimeZone("BRT", TimeSpan.FromHours(-3), "BRT", "BRT"));
        return ms.ToArray();
    }

    [Fact]
    public void Sheet_HasOnlyLoot_InTimeOrder_WithLocalTimeAndTotal()
    {
        var parts = Open(Export([Loot(25, "B", "T6_SHOES", 3, 100_000, LootTracker.MobName), Loot(24, "A", "T5_MAIN", 1, 1_083_079)]));

        Assert.Contains("[Content_Types].xml", parts.Keys);
        Assert.Contains("xl/workbook.xml", parts.Keys);
        Assert.Contains("xl/styles.xml", parts.Keys);
        XNamespace ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        var rows = parts["xl/worksheets/sheet1.xml"].Descendants(ns + "row").ToList();
        Assert.Equal(4, rows.Count);

        string[] Cells(XElement row) => row.Elements(ns + "c").Select(c => c.Element(ns + "v")?.Value ?? c.Value).ToArray();

        Assert.Equal(["Hora", "Quem pegou", "Guild", "Item", "Qtd", "De quem", "Guild de quem", "Valor"], Cells(rows[0]));
        Assert.Equal(["19:17:24", "A", "PlVAS", "T5_MAIN <&>", "1", "Fulano", "Take Care", "1083079"], Cells(rows[1]));
        Assert.Equal(["19:17:25", "B", "PlVAS", "T6_SHOES <&>", "3", "monstro", "Take Care", "300000"], Cells(rows[2]));
        Assert.Equal(["Total", "4", "1383079"], Cells(rows[3]));
        Assert.Equal("SUM(H2:H3)", rows[3].Elements(ns + "c").Last().Element(ns + "f")!.Value);
    }

    [Fact]
    public void EmptySession_StillOpens()
    {
        var parts = Open(Export([]));
        XNamespace ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        Assert.Equal(2, parts["xl/worksheets/sheet1.xml"].Descendants(ns + "row").Count());
    }
}
