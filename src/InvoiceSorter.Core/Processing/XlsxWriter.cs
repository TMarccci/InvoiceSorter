using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml;

namespace InvoiceSorter.Core.Processing;

/// <summary>One worksheet: a bold, frozen, filterable header row and the rows below it.</summary>
/// <param name="Rows">Cell values: string (text), int / double (number), DateTime (date), null (empty).</param>
/// <param name="Widths">Column widths in characters (missing: 12).</param>
public sealed record XlsxSheet(string Name, IReadOnlyList<string> Header, IReadOnlyList<object?[]> Rows, IReadOnlyList<double>? Widths = null);

/// <summary>
/// Writes a minimal Excel workbook (Office Open XML, .xlsx) without any library: inline strings, numbers and dates
/// (shown as 2026.09.02.).
/// </summary>
public static class XlsxWriter
{
    /// <summary>Excel keeps at most this many characters in a cell.</summary>
    public const int MaxCellText = 32767;

    private const string Main = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private const string Rel = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private const int HeaderStyle = 1, DateStyle = 2;

    public static void Write(string path, IReadOnlyList<XlsxSheet> sheets)
    {
        var temp = path + ".tmp";
        using (var zip = ZipFile.Open(temp, ZipArchiveMode.Create))
        {
            Part(zip, "[Content_Types].xml", ContentTypes(sheets.Count));
            Part(zip, "_rels/.rels",
                "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
                $"<Relationship Id=\"rId1\" Type=\"{Rel}/officeDocument\" Target=\"xl/workbook.xml\"/></Relationships>");
            Part(zip, "xl/workbook.xml", Workbook(sheets));
            Part(zip, "xl/_rels/workbook.xml.rels", WorkbookRels(sheets.Count));
            Part(zip, "xl/styles.xml", Styles);
            for (int i = 0; i < sheets.Count; i++)
                WriteSheet(zip.CreateEntry($"xl/worksheets/sheet{i + 1}.xml", CompressionLevel.Optimal), sheets[i]);
        }
        File.Move(temp, path, overwrite: true);
    }

    private static void Part(ZipArchive zip, string name, string xml)
    {
        using var writer = new StreamWriter(zip.CreateEntry(name, CompressionLevel.Optimal).Open(), new UTF8Encoding(false));
        writer.Write("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
        writer.Write(xml);
    }

    private static string ContentTypes(int sheets)
    {
        var sb = new StringBuilder("<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">" +
            "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>" +
            "<Default Extension=\"xml\" ContentType=\"application/xml\"/>" +
            "<Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/>" +
            "<Override PartName=\"/xl/styles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml\"/>");
        for (int i = 1; i <= sheets; i++)
            sb.Append($"<Override PartName=\"/xl/worksheets/sheet{i}.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/>");
        return sb.Append("</Types>").ToString();
    }

    private static string Workbook(IReadOnlyList<XlsxSheet> sheets)
    {
        var sb = new StringBuilder($"<workbook xmlns=\"{Main}\" xmlns:r=\"{Rel}\"><sheets>");
        for (int i = 0; i < sheets.Count; i++)
            sb.Append($"<sheet name=\"{Escape(SheetName(sheets[i].Name))}\" sheetId=\"{i + 1}\" r:id=\"rId{i + 1}\"/>");
        sb.Append("</sheets><definedNames>");
        for (int i = 0; i < sheets.Count; i++)
            sb.Append($"<definedName name=\"_xlnm._FilterDatabase\" localSheetId=\"{i}\" hidden=\"1\">" +
                      $"{Escape($"'{SheetName(sheets[i].Name)}'!{FilterRange(sheets[i], absolute: true)}")}</definedName>");
        return sb.Append("</definedNames></workbook>").ToString();
    }

    private static string WorkbookRels(int sheets)
    {
        var sb = new StringBuilder("<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">");
        for (int i = 1; i <= sheets; i++)
            sb.Append($"<Relationship Id=\"rId{i}\" Type=\"{Rel}/worksheet\" Target=\"worksheets/sheet{i}.xml\"/>");
        sb.Append($"<Relationship Id=\"rId{sheets + 1}\" Type=\"{Rel}/styles\" Target=\"styles.xml\"/>");
        return sb.Append("</Relationships>").ToString();
    }

    // 0: normal, 1: header (bold, grey), 2: date 2026.09.02.
    private const string Styles =
        $"<styleSheet xmlns=\"{Main}\">" +
        "<numFmts count=\"1\"><numFmt numFmtId=\"164\" formatCode=\"yyyy\\.mm\\.dd\\.\"/></numFmts>" +
        "<fonts count=\"2\"><font><sz val=\"11\"/><name val=\"Calibri\"/><family val=\"2\"/></font>" +
        "<font><b/><sz val=\"11\"/><name val=\"Calibri\"/><family val=\"2\"/></font></fonts>" +
        "<fills count=\"3\"><fill><patternFill patternType=\"none\"/></fill><fill><patternFill patternType=\"gray125\"/></fill>" +
        "<fill><patternFill patternType=\"solid\"><fgColor rgb=\"FFE2E8F0\"/><bgColor indexed=\"64\"/></patternFill></fill></fills>" +
        "<borders count=\"1\"><border><left/><right/><top/><bottom/><diagonal/></border></borders>" +
        "<cellStyleXfs count=\"1\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\"/></cellStyleXfs>" +
        "<cellXfs count=\"3\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\"/>" +
        "<xf numFmtId=\"0\" fontId=\"1\" fillId=\"2\" borderId=\"0\" xfId=\"0\" applyFont=\"1\" applyFill=\"1\"/>" +
        "<xf numFmtId=\"164\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyNumberFormat=\"1\"/></cellXfs>" +
        "<cellStyles count=\"1\"><cellStyle name=\"Normal\" xfId=\"0\" builtinId=\"0\"/></cellStyles></styleSheet>";

    private static void WriteSheet(ZipArchiveEntry entry, XlsxSheet sheet)
    {
        using var stream = entry.Open();
        using var xml = XmlWriter.Create(stream, new XmlWriterSettings { Encoding = new UTF8Encoding(false) });
        xml.WriteStartDocument(standalone: true);
        xml.WriteStartElement("worksheet", Main);

        // Header row frozen.
        xml.WriteStartElement("sheetViews", Main);
        xml.WriteStartElement("sheetView", Main);
        xml.WriteAttributeString("workbookViewId", "0");
        xml.WriteStartElement("pane", Main);
        xml.WriteAttributeString("ySplit", "1");
        xml.WriteAttributeString("topLeftCell", "A2");
        xml.WriteAttributeString("activePane", "bottomLeft");
        xml.WriteAttributeString("state", "frozen");
        xml.WriteEndElement();
        xml.WriteEndElement();
        xml.WriteEndElement();

        xml.WriteStartElement("cols", Main);
        for (int c = 0; c < sheet.Header.Count; c++)
        {
            xml.WriteStartElement("col", Main);
            xml.WriteAttributeString("min", (c + 1).ToString(CultureInfo.InvariantCulture));
            xml.WriteAttributeString("max", (c + 1).ToString(CultureInfo.InvariantCulture));
            var width = sheet.Widths != null && c < sheet.Widths.Count ? sheet.Widths[c] : 12;
            xml.WriteAttributeString("width", width.ToString("0.##", CultureInfo.InvariantCulture));
            xml.WriteAttributeString("customWidth", "1");
            xml.WriteEndElement();
        }
        xml.WriteEndElement();

        xml.WriteStartElement("sheetData", Main);
        WriteRow(xml, 1, sheet.Header.Cast<object?>().ToArray(), HeaderStyle);
        for (int r = 0; r < sheet.Rows.Count; r++) WriteRow(xml, r + 2, sheet.Rows[r], 0);
        xml.WriteEndElement();

        xml.WriteStartElement("autoFilter", Main);
        xml.WriteAttributeString("ref", FilterRange(sheet, absolute: false));
        xml.WriteEndElement();

        xml.WriteEndElement();
        xml.WriteEndDocument();
    }

    private static void WriteRow(XmlWriter xml, int row, object?[] values, int style)
    {
        xml.WriteStartElement("row", Main);
        xml.WriteAttributeString("r", row.ToString(CultureInfo.InvariantCulture));
        for (int c = 0; c < values.Length; c++)
        {
            var value = values[c];
            if (value == null || value is string { Length: 0 }) continue;
            xml.WriteStartElement("c", Main);
            xml.WriteAttributeString("r", CellName(c, row));
            switch (value)
            {
                case DateTime date:
                    xml.WriteAttributeString("s", DateStyle.ToString(CultureInfo.InvariantCulture));
                    xml.WriteElementString("v", Main, date.ToOADate().ToString(CultureInfo.InvariantCulture));
                    break;
                case int or long or double or decimal:
                    if (style != 0) xml.WriteAttributeString("s", style.ToString(CultureInfo.InvariantCulture));
                    xml.WriteElementString("v", Main, Convert.ToDouble(value, CultureInfo.InvariantCulture).ToString("R", CultureInfo.InvariantCulture));
                    break;
                default:
                    if (style != 0) xml.WriteAttributeString("s", style.ToString(CultureInfo.InvariantCulture));
                    xml.WriteAttributeString("t", "inlineStr");
                    xml.WriteStartElement("is", Main);
                    xml.WriteStartElement("t", Main);
                    var text = CellText(value.ToString() ?? "");
                    if (text.Length != text.Trim().Length || text.Contains('\n')) xml.WriteAttributeString("xml", "space", null, "preserve");
                    xml.WriteString(text);
                    xml.WriteEndElement();
                    xml.WriteEndElement();
                    break;
            }
            xml.WriteEndElement();
        }
        xml.WriteEndElement();
    }

    /// <summary>The text without the characters XML cannot hold, cut to the Excel cell limit.</summary>
    public static string CellText(string text)
    {
        var sb = new StringBuilder(Math.Min(text.Length, MaxCellText));
        for (int i = 0; i < text.Length; i++)
        {
            char ch = text[i];
            if (char.IsHighSurrogate(ch) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1])) { sb.Append(ch).Append(text[++i]); continue; }
            if (char.IsSurrogate(ch) || ch < 0x20 && ch is not ('\t' or '\n' or '\r') || ch is '￾' or '￿') continue;
            if (ch == '\r') { if (i + 1 < text.Length && text[i + 1] == '\n') continue; ch = '\n'; }
            sb.Append(ch);
        }
        if (sb.Length <= MaxCellText) return sb.ToString();
        int cut = MaxCellText - 1;
        if (char.IsHighSurrogate(sb[cut - 1])) cut--;
        return sb.ToString(0, cut) + "…";
    }

    /// <summary>A1-style cell reference: (0, 1) → "A1", (27, 3) → "AB3".</summary>
    public static string CellName(int column, int row)
    {
        var name = "";
        for (int c = column + 1; c > 0; c = (c - 1) / 26) name = (char)('A' + (c - 1) % 26) + name;
        return name + row.ToString(CultureInfo.InvariantCulture);
    }

    private static string FilterRange(XlsxSheet sheet, bool absolute)
    {
        string from = CellName(0, 1), to = CellName(Math.Max(0, sheet.Header.Count - 1), sheet.Rows.Count + 1);
        if (!absolute) return $"{from}:{to}";
        static string Abs(string cell) { int i = cell.IndexOfAny("0123456789".ToCharArray()); return $"${cell[..i]}${cell[i..]}"; }
        return $"{Abs(from)}:{Abs(to)}";
    }

    /// <summary>Excel sheet names: at most 31 characters, none of []:*?/\.</summary>
    private static string SheetName(string name)
    {
        var clean = new string(name.Where(ch => ch is not ('[' or ']' or ':' or '*' or '?' or '/' or '\\')).ToArray()).Trim();
        return clean.Length == 0 ? "Munka1" : clean.Length > 31 ? clean[..31] : clean;
    }

    private static string Escape(string s) => System.Security.SecurityElement.Escape(s) ?? "";
}
