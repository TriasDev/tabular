using System.Globalization;
using System.IO.Compression;
using System.Xml;

namespace TriasDev.Tabular.Tests.Spike;

/// <summary>
/// Reads xlsx with nothing but the base class library: <see cref="ZipArchive"/> for the package and
/// <see cref="XmlReader"/> for the parts.
/// </summary>
/// <remarks>
/// This is the prototype of the cursor the library will own, and it is in the race for a reason
/// beyond speed: a reader with no third-party dependency at all makes the library a folder that can
/// be copied into another solution, which is one of the stated goals. What it costs is that every
/// edge — the date epoch, custom number formats, inline strings, sparse rows — has to be handled
/// here rather than inherited from someone else's library. The fixtures are what say whether that
/// cost was paid correctly.
/// </remarks>
public sealed class BclXlsxCandidate : IParserCandidate
{
    public string Name => "Own cursor, xlsx (BCL only)";

    public CandidateFormats Formats => CandidateFormats.Xlsx;

    public IEnumerable<IReadOnlyList<string?>> Rows(Stream stream)
    {
        using ZipArchive package = new(stream, ZipArchiveMode.Read, leaveOpen: true);

        bool date1904 = ReadDate1904(package);
        string[] sharedStrings = ReadSharedStrings(package);
        bool[] styleIsDate = ReadDateStyles(package);
        string sheetPath = FirstSheetPath(package);

        // Enumerated here rather than returned: this method is an iterator, so the `using` above
        // stays open for as long as the caller is reading. Returning the inner enumerable directly
        // would close the archive before the first row was ever pulled.
        foreach (IReadOnlyList<string?> row in ReadSheet(package, sheetPath, sharedStrings, styleIsDate, date1904))
        {
            yield return row;
        }
    }

    private static ZipArchiveEntry? Part(ZipArchive package, string path) =>
        package.GetEntry(path);

    private static bool ReadDate1904(ZipArchive package)
    {
        ZipArchiveEntry? workbook = Part(package, "xl/workbook.xml");

        if (workbook is null)
        {
            return false;
        }

        using Stream stream = workbook.Open();
        using XmlReader reader = XmlReader.Create(stream);

        while (reader.Read())
        {
            if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "workbookPr")
            {
                string? value = reader.GetAttribute("date1904");
                return value is "1" or "true";
            }

            if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "sheets")
            {
                break;      // workbookPr precedes sheets; past it there is nothing to find
            }
        }

        return false;
    }

    /// <summary>
    /// Reads the shared string table. Every <c>&lt;t&gt;</c> inside an item is concatenated, which is
    /// what makes a rich-text string built from several runs come back whole instead of empty.
    /// </summary>
    private static string[] ReadSharedStrings(ZipArchive package)
    {
        ZipArchiveEntry? part = Part(package, "xl/sharedStrings.xml");

        if (part is null)
        {
            return [];
        }

        List<string> values = [];

        using Stream stream = part.Open();
        using XmlReader reader = XmlReader.Create(stream);

        while (reader.Read())
        {
            if (reader.NodeType != XmlNodeType.Element || reader.LocalName != "si")
            {
                continue;
            }

            using XmlReader item = reader.ReadSubtree();
            values.Add(ConcatenateText(item));
        }

        return values.ToArray();
    }

    private static string ConcatenateText(XmlReader reader)
    {
        System.Text.StringBuilder text = new();

        while (reader.Read())
        {
            if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "t")
            {
                text.Append(reader.ReadElementContentAsString());
            }
        }

        return text.ToString();
    }

    /// <summary>
    /// Builds, per style index, whether that style formats its number as a date. A cell carries a
    /// bare serial either way; the style is the only thing that distinguishes 44927 the date from
    /// 44927 the quantity.
    /// </summary>
    private static bool[] ReadDateStyles(ZipArchive package)
    {
        ZipArchiveEntry? part = Part(package, "xl/styles.xml");

        if (part is null)
        {
            return [];
        }

        Dictionary<int, string> customFormats = [];
        List<int> cellFormatIds = [];

        using (Stream stream = part.Open())
        using (XmlReader reader = XmlReader.Create(stream))
        {
            bool inCellXfs = false;

            while (reader.Read())
            {
                if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "numFmt")
                {
                    string? id = reader.GetAttribute("numFmtId");
                    string? code = reader.GetAttribute("formatCode");

                    if (id is not null && code is not null && int.TryParse(id, CultureInfo.InvariantCulture, out int numFmtId))
                    {
                        customFormats[numFmtId] = code;
                    }
                }
                else if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "cellXfs")
                {
                    inCellXfs = true;
                }
                else if (reader.NodeType == XmlNodeType.EndElement && reader.LocalName == "cellXfs")
                {
                    inCellXfs = false;
                }
                else if (inCellXfs && reader.NodeType == XmlNodeType.Element && reader.LocalName == "xf")
                {
                    string? id = reader.GetAttribute("numFmtId");
                    cellFormatIds.Add(id is not null && int.TryParse(id, CultureInfo.InvariantCulture, out int numFmtId) ? numFmtId : 0);
                }
            }
        }

        bool[] isDate = new bool[cellFormatIds.Count];

        for (int i = 0; i < cellFormatIds.Count; i++)
        {
            int id = cellFormatIds[i];
            isDate[i] = IsBuiltInDateFormat(id)
                || (customFormats.TryGetValue(id, out string? code) && LooksLikeDateFormat(code));
        }

        return isDate;
    }

    /// <summary>The number format identifiers the specification reserves for dates and times.</summary>
    private static bool IsBuiltInDateFormat(int numFmtId) =>
        numFmtId is (>= 14 and <= 22) or (>= 45 and <= 47);

    /// <summary>
    /// Decides whether a custom format code renders a date, by looking for date tokens outside the
    /// parts of the code that are literal text: quoted runs, bracketed sections, and escaped
    /// characters. Without that exclusion a currency format such as <c>#,##0 "Dm"</c> reads as a date
    /// because of its letter d.
    /// </summary>
    private static bool LooksLikeDateFormat(string code)
    {
        bool inQuotes = false;
        bool inBrackets = false;

        for (int i = 0; i < code.Length; i++)
        {
            char c = code[i];

            switch (c)
            {
                case '"':
                    inQuotes = !inQuotes;
                    continue;
                case '[':
                    inBrackets = true;
                    continue;
                case ']':
                    inBrackets = false;
                    continue;
                case '\\':
#pragma warning disable S127 // skipping the escaped character is the point of the escape
                    i++;                        // the next character is literal
#pragma warning restore S127
                    continue;
            }

            if (inQuotes || inBrackets)
            {
                continue;
            }

            if (c is 'y' or 'Y' or 'd' or 'D' or 'h' or 'H' or 's' or 'S' or 'm' or 'M')
            {
                return true;
            }
        }

        return false;
    }

    private static string FirstSheetPath(ZipArchive package)
    {
        return package.Entries
            .FirstOrDefault(entry => entry.FullName.StartsWith("xl/worksheets/sheet", StringComparison.OrdinalIgnoreCase))
            ?.FullName
            ?? throw new InvalidDataException("The package holds no worksheet.");
    }

    private static IEnumerable<IReadOnlyList<string?>> ReadSheet(
        ZipArchive package,
        string sheetPath,
        string[] sharedStrings,
        bool[] styleIsDate,
        bool date1904)
    {
        ZipArchiveEntry part = Part(package, sheetPath)
            ?? throw new InvalidDataException($"The worksheet part {sheetPath} is missing.");

        using Stream sheetStream = part.Open();
        using XmlReader reader = XmlReader.Create(sheetStream);

        List<string?> cells = [];

        while (reader.Read())
        {
            if (reader.NodeType != XmlNodeType.Element || reader.LocalName != "row")
            {
                continue;
            }

            using XmlReader row = reader.ReadSubtree();
            ReadRow(row, cells, sharedStrings, styleIsDate, date1904);
            yield return cells;
        }
    }

    private static void ReadRow(
        XmlReader row,
        List<string?> cells,
        string[] sharedStrings,
        bool[] styleIsDate,
        bool date1904)
    {
        cells.Clear();

        while (row.Read())
        {
            if (row.NodeType != XmlNodeType.Element || row.LocalName != "c")
            {
                continue;
            }

            string? reference = row.GetAttribute("r");
            string? type = row.GetAttribute("t");
            string? style = row.GetAttribute("s");

            // The cell's position comes from its reference, never from its order among siblings:
            // a row may skip columns and may start past column A.
            int index = reference is null ? cells.Count : ColumnIndex(reference);

            while (cells.Count < index)
            {
                cells.Add(CellNormalization.Empty);
            }

            using XmlReader cell = row.ReadSubtree();
            cells.Add(ReadCell(cell, type, style, sharedStrings, styleIsDate, date1904));
        }
    }

    private static string? ReadCell(
        XmlReader cell,
        string? type,
        string? style,
        string[] sharedStrings,
        bool[] styleIsDate,
        bool date1904)
    {
        string? value = null;
        string? inline = null;

        while (cell.Read())
        {
            if (cell.NodeType != XmlNodeType.Element)
            {
                continue;
            }

            if (cell.LocalName == "v")
            {
                value = cell.ReadElementContentAsString();
            }
            else if (cell.LocalName == "is")
            {
                using XmlReader item = cell.ReadSubtree();
                inline = ConcatenateText(item);
            }
        }

        if (inline is not null)
        {
            return CellNormalization.Text(inline);
        }

        if (value is null)
        {
            return CellNormalization.Empty;
        }

        switch (type)
        {
            case "s":
                return int.TryParse(value, CultureInfo.InvariantCulture, out int index) && index < sharedStrings.Length
                    ? CellNormalization.Text(sharedStrings[index])
                    : CellNormalization.Empty;
            case "b":
                return CellNormalization.Boolean(value == "1");
            case "str":
            case "inlineStr":
                return CellNormalization.Text(value);
            case "e":
                return CellNormalization.Text(value);
        }

        if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double number))
        {
            return CellNormalization.Text(value);
        }

        if (style is not null
            && int.TryParse(style, CultureInfo.InvariantCulture, out int styleIndex)
            && styleIndex < styleIsDate.Length
            && styleIsDate[styleIndex])
        {
            return CellNormalization.Date(FromSerial(number, date1904));
        }

        return CellNormalization.Number(number);
    }

    /// <summary>
    /// Converts an Excel serial number to a date.
    /// </summary>
    /// <remarks>
    /// Under the 1900 system the epoch is nominally 1900-01-01, but Excel treats the non-existent
    /// 29 February 1900 as real, so every serial above 60 is one day further from the epoch than
    /// arithmetic suggests — hence 1899-12-30 as the base. Below 60 the bug has not yet applied.
    /// The 1904 system has no such quirk.
    /// </remarks>
    private static DateTime FromSerial(double serial, bool date1904)
    {
        if (date1904)
        {
            return new DateTime(1904, 1, 1, 0, 0, 0, DateTimeKind.Unspecified).AddDays(serial);
        }

        return serial < 60
            ? new DateTime(1899, 12, 31, 0, 0, 0, DateTimeKind.Unspecified).AddDays(serial - 1)
            : new DateTime(1899, 12, 30, 0, 0, 0, DateTimeKind.Unspecified).AddDays(serial);
    }

    /// <summary>Turns a cell reference such as <c>AB12</c> into the zero-based column index.</summary>
    private static int ColumnIndex(string reference)
    {
        int index = 0;

        foreach (char c in reference)
        {
            if (c is >= 'A' and <= 'Z')
            {
                index = (index * 26) + (c - 'A' + 1);
            }
            else if (c is >= 'a' and <= 'z')
            {
                index = (index * 26) + (c - 'a' + 1);
            }
            else
            {
                break;
            }
        }

        return index - 1;
    }
}
