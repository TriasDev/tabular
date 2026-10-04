using System.Globalization;
using System.Text;

namespace TriasDev.Tabular.Ods;

/// <summary>The fixed parts of an OpenDocument spreadsheet, and the start and end of its content.</summary>
internal static class OdsParts
{
    public const string Mimetype = "application/vnd.oasis.opendocument.spreadsheet";

    /// <summary>The widest column style declared, in characters — Excel's own limit, which the writer enforces.</summary>
    public const int WidestColumn = 255;

    /// <summary>Cell styles: a date, a date and time, a boolean.</summary>
    public const string DateStyle = "ce1";

    public const string DateTimeStyle = "ce2";

    public const string BooleanStyle = "ce3";

    public const string TableEnd = "</table:table>";

    public const string SpreadsheetEnd = "</office:spreadsheet></office:body></office:document-content>";

    /// <summary>The auto-filters, as LibreOffice's sheet-local anonymous database ranges, header through last row.</summary>
    public static string DatabaseRanges(IReadOnlyList<(int Sheet, string Name, int Columns, long Rows)> filters)
    {
        if (filters.Count == 0)
        {
            return string.Empty;
        }

        StringBuilder xml = new("<table:database-ranges>");

        foreach ((int sheet, string name, int columns, long rows) in filters)
        {
            string quoted = "'" + name.Replace("'", "''", StringComparison.Ordinal) + "'";
            xml.Append(CultureInfo.InvariantCulture, $"<table:database-range table:name=\"__Anonymous_Sheet_DB__{sheet}\" table:target-range-address=\"");
            AppendAttribute(xml, $"{quoted}.A1:{quoted}.{Xlsx.XlsxParts.ColumnName(columns - 1)}{rows}");
            xml.Append("\" table:display-filter-buttons=\"true\"/>");
        }

        return xml.Append("</table:database-ranges>").ToString();
    }

    internal const string XmlDeclaration = "<?xml version=\"1.0\" encoding=\"UTF-8\"?>";

    internal const string Namespaces =
        " xmlns:office=\"urn:oasis:names:tc:opendocument:xmlns:office:1.0\""
        + " xmlns:style=\"urn:oasis:names:tc:opendocument:xmlns:style:1.0\""
        + " xmlns:text=\"urn:oasis:names:tc:opendocument:xmlns:text:1.0\""
        + " xmlns:table=\"urn:oasis:names:tc:opendocument:xmlns:table:1.0\""
        + " xmlns:number=\"urn:oasis:names:tc:opendocument:xmlns:datastyle:1.0\""
        + " xmlns:fo=\"urn:oasis:names:tc:opendocument:xmlns:xsl-fo-compatible:1.0\""
        + " office:version=\"1.3\"";

    private static readonly string[] ColumnStyleNames = [.. Enumerable.Range(0, WidestColumn + 1).Select(i => string.Create(CultureInfo.InvariantCulture, $"co{i}"))];

    /// <summary>
    /// Everything <c>content.xml</c> holds before its first table: the automatic styles every table
    /// may use, declared up front because the part streams and later sheets' widths are not yet known.
    /// </summary>
    public static readonly string ContentStart = BuildContentStart();

    public static byte[] Manifest(bool settings) => Encoding.UTF8.GetBytes(
        XmlDeclaration
        + "<manifest:manifest xmlns:manifest=\"urn:oasis:names:tc:opendocument:xmlns:manifest:1.0\" manifest:version=\"1.3\">"
        + "<manifest:file-entry manifest:full-path=\"/\" manifest:version=\"1.3\" manifest:media-type=\"" + Mimetype + "\"/>"
        + "<manifest:file-entry manifest:full-path=\"content.xml\" manifest:media-type=\"text/xml\"/>"
        + "<manifest:file-entry manifest:full-path=\"styles.xml\" manifest:media-type=\"text/xml\"/>"
        + (settings ? "<manifest:file-entry manifest:full-path=\"settings.xml\" manifest:media-type=\"text/xml\"/>" : string.Empty)
        + "</manifest:manifest>");

    /// <summary>
    /// The view settings that freeze panes, as LibreOffice stores them: per sheet, a split mode of 2
    /// (frozen) and the split position in rows or columns, the bottom-right part active. The root declares
    /// <c>ooo</c>: the view-settings set's name is a QName, and LibreOffice ignores the set without it.
    /// </summary>
    public static byte[] Settings(IReadOnlyList<(string Name, int Rows, int Columns)> frozen)
    {
        StringBuilder xml = new(
            XmlDeclaration
            + "<office:document-settings xmlns:office=\"urn:oasis:names:tc:opendocument:xmlns:office:1.0\" xmlns:config=\"urn:oasis:names:tc:opendocument:xmlns:config:1.0\" xmlns:ooo=\"http://openoffice.org/2004/office\" office:version=\"1.3\">"
            + "<office:settings><config:config-item-set config:name=\"ooo:view-settings\"><config:config-item-map-indexed config:name=\"Views\"><config:config-item-map-entry>"
            + "<config:config-item config:name=\"ViewId\" config:type=\"string\">view1</config:config-item><config:config-item-map-named config:name=\"Tables\">");

        foreach ((string name, int rows, int columns) in frozen)
        {
            xml.Append("<config:config-item-map-entry config:name=\"");
            AppendAttribute(xml, name);
            xml.Append("\">");
            Item(xml, "HorizontalSplitMode", "short", columns > 0 ? 2 : 0);
            Item(xml, "VerticalSplitMode", "short", rows > 0 ? 2 : 0);
            Item(xml, "HorizontalSplitPosition", "int", columns);
            Item(xml, "VerticalSplitPosition", "int", rows);
            Item(xml, "ActiveSplitRange", "short", 2);
            Item(xml, "PositionLeft", "int", 0);
            Item(xml, "PositionRight", "int", columns);
            Item(xml, "PositionTop", "int", 0);
            Item(xml, "PositionBottom", "int", rows);
            xml.Append("</config:config-item-map-entry>");
        }

        xml.Append("</config:config-item-map-named></config:config-item-map-entry></config:config-item-map-indexed></config:config-item-set></office:settings></office:document-settings>");
        return Encoding.UTF8.GetBytes(xml.ToString());
    }

    private static void Item(StringBuilder xml, string name, string type, int value) =>
        xml.Append(CultureInfo.InvariantCulture, $"<config:config-item config:name=\"{name}\" config:type=\"{type}\">{value}</config:config-item>");

    private static void AppendAttribute(StringBuilder xml, string value)
    {
        foreach (char c in value)
        {
            _ = c switch
            {
                '&' => xml.Append("&amp;"),
                '<' => xml.Append("&lt;"),
                '>' => xml.Append("&gt;"),
                '"' => xml.Append("&quot;"),
                _ => xml.Append(c),
            };
        }
    }

    /// <summary>The column style for a width in characters, rounded to the nearest whole one.</summary>
    public static string ColumnStyleFor(double width) =>
        ColumnStyleNames[Math.Clamp((int)Math.Round(width, MidpointRounding.AwayFromZero), 1, WidestColumn)];

    /// <summary>The default date data style, named <paramref name="name"/>.</summary>
    public static string DateDataStyle(string name) =>
        $"<number:date-style style:name=\"{name}\"><number:year number:style=\"long\"/><number:text>-</number:text>"
        + "<number:month number:style=\"long\"/><number:text>-</number:text><number:day number:style=\"long\"/></number:date-style>";

    /// <summary>The default date and time data style, named <paramref name="name"/>.</summary>
    public static string DateTimeDataStyle(string name) =>
        $"<number:date-style style:name=\"{name}\"><number:year number:style=\"long\"/><number:text>-</number:text>"
        + "<number:month number:style=\"long\"/><number:text>-</number:text><number:day number:style=\"long\"/>"
        + "<number:text> </number:text><number:hours number:style=\"long\"/><number:text>:</number:text>"
        + "<number:minutes number:style=\"long\"/><number:text>:</number:text><number:seconds number:style=\"long\"/></number:date-style>";

    private static string BuildContentStart()
    {
        StringBuilder xml = new(XmlDeclaration + "<office:document-content" + Namespaces + "><office:automatic-styles>");

        for (int chars = 1; chars <= WidestColumn; chars++)
        {
            // Excel's measure, which WriteColumn's width is in: a character is 7 pixels, plus 5 of
            // padding, at 96 pixels to the inch.
            double inches = ((chars * 7) + 5) / 96d;
            xml.Append(CultureInfo.InvariantCulture, $"<style:style style:name=\"{ColumnStyleNames[chars]}\" style:family=\"table-column\"><style:table-column-properties style:column-width=\"{inches:0.####}in\"/></style:style>");
        }

        xml.Append(
            DateDataStyle("N1")
            + DateTimeDataStyle("N2")
            + "<number:boolean-style style:name=\"N3\"><number:boolean/></number:boolean-style>"
            + "<style:style style:name=\"" + DateStyle + "\" style:family=\"table-cell\" style:data-style-name=\"N1\"/>"
            + "<style:style style:name=\"" + DateTimeStyle + "\" style:family=\"table-cell\" style:data-style-name=\"N2\"/>"
            + "<style:style style:name=\"" + BooleanStyle + "\" style:family=\"table-cell\" style:data-style-name=\"N3\"/>"
            + "</office:automatic-styles><office:body><office:spreadsheet>");

        return xml.ToString();
    }
}
