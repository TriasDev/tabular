using System.Buffers;
using System.Globalization;

namespace TriasDev.Tabular.Xlsx;

/// <summary>
/// Writes an xlsx workbook: each sheet one deflated zip entry written row by row, the workbook's
/// other parts once at the end.
/// </summary>
/// <remarks>
/// <para>
/// Text is written inline (<c>t="inlineStr"</c>), never into a shared-string table, which would hold
/// every distinct string in memory until the end. Every row and every cell carries its reference,
/// so an empty cell is simply left out and an empty row still has its number.
/// </para>
/// <para>
/// A carriage return is written as <c>_x000D_</c>: the reader turns a literal one into a line feed,
/// as XML does. An underscore that starts <c>_x</c> is written as <c>_x005F_</c>, so text that
/// happens to look like an escape is not decoded into something else.
/// </para>
/// </remarks>
internal sealed class XlsxSheetWriter : ISheetWriter
{
    /// <summary>The most characters a cell holds.</summary>
    public const int MaxTextChars = 32_767;

    /// <summary>Three bytes a character is what UTF-8 may take, so a row at the row buffer's cap does not reallocate.</summary>
    private const int RetainedBytes = 3 * RowText.RetainedChars;

    private const string ValueEnd = "</v></c>";
    private const int DateStyle = 1;
    private const int DateTimeStyle = 2;
    private const int IntegerStyle = 3;

    /// <summary>The first day a workbook holds, as the reader reads serials.</summary>
    private static readonly DateTime FirstDay = new(1900, 1, 1, 0, 0, 0, DateTimeKind.Unspecified);

    /// <summary>The first day after Excel's phantom 29 February 1900, from which serials count from 30 December 1899.</summary>
    private static readonly DateTime AfterLeapBug = new(1900, 3, 1, 0, 0, 0, DateTimeKind.Unspecified);

    private static readonly long EarlyEpochTicks = new DateTime(1899, 12, 31, 0, 0, 0, DateTimeKind.Unspecified).Ticks;

    private static readonly long EpochTicks = new DateTime(1899, 12, 30, 0, 0, 0, DateTimeKind.Unspecified).Ticks;

    private static readonly SearchValues<char> NeedsEscape = SearchValues.Create("&<>\r_");

    private readonly ZipWriter _zip;
    private readonly List<string> _sheetNames = [];
    private readonly RowText _row = new();
    private ArrayBufferWriter<byte> _bytes = new(16 * 1024);
    private Stream? _sheet;
    private string[] _columnNames = [];
    private long _rowNumber;
    private int _column;

    public XlsxSheetWriter(SpillBuffer output, XlsxWriterOptions options)
    {
        _zip = new ZipWriter(output, options.CompressionLevel);
    }

    /// <summary>
    /// Releases the open sheet entry's deflate state, writing no descriptor: the file is abandoned.
    /// After <see cref="Complete"/> nothing is open and this does nothing.
    /// </summary>
    public void Dispose()
    {
        _sheet?.Dispose();
        _sheet = null;
    }

    public long MaxRows => 1_048_576;

    public bool AllowsSeveralSheets => true;

    public bool NamesSheets => true;

    public void BeginSheet(string name, ReadOnlySpan<WriteColumn> columns)
    {
        CloseSheet();
        _sheetNames.Add(name);
        _sheet = _zip.BeginDeflated(XlsxParts.SheetPath(_sheetNames.Count));
        _columnNames = new string[columns.Length];

        for (int i = 0; i < columns.Length; i++)
        {
            _columnNames[i] = XlsxParts.ColumnName(i);
        }

        _rowNumber = 0;
        _row.Clear();
        _row.Append(XlsxParts.WorksheetStart);
        AppendWidths(columns);
        _row.Append("<sheetData>");
        Emit();
    }

    public void BeginRow()
    {
        _rowNumber++;
        _column = 0;
        _row.Clear();
        _row.Append("<row r=\"");
        _row.AppendFormatted(_rowNumber, default, CultureInfo.InvariantCulture);
        _row.Append("\">");
    }

    public string? WriteHeader(string value) => WriteInline(value);

    public string? WriteText(string value, int column) => WriteInline(value);

    public string? WriteLong(long value)
    {
        if (ValueChecks.LongInDouble(value) is { } code)
        {
            return code;
        }

        WriteNumber(IntegerStyle);
        _row.AppendFormatted(value, default, CultureInfo.InvariantCulture);
        _row.Append(ValueEnd);
        return null;
    }

    public string? WriteDecimal(decimal value)
    {
        if (ValueChecks.DecimalInDouble(value) is { } code)
        {
            return code;
        }

        WriteNumber(style: 0);
        _row.AppendFormatted(value, default, CultureInfo.InvariantCulture);
        _row.Append(ValueEnd);
        return null;
    }

    public string? WriteDouble(double value)
    {
        WriteNumber(style: 0);
        _row.AppendFormatted(value, "R", CultureInfo.InvariantCulture);
        _row.Append(ValueEnd);
        return null;
    }

    public string? WriteDate(DateTime value, bool hasTime)
    {
        if (value < FirstDay)
        {
            return ErrorCodes.Write.DateOutOfRange;
        }

        WriteNumber(hasTime ? DateTimeStyle : DateStyle);
        _row.AppendFormatted(Serial(value), "R", CultureInfo.InvariantCulture);
        _row.Append(ValueEnd);
        return null;
    }

    public void WriteBoolean(bool value)
    {
        StartCell();
        _row.Append(" t=\"b\"><v>");
        _row.Append(value ? '1' : '0');
        _row.Append(ValueEnd);
    }

    public void WriteEmpty() => _column++;

    public void EndRow()
    {
        _row.Append("</row>");
        Emit();
    }

    public void Complete()
    {
        CloseSheet();
        _zip.AddStored("[Content_Types].xml", XlsxParts.ContentTypes(_sheetNames.Count));
        _zip.AddStored("_rels/.rels", XlsxParts.PackageRelationships);
        _zip.AddStored("xl/workbook.xml", XlsxParts.Workbook(_sheetNames));
        _zip.AddStored("xl/_rels/workbook.xml.rels", XlsxParts.WorkbookRelationships(_sheetNames.Count));
        _zip.AddStored("xl/styles.xml", XlsxParts.Styles);
        _zip.Complete();
    }

    private string? WriteInline(string value)
    {
        if (value.Length > MaxTextChars)
        {
            return ErrorCodes.Write.TextTooLong;
        }

        StartCell();
        _row.Append(" t=\"inlineStr\"><is><t");

        if (value.Length > 0 && (char.IsWhiteSpace(value[0]) || char.IsWhiteSpace(value[^1])))
        {
            _row.Append(" xml:space=\"preserve\"");
        }

        _row.Append('>');
        AppendEscaped(value);
        _row.Append("</t></is></c>");
        return null;
    }

    /// <summary>Opens a number cell in a style, up to its value.</summary>
    private void WriteNumber(int style)
    {
        StartCell();

        if (style != 0)
        {
            _row.Append(" s=\"");
            _row.Append((char)('0' + style));
            _row.Append('"');
        }

        _row.Append("><v>");
    }

    /// <summary>
    /// The workbook serial of a date already truncated to the millisecond, counted as the reader
    /// counts it: from 31 December 1899 before Excel's phantom leap day, from 30 December after it.
    /// </summary>
    /// <remarks>
    /// Milliseconds over milliseconds per day: the reader multiplies back and rounds to the
    /// millisecond, and at 2.6 × 10^14 milliseconds for 9999-12-31 the double's precision leaves
    /// that rounding exact.
    /// </remarks>
    private static double Serial(DateTime value)
    {
        long epoch = value < AfterLeapBug ? EarlyEpochTicks : EpochTicks;
        long milliseconds = (value.Ticks - epoch) / TimeSpan.TicksPerMillisecond;
        return milliseconds / 86_400_000d;
    }

    /// <summary>Opens a cell at the current column, with its reference; the caller writes the rest.</summary>
    private void StartCell()
    {
        _row.Append("<c r=\"");
        _row.Append(_columnNames[_column]);
        _row.AppendFormatted(_rowNumber, default, CultureInfo.InvariantCulture);
        _row.Append('"');
        _column++;
    }

    private void AppendEscaped(ReadOnlySpan<char> text)
    {
        while (true)
        {
            int at = text.IndexOfAny(NeedsEscape);

            if (at < 0)
            {
                _row.Append(text);
                return;
            }

            _row.Append(text[..at]);

            switch (text[at])
            {
                case '&':
                    _row.Append("&amp;");
                    break;
                case '<':
                    _row.Append("&lt;");
                    break;
                case '>':
                    _row.Append("&gt;");
                    break;
                case '\r':
                    _row.Append("_x000D_");
                    break;
                default:
                    // An underscore: escaped only where it starts "_x", which a reader would decode.
                    _row.Append(at + 1 < text.Length && text[at + 1] == 'x' ? "_x005F_" : "_");
                    break;
            }

            text = text[(at + 1)..];
        }
    }

    private void AppendWidths(ReadOnlySpan<WriteColumn> columns)
    {
        bool any = false;

        for (int i = 0; i < columns.Length; i++)
        {
            if (columns[i].Width is not { } width)
            {
                continue;
            }

            if (!any)
            {
                _row.Append("<cols>");
                any = true;
            }

            _row.Append("<col min=\"");
            _row.AppendFormatted(i + 1, default, CultureInfo.InvariantCulture);
            _row.Append("\" max=\"");
            _row.AppendFormatted(i + 1, default, CultureInfo.InvariantCulture);
            _row.Append("\" width=\"");
            _row.AppendFormatted(width, "R", CultureInfo.InvariantCulture);
            _row.Append("\" customWidth=\"1\"/>");
        }

        if (any)
        {
            _row.Append("</cols>");
        }
    }

    /// <summary>Encodes what the row buffer holds into the open sheet entry, and empties it.</summary>
    private void Emit()
    {
        _row.WriteUtf8To(_bytes);
        _sheet!.Write(_bytes.WrittenSpan);
        _bytes.ResetWrittenCount();
        _row.Clear();

        if (_bytes.Capacity > RetainedBytes)
        {
            _bytes = new ArrayBufferWriter<byte>(16 * 1024);
        }
    }

    private void CloseSheet()
    {
        if (_sheet is null)
        {
            return;
        }

        _row.Clear();
        _row.Append(XlsxParts.WorksheetEnd);
        Emit();
        _zip.EndEntry();
        _sheet = null;
    }
}
