using System.Globalization;
using System.Text;

namespace TriasDev.Tabular.Xlsx;

/// <summary>The parts of a workbook package besides its sheets — small, and written once at the end.</summary>
internal static class XlsxParts
{
    private const string XmlDeclaration = "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>";

    public const string WorksheetStart =
        XmlDeclaration
        + "<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" "
        + "xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\">";

    public const string WorksheetEnd = "</sheetData></worksheet>";

    public static readonly byte[] PackageRelationships = Encoding.UTF8.GetBytes(
        XmlDeclaration
        + "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">"
        + "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/>"
        + "</Relationships>");

    public static string SheetPath(int number) => string.Create(CultureInfo.InvariantCulture, $"xl/worksheets/sheet{number}.xml");

    public static byte[] ContentTypes(int sheets)
    {
        StringBuilder xml = new(
            XmlDeclaration
            + "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">"
            + "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>"
            + "<Default Extension=\"xml\" ContentType=\"application/xml\"/>"
            + "<Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/>"
            + "<Override PartName=\"/xl/styles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml\"/>");

        for (int i = 1; i <= sheets; i++)
        {
            xml.Append(CultureInfo.InvariantCulture, $"<Override PartName=\"/{SheetPath(i)}\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/>");
        }

        return Encoding.UTF8.GetBytes(xml.Append("</Types>").ToString());
    }

    public static byte[] Workbook(IReadOnlyList<string> sheetNames)
    {
        StringBuilder xml = new(
            XmlDeclaration
            + "<workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" "
            + "xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><sheets>");

        for (int i = 0; i < sheetNames.Count; i++)
        {
            xml.Append("<sheet name=\"");
            AppendEscaped(xml, sheetNames[i]);
            xml.Append(CultureInfo.InvariantCulture, $"\" sheetId=\"{i + 1}\" r:id=\"rId{i + 1}\"/>");
        }

        return Encoding.UTF8.GetBytes(xml.Append("</sheets></workbook>").ToString());
    }

    public static byte[] WorkbookRelationships(int sheets)
    {
        StringBuilder xml = new(
            XmlDeclaration
            + "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">");

        for (int i = 1; i <= sheets; i++)
        {
            xml.Append(CultureInfo.InvariantCulture, $"<Relationship Id=\"rId{i}\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet{i}.xml\"/>");
        }

        xml.Append(CultureInfo.InvariantCulture, $"<Relationship Id=\"rId{sheets + 1}\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles\" Target=\"styles.xml\"/>");
        return Encoding.UTF8.GetBytes(xml.Append("</Relationships>").ToString());
    }

    /// <summary>The column's letters: 0 → A, 25 → Z, 26 → AA, 16,383 → XFD.</summary>
    public static string ColumnName(int index)
    {
        Span<char> letters = stackalloc char[3];
        int at = letters.Length;

        for (int n = index + 1; n > 0; n = (n - 1) / 26)
        {
            letters[--at] = (char)('A' + ((n - 1) % 26));
        }

        return new string(letters[at..]);
    }

    private static void AppendEscaped(StringBuilder xml, string value)
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
}
