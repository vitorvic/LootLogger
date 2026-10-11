using System.Globalization;
using System.IO.Compression;
using System.Security;
using System.Text;
using LootLogger.Core.Tracking;

namespace LootLogger.Core.Export;

/// <summary>
/// A spreadsheet only for reading: one row per item looted, no death rows and no empty columns, with a total.
/// The CSV stays the file guild tools read; this one is for people opening it in Excel.
/// </summary>
public static class XlsxExporter
{
    /// <summary>Header texts, in the program's language.</summary>
    public sealed record Labels(string Sheet, string Time, string LootedBy, string Guild, string Item, string Quantity,
        string LootedFrom, string FromGuild, string Value, string Total);

    /// <param name="itemName">Item name as people read it, like "Cair da Noite 5.0".</param>
    /// <param name="lootedFrom">Body name as people read it ("monstro" instead of the internal mob marker).</param>
    public static void Write(string path, IEnumerable<LootEntry> loot, Labels labels, Func<LootEntry, string> itemName,
        Func<LootEntry, string> lootedFrom, TimeZoneInfo zone)
    {
        using var file = File.Create(path);
        Write(file, loot, labels, itemName, lootedFrom, zone);
    }

    public static void Write(Stream output, IEnumerable<LootEntry> loot, Labels labels, Func<LootEntry, string> itemName,
        Func<LootEntry, string> lootedFrom, TimeZoneInfo zone)
    {
        var rows = loot.OrderBy(l => l.UtcTime).ToList();
        using var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);
        Add(zip, "[Content_Types].xml", ContentTypes);
        Add(zip, "_rels/.rels", RootRels);
        Add(zip, "xl/workbook.xml", Workbook(labels.Sheet));
        Add(zip, "xl/_rels/workbook.xml.rels", WorkbookRels);
        Add(zip, "xl/styles.xml", Styles);
        Add(zip, "xl/worksheets/sheet1.xml", Sheet(rows, labels, itemName, lootedFrom, zone));
    }

    private static string Sheet(List<LootEntry> rows, Labels labels, Func<LootEntry, string> itemName, Func<LootEntry, string> lootedFrom, TimeZoneInfo zone)
    {
        var sb = new StringBuilder();
        sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>")
            .Append("<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">")
            .Append("<sheetViews><sheetView workbookViewId=\"0\"><pane ySplit=\"1\" topLeftCell=\"A2\" activePane=\"bottomLeft\" state=\"frozen\"/></sheetView></sheetViews>")
            .Append("<cols>")
            .Append("<col min=\"1\" max=\"1\" width=\"10\" customWidth=\"1\"/>")
            .Append("<col min=\"2\" max=\"3\" width=\"18\" customWidth=\"1\"/>")
            .Append("<col min=\"4\" max=\"4\" width=\"40\" customWidth=\"1\"/>")
            .Append("<col min=\"5\" max=\"5\" width=\"7\" customWidth=\"1\"/>")
            .Append("<col min=\"6\" max=\"7\" width=\"18\" customWidth=\"1\"/>")
            .Append("<col min=\"8\" max=\"8\" width=\"14\" customWidth=\"1\"/>")
            .Append("</cols><sheetData>");

        sb.Append("<row r=\"1\">");
        string[] header = [labels.Time, labels.LootedBy, labels.Guild, labels.Item, labels.Quantity, labels.LootedFrom, labels.FromGuild, labels.Value];
        for (var c = 0; c < header.Length; c++)
        {
            Text(sb, c, 1, header[c], style: 1);
        }

        sb.Append("</row>");

        var r = 1;
        foreach (var l in rows)
        {
            r++;
            sb.Append("<row r=\"").Append(r).Append("\">");
            Text(sb, 0, r, TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(l.UtcTime, DateTimeKind.Utc), zone).ToString("HH:mm:ss", CultureInfo.InvariantCulture));
            Text(sb, 1, r, l.LootedByName);
            Text(sb, 2, r, l.LootedByGuild);
            Text(sb, 3, r, itemName(l));
            Number(sb, 4, r, l.Quantity, style: 0);
            Text(sb, 5, r, lootedFrom(l));
            Text(sb, 6, r, l.LootedFromGuild);
            Number(sb, 7, r, l.TotalValue, style: 2);
            sb.Append("</row>");
        }

        r++;
        sb.Append("<row r=\"").Append(r).Append("\">");
        Text(sb, 0, r, labels.Total, style: 1);
        sb.Append("<c r=\"E").Append(r).Append("\" s=\"1\"><f>SUM(E2:E").Append(r - 1).Append(")</f><v>")
            .Append(rows.Sum(l => (long) l.Quantity)).Append("</v></c>");
        sb.Append("<c r=\"H").Append(r).Append("\" s=\"3\"><f>SUM(H2:H").Append(r - 1).Append(")</f><v>")
            .Append(rows.Sum(l => l.TotalValue)).Append("</v></c>");
        sb.Append("</row>");

        sb.Append("</sheetData>");
        if (rows.Count > 0)
        {
            sb.Append("<autoFilter ref=\"A1:H").Append(rows.Count + 1).Append("\"/>");
        }

        return sb.Append("</worksheet>").ToString();
    }

    private static void Text(StringBuilder sb, int column, int row, string value, int style = 0)
    {
        sb.Append("<c r=\"").Append((char) ('A' + column)).Append(row).Append('"');
        if (style != 0)
        {
            sb.Append(" s=\"").Append(style).Append('"');
        }

        sb.Append(" t=\"inlineStr\"><is><t xml:space=\"preserve\">").Append(SecurityElement.Escape(Clean(value))).Append("</t></is></c>");
    }

    private static void Number(StringBuilder sb, int column, int row, long value, int style) =>
        sb.Append("<c r=\"").Append((char) ('A' + column)).Append(row).Append("\" s=\"").Append(style).Append("\"><v>")
            .Append(value.ToString(CultureInfo.InvariantCulture)).Append("</v></c>");

    // Characters XML 1.0 does not allow would make Excel refuse the file.
    private static string Clean(string value) => string.Concat(value.Where(c => c is '\t' or '\n' or '\r' || c >= ' '));

    private static void Add(ZipArchive zip, string name, string content)
    {
        using var writer = new StreamWriter(zip.CreateEntry(name, CompressionLevel.Optimal).Open(), new UTF8Encoding(false));
        writer.Write(content);
    }

    private static string Workbook(string sheetName) =>
        "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
        "<workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\">" +
        "<sheets><sheet name=\"" + SecurityElement.Escape(sheetName) + "\" sheetId=\"1\" r:id=\"rId1\"/></sheets>" +
        "<definedNames><definedName name=\"_xlnm._FilterDatabase\" localSheetId=\"0\" hidden=\"1\">'" + sheetName.Replace("'", "''") + "'!$A$1:$H$1</definedName></definedNames>" +
        "</workbook>";

    private const string ContentTypes =
        "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
        "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">" +
        "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>" +
        "<Default Extension=\"xml\" ContentType=\"application/xml\"/>" +
        "<Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/>" +
        "<Override PartName=\"/xl/worksheets/sheet1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/>" +
        "<Override PartName=\"/xl/styles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml\"/>" +
        "</Types>";

    private const string RootRels =
        "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
        "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
        "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/>" +
        "</Relationships>";

    private const string WorkbookRels =
        "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
        "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
        "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet1.xml\"/>" +
        "<Relationship Id=\"rId2\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles\" Target=\"styles.xml\"/>" +
        "</Relationships>";

    // Styles: 0 plain, 1 bold, 2 thousands separator, 3 bold with thousands separator.
    private const string Styles =
        "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
        "<styleSheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">" +
        "<fonts count=\"2\"><font><sz val=\"11\"/><name val=\"Calibri\"/></font><font><b/><sz val=\"11\"/><name val=\"Calibri\"/></font></fonts>" +
        "<fills count=\"2\"><fill><patternFill patternType=\"none\"/></fill><fill><patternFill patternType=\"gray125\"/></fill></fills>" +
        "<borders count=\"1\"><border><left/><right/><top/><bottom/><diagonal/></border></borders>" +
        "<cellStyleXfs count=\"1\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\"/></cellStyleXfs>" +
        "<cellXfs count=\"4\">" +
        "<xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\"/>" +
        "<xf numFmtId=\"0\" fontId=\"1\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyFont=\"1\"/>" +
        "<xf numFmtId=\"3\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyNumberFormat=\"1\"/>" +
        "<xf numFmtId=\"3\" fontId=\"1\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyFont=\"1\" applyNumberFormat=\"1\"/>" +
        "</cellXfs>" +
        "<cellStyles count=\"1\"><cellStyle name=\"Normal\" xfId=\"0\" builtinId=\"0\"/></cellStyles>" +
        "</styleSheet>";
}
