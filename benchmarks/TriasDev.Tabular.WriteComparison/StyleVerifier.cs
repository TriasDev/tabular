using System.Globalization;
using System.IO.Compression;
using System.Xml;
using System.Xml.Linq;

namespace TriasDev.Tabular.WriteComparison;

/// <summary>
/// Checks that a styled scenario's file carries the styles it was asked for: the header's fill, the legend
/// fill of each double by its value, and the number and date formats. Cheap: the style tables and the first
/// data row only.
/// </summary>
/// <remarks>
/// A cell's look is read the way a spreadsheet would resolve it: xlsx cell, to its <c>xf</c>, to the fill
/// and number format; ods cell, to its named style, to the background colour and the data style. A writer
/// that reports itself styled and ignores the styles therefore fails here, rather than being timed on less work.
/// </remarks>
internal static class StyleVerifier
{
    private const int ColumnsChecked = 30;

    private static readonly XNamespace Main = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private static readonly XNamespace Style = "urn:oasis:names:tc:opendocument:xmlns:style:1.0";
    private static readonly XNamespace Fo = "urn:oasis:names:tc:opendocument:xmlns:xsl-fo-compatible:1.0";
    private static readonly XNamespace Number = "urn:oasis:names:tc:opendocument:xmlns:datastyle:1.0";

    /// <summary>The built-in number formats the specification fixes to one code in every locale (ids 1 to 4); a writer may reference them instead of declaring the code.</summary>
    private static readonly Dictionary<int, string> BuiltInFormats = new() { [1] = "0", [2] = "0.00", [3] = "#,##0", [4] = "#,##0.00" };

    private const string TableNamespace = "urn:oasis:names:tc:opendocument:xmlns:table:1.0";

    /// <summary>What a cell looks like: its fill as RRGGBB and its number or date format code, either null.</summary>
    private readonly record struct Look(string? Fill, string? Format);

    /// <summary>Null when the styles are there; otherwise what is missing.</summary>
    public static string? Check(Scenario scenario, string path)
    {
        if (!scenario.Styled || scenario.Kind is not (FileKind.Xlsx or FileKind.Ods))
        {
            return null;
        }

        using ZipArchive zip = ZipFile.OpenRead(path);
        Func<int, int, Look> look = scenario.Kind == FileKind.Xlsx ? XlsxLooks(zip) : OdsLooks(zip);

        return Expect(look(0, 0).Fill, Styles.HeaderFill.ToString("X6", CultureInfo.InvariantCulture), "the header's fill", "header cell A")
            ?? CheckFirstRow(scenario.Dataset, look);
    }

    private static string? CheckFirstRow(IDataset dataset, Func<int, int, Look> look)
    {
        Datum[] first = new Datum[dataset.Columns.Length];
        dataset.Fill(0, first);

        return Enumerable.Range(0, Math.Min(ColumnsChecked, first.Length))
            .Select(c => CheckCell(first[c], dataset.Columns[c], look(1, c)))
            .FirstOrDefault(problem => problem is not null);
    }

    private static string? CheckCell(Datum value, DataColumn column, Look cell) => value.Kind switch
    {
        ValueKind.Double => Expect(cell.Fill, Styles.LegendFills[Styles.Legend(value.Double)].ToString("X6", CultureInfo.InvariantCulture), "a double's legend fill", column.Header),
        ValueKind.Decimal => Expect(cell.Format, Styles.DecimalFormat, "a decimal's number format", column.Header),
        ValueKind.Date => Expect(cell.Format, Styles.DateFormat, "a date's format", column.Header),
        ValueKind.DateTime => Expect(cell.Format, Styles.DateTimeFormat, "a date-time's format", column.Header),
        _ => null,
    };

    private static string? Expect(string? read, string expected, string what, string where) =>
        string.Equals(read, expected, StringComparison.OrdinalIgnoreCase) ? null : $"style of {where}: {what} should be {expected}, read {read ?? "none"}";

    /// <summary>Format codes are compared without the escaping a writer adds around literals.</summary>
    private static string Plain(string code) => code.Replace("\\", string.Empty, StringComparison.Ordinal).Replace("\"", string.Empty, StringComparison.Ordinal);

    // ---- xlsx

    private static Func<int, int, Look> XlsxLooks(ZipArchive zip)
    {
        XDocument styles = Load(zip, "xl/styles.xml");
        Dictionary<int, string> formats = new(BuiltInFormats);

        foreach (XElement declared in styles.Descendants(Main + "numFmt"))
        {
            formats[(int)declared.Attribute("numFmtId")!] = Plain((string)declared.Attribute("formatCode")!);
        }

        List<string?> fills = [.. styles.Descendants(Main + "fills").Elements(Main + "fill").Select(FillOf)];
        List<Look> xfs = [.. styles.Descendants(Main + "cellXfs").Elements(Main + "xf")
            .Select(x => new Look(
                fills.ElementAtOrDefault((int?)x.Attribute("fillId") ?? 0),
                formats.GetValueOrDefault((int?)x.Attribute("numFmtId") ?? 0)))];

        Dictionary<(int Row, int Column), int> cells = XlsxCellStyles(zip);

        return (row, column) => cells.TryGetValue((row, column), out int s) && s < xfs.Count ? xfs[s] : default;
    }

    private static string? FillOf(XElement fill) =>
        fill.Descendants(Main + "fgColor").FirstOrDefault()?.Attribute("rgb")?.Value is { Length: >= 6 } rgb ? rgb[^6..] : null;

    /// <summary>The style index of each styled cell in the first two rows of the first sheet.</summary>
    private static Dictionary<(int Row, int Column), int> XlsxCellStyles(ZipArchive zip)
    {
        string sheet = zip.Entries.Select(e => e.FullName).Where(n => n.StartsWith("xl/worksheets/sheet", StringComparison.Ordinal)).Order(StringComparer.Ordinal).First();
        Dictionary<(int Row, int Column), int> cells = [];

        using Stream stream = zip.GetEntry(sheet)!.Open();
        using XmlReader reader = XmlReader.Create(stream);
        int row = -1;
        int column = -1;

        while (row <= 1 && reader.Read())
        {
            if (reader.NodeType != XmlNodeType.Element)
            {
                continue;
            }

            // The row and column attributes are optional: a cell without one follows the previous.
            if (reader.LocalName == "row")
            {
                row = int.TryParse(reader.GetAttribute("r"), CultureInfo.InvariantCulture, out int rowNumber) ? rowNumber - 1 : row + 1;
                column = -1;
            }
            else if (reader.LocalName == "c")
            {
                column = reader.GetAttribute("r") is { } reference ? Split(reference).Column : column + 1;
                RecordStyle(cells, reader, row, column);
            }
        }

        return cells;
    }

    private static void RecordStyle(Dictionary<(int Row, int Column), int> cells, XmlReader cell, int row, int column)
    {
        if (row <= 1 && int.TryParse(cell.GetAttribute("s"), CultureInfo.InvariantCulture, out int style))
        {
            cells[(row, column)] = style;
        }
    }

    /// <summary>"AB12" as (row 11, column 27), both 0-based.</summary>
    private static (int Row, int Column) Split(string reference)
    {
        int i = 0;
        int column = 0;

        while (i < reference.Length && char.IsAsciiLetter(reference[i]))
        {
            column = (column * 26) + (char.ToUpperInvariant(reference[i]) - 'A' + 1);
            i++;
        }

        return (int.Parse(reference.AsSpan(i), CultureInfo.InvariantCulture) - 1, column - 1);
    }

    // ---- ods

    private static Func<int, int, Look> OdsLooks(ZipArchive zip)
    {
        XDocument styles = Load(zip, "styles.xml");
        Dictionary<string, string> codes = [];

        foreach (XElement data in styles.Descendants().Where(e => e.Name.Namespace == Number && e.Name.LocalName is "number-style" or "date-style"))
        {
            codes[(string)data.Attribute(Style + "name")!] = data.Name.LocalName == "date-style" ? DateCode(data) : NumberCode(data);
        }

        Dictionary<string, Look> named = [];

        foreach (XElement style in styles.Descendants(Style + "style").Where(e => (string?)e.Attribute(Style + "family") == "table-cell"))
        {
            string? background = style.Element(Style + "table-cell-properties")?.Attribute(Fo + "background-color")?.Value;
            string? data = (string?)style.Attribute(Style + "data-style-name");

            named[(string)style.Attribute(Style + "name")!] = new Look(background?.TrimStart('#'), data is not null ? codes.GetValueOrDefault(data) : null);
        }

        Dictionary<(int Row, int Column), string> cells = OdsCellStyles(zip);

        return (row, col) => cells.TryGetValue((row, col), out string? n) && named.TryGetValue(n, out Look l) ? l : default;
    }

    /// <summary>The style name of each styled cell in the first two rows of the first table, columns counted through repeats.</summary>
    private static Dictionary<(int Row, int Column), string> OdsCellStyles(ZipArchive zip)
    {
        Dictionary<(int Row, int Column), string> cells = [];
        using Stream stream = zip.GetEntry("content.xml")!.Open();
        using XmlReader reader = XmlReader.Create(stream);
        int rowIndex = -1;
        int column = 0;

        while (rowIndex <= 1 && reader.Read())
        {
            if (reader.NodeType != XmlNodeType.Element || reader.NamespaceURI != TableNamespace)
            {
                continue;
            }

            if (reader.LocalName == "table-row")
            {
                rowIndex++;
                column = 0;
            }
            else if (reader.LocalName is "table-cell" or "covered-table-cell")
            {
                if (reader.GetAttribute("style-name", TableNamespace) is { } name)
                {
                    cells[(rowIndex, column)] = name;
                }

                column += Repeat(reader);
            }
        }

        return cells;
    }

    private static int Repeat(XmlReader cell) =>
        int.TryParse(cell.GetAttribute("number-columns-repeated", TableNamespace), CultureInfo.InvariantCulture, out int repeat) ? repeat : 1;

    /// <summary>A date-style's parts back as a code: <c>dd/mm/yyyy</c>.</summary>
    private static string DateCode(XElement style) =>
        string.Concat(style.Elements().Select(part => part.Name.LocalName switch
        {
            "text" => part.Value,
            "day" => "dd",
            "month" => "mm",
            "year" => "yyyy",
            "hours" => "hh",
            "minutes" => "mm",
            "seconds" => "ss",
            _ => "?",
        }));

    /// <summary>A number-style as a code: <c>#,##0.00</c>.</summary>
    private static string NumberCode(XElement style)
    {
        XElement? number = style.Element(Number + "number");
        int places = (int?)number?.Attribute(Number + "decimal-places") ?? 0;
        bool grouping = (string?)number?.Attribute(Number + "grouping") == "true";

        return (grouping ? "#,##0" : "0") + (places > 0 ? "." + new string('0', places) : string.Empty);
    }

    private static XDocument Load(ZipArchive zip, string name)
    {
        using Stream stream = zip.GetEntry(name)?.Open() ?? throw new InvalidDataException($"the file has no {name}");
        return XDocument.Load(stream, LoadOptions.PreserveWhitespace);
    }
}
