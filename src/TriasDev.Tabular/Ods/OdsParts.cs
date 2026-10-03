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

    public const string ContentEnd = "</table:table></office:spreadsheet></office:body></office:document-content>";

    private const string XmlDeclaration = "<?xml version=\"1.0\" encoding=\"UTF-8\"?>";

    private const string Namespaces =
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

    public static readonly byte[] Manifest = Encoding.UTF8.GetBytes(
        XmlDeclaration
        + "<manifest:manifest xmlns:manifest=\"urn:oasis:names:tc:opendocument:xmlns:manifest:1.0\" manifest:version=\"1.3\">"
        + "<manifest:file-entry manifest:full-path=\"/\" manifest:version=\"1.3\" manifest:media-type=\"" + Mimetype + "\"/>"
        + "<manifest:file-entry manifest:full-path=\"content.xml\" manifest:media-type=\"text/xml\"/>"
        + "<manifest:file-entry manifest:full-path=\"styles.xml\" manifest:media-type=\"text/xml\"/>"
        + "</manifest:manifest>");

    public static readonly byte[] Styles = Encoding.UTF8.GetBytes(
        XmlDeclaration + "<office:document-styles" + Namespaces + "><office:styles/></office:document-styles>");

    /// <summary>The name of the column style for a width of so many whole characters.</summary>
    public static string ColumnStyleName(int chars) => ColumnStyleNames[chars];

    /// <summary>The column style for a width in characters, rounded to the nearest whole one.</summary>
    public static string ColumnStyleFor(double width) =>
        ColumnStyleNames[Math.Clamp((int)Math.Round(width, MidpointRounding.AwayFromZero), 1, WidestColumn)];

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
            "<number:date-style style:name=\"N1\"><number:year number:style=\"long\"/><number:text>-</number:text>"
            + "<number:month number:style=\"long\"/><number:text>-</number:text><number:day number:style=\"long\"/></number:date-style>"
            + "<number:date-style style:name=\"N2\"><number:year number:style=\"long\"/><number:text>-</number:text>"
            + "<number:month number:style=\"long\"/><number:text>-</number:text><number:day number:style=\"long\"/>"
            + "<number:text> </number:text><number:hours number:style=\"long\"/><number:text>:</number:text>"
            + "<number:minutes number:style=\"long\"/><number:text>:</number:text><number:seconds number:style=\"long\"/></number:date-style>"
            + "<number:boolean-style style:name=\"N3\"><number:boolean/></number:boolean-style>"
            + "<style:style style:name=\"" + DateStyle + "\" style:family=\"table-cell\" style:data-style-name=\"N1\"/>"
            + "<style:style style:name=\"" + DateTimeStyle + "\" style:family=\"table-cell\" style:data-style-name=\"N2\"/>"
            + "<style:style style:name=\"" + BooleanStyle + "\" style:family=\"table-cell\" style:data-style-name=\"N3\"/>"
            + "</office:automatic-styles><office:body><office:spreadsheet>");

        return xml.ToString();
    }
}
