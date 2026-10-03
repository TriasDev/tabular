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

    /// <summary>
    /// Four cell formats, by index: 0 General, 1 a date, 2 a date and time, 3 an integer (format
    /// <c>0</c>, so an id of twelve digits does not show as <c>1.23457E+11</c>). The reader takes
    /// formats 14 and 164 for dates, 0 and 1 for numbers.
    /// </summary>
    public static readonly byte[] Styles = Encoding.UTF8.GetBytes(
        XmlDeclaration
        + "<styleSheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">"
        + "<numFmts count=\"1\"><numFmt numFmtId=\"164\" formatCode=\"yyyy\\-mm\\-dd\\ hh:mm:ss\"/></numFmts>"
        + "<fonts count=\"1\"><font><sz val=\"11\"/><name val=\"Calibri\"/></font></fonts>"
        + "<fills count=\"2\"><fill><patternFill patternType=\"none\"/></fill><fill><patternFill patternType=\"gray125\"/></fill></fills>"
        + "<borders count=\"1\"><border><left/><right/><top/><bottom/><diagonal/></border></borders>"
        + "<cellStyleXfs count=\"1\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\"/></cellStyleXfs>"
        + "<cellXfs count=\"4\">"
        + "<xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\"/>"
        + "<xf numFmtId=\"14\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyNumberFormat=\"1\"/>"
        + "<xf numFmtId=\"164\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyNumberFormat=\"1\"/>"
        + "<xf numFmtId=\"1\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyNumberFormat=\"1\"/>"
        + "</cellXfs>"
        + "<cellStyles count=\"1\"><cellStyle name=\"Normal\" xfId=\"0\" builtinId=\"0\"/></cellStyles>"
        + "</styleSheet>");

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
