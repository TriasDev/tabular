using System.Buffers;
using System.Globalization;

using TriasDev.Tabular.Xlsx;

namespace TriasDev.Tabular.Ods;

/// <summary>
/// Writes an OpenDocument spreadsheet: the <c>mimetype</c> first and stored, every sheet in one
/// deflated <c>content.xml</c> written row by row, the manifest and styles at the end.
/// </summary>
/// <remarks>
/// <para>
/// Text goes into paragraphs as LibreOffice shows it: a line break starts a paragraph, a tab is
/// <c>&lt;text:tab/&gt;</c>, and spaces that ODF would collapse — at the start of a paragraph, after
/// a tab, or after another space — are counted in <c>&lt;text:s/&gt;</c>.
/// </para>
/// <para>
/// A carriage return has no paragraph form that keeps it, so text holding one also states its exact
/// value in <c>office:string-value</c>, which the reader prefers over the paragraphs. Character
/// references keep the CR, LF and tab in that attribute from being normalized into spaces.
/// </para>
/// </remarks>
internal sealed class OdsSheetWriter : ISheetWriter
{
    /// <summary>Room left under the reader's token limit for markup around the text.</summary>
    private const int TokenMargin = 1024;

    private const int TagOverhead = 80;

    private const string StyleAttribute = " table:style-name=\"";

    private const string NumberEnd = "\"/>";

    private static readonly int MaxTextChars = OdsCursorOptions.Default.MaxValueChars;

    private static readonly SearchValues<char> NeedsMarkup = SearchValues.Create("&<> \t\n\r");

    private readonly ZipWriter _zip;
    private readonly OdsStyles _styles;
    private readonly RowText _row = new();
    private readonly List<(string Name, int Rows, int Columns)> _frozen = [];
    private readonly RowBytes _bytes = new();
    private readonly List<(int Sheet, string Name, int Columns, long Rows)> _filters = [];
    private Stream? _content;
    private string _name = string.Empty;
    private int _columns;
    private int _sheets;
    private long _rowNumber;
    private bool _filter;
    private string? _span;               // the pending span attributes for the next cell, or null
    private int _emptyRun;               // unstyled empty cells not yet written

    public OdsSheetWriter(SpillBuffer output, OdsWriterOptions options, StyleTable styles)
    {
        _styles = new OdsStyles(styles);
        _zip = new ZipWriter(output, options.CompressionLevel);

        // OpenDocument's rule: the mimetype is the first entry, stored, so a reader knows the file
        // from its first bytes.
        _zip.AddStored("mimetype", "application/vnd.oasis.opendocument.spreadsheet"u8);
    }

    public long MaxRows => SheetLimits.WorkbookMaxRows;

    public bool AllowsSeveralSheets => true;

    public bool NamesSheets => true;

    public string? NameProblem(string name) => null;

    public void BeginSheet(string name, ReadOnlySpan<WriteColumn> columns, SheetOptions options)
    {
        if (options.FreezeRows > 0 || options.FreezeColumns > 0)
        {
            _frozen.Add((name, options.FreezeRows, options.FreezeColumns));
        }

        RecordFilter();
        _name = name;
        _columns = columns.Length;
        _filter = options.AutoFilter;
        _rowNumber = 0;
        _sheets++;
        _row.Clear();

        if (_content is null)
        {
            _content = _zip.BeginDeflated("content.xml");
            _bytes.Begin(_content);
            _row.Append(OdsParts.ContentStart);
        }
        else
        {
            _row.Append(OdsParts.TableEnd);
        }

        _row.Append("<table:table table:name=\"");
        AppendAttribute(name);
        _row.Append("\">");
        AppendColumns(columns);
        Emit();
    }

    public void BeginRow()
    {
        _rowNumber++;
        _row.Clear();
        _row.Append("<table:table-row>");
    }

    public string? WriteHeader(string value, int style) => WriteString(value, style);

    public string? WriteText(string value, int column, int style) => WriteString(value, style);

    public string? WriteLong(long value, int style)
    {
        if (ValueChecks.LongInDouble(value) is { } code)
        {
            return code;
        }

        StartNumber(style, ValueKind.Integer);
        _row.AppendFormatted(value, default, CultureInfo.InvariantCulture);
        _row.Append(NumberEnd);
        return null;
    }

    public string? WriteDecimal(decimal value, int style)
    {
        if (ValueChecks.DecimalInDouble(value) is { } code)
        {
            return code;
        }

        StartNumber(style, ValueKind.Number);
        _row.AppendFormatted(value, default, CultureInfo.InvariantCulture);
        _row.Append(NumberEnd);
        return null;
    }

    public string? WriteDouble(double value, int style)
    {
        StartNumber(style, ValueKind.Number);
        _row.AppendFormatted(value, "R", CultureInfo.InvariantCulture);
        _row.Append(NumberEnd);
        return null;
    }

    public string? WriteDate(DateTime value, bool hasTime, int style)
    {
        // ISO in an attribute: no serial, so no 1900 floor and no leap-year bug — any year reads back.
        OpenCell();
        _row.Append(StyleAttribute);
        _row.Append(DateStyleName(style, hasTime));
        _row.Append("\" office:value-type=\"date\" office:date-value=\"");
        _row.AppendFormatted(value, hasTime ? "yyyy'-'MM'-'dd'T'HH':'mm':'ss'.'fff" : "yyyy'-'MM'-'dd", CultureInfo.InvariantCulture);
        _row.Append("\"/>");
        return null;
    }

    public void WriteBoolean(bool value, int style)
    {
        OpenCell();
        _row.Append(StyleAttribute);
        _row.Append(style == 0 ? OdsParts.BooleanStyle : _styles.Cell(style, ValueKind.Boolean).Name);
        _row.Append("\" office:value-type=\"boolean\" office:boolean-value=\"");
        _row.Append(value ? "true" : "false");
        _row.Append("\"><text:p>");
        _row.Append(value ? "TRUE" : "FALSE");
        _row.Append("</text:p></table:table-cell>");
    }

    public int MaxMerges => int.MaxValue;

    public void Merge(int rows, int columns) =>
        _span = string.Create(CultureInfo.InvariantCulture, $" table:number-columns-spanned=\"{columns}\" table:number-rows-spanned=\"{rows}\"");

    public void WriteCovered()
    {
        FlushEmpties();
        _row.Append("<table:covered-table-cell/>");
    }

    public void WriteEmpty(int style)
    {
        if (style == 0 && _span is null)
        {
            _emptyRun++;
            return;
        }

        OpenCell();

        if (style != 0)
        {
            _row.Append(StyleAttribute);
            _row.Append(_styles.Cell(style, ValueKind.Empty).Name);
            _row.Append('"');
        }

        _row.Append("/>");
    }

    public void EndRow()
    {
        FlushEmpties();
        _row.Append("</table:table-row>");
        Emit();
    }

    public void Complete()
    {
        if (_content is not null)
        {
            _row.Clear();
            RecordFilter();
            _row.Append(OdsParts.TableEnd);
            _row.Append(OdsParts.DatabaseRanges(_filters));
            _row.Append(OdsParts.SpreadsheetEnd);
            Emit();
            _bytes.Flush();
            _zip.EndEntry();
            _content = null;
        }

        _zip.AddStored("META-INF/manifest.xml", OdsParts.Manifest(settings: _frozen.Count > 0));
        _zip.AddStored("styles.xml", _styles.Build());

        if (_frozen.Count > 0)
        {
            _zip.AddStored("settings.xml", OdsParts.Settings(_frozen));
        }

        _zip.Complete();
    }

    public void Dispose()
    {
        // An abandoned spreadsheet: release the open entry's deflate state, write nothing more.
        _content?.Dispose();
        _content = null;
    }

    /// <summary>At a sheet's end: notes its filter, if it has one, for the database ranges written after the last sheet.</summary>
    private void RecordFilter()
    {
        if (_filter)
        {
            _filters.Add((_sheets - 1, _name, _columns, _rowNumber));
        }
    }

    /// <summary>Opens a value cell: writes any pending empty run, then <c>&lt;table:table-cell</c> and a pending span.</summary>
    private void OpenCell()
    {
        FlushEmpties();
        _row.Append("<table:table-cell");

        if (_span is not null)
        {
            _row.Append(_span);
            _span = null;
        }
    }

    private void FlushEmpties()
    {
        if (_emptyRun == 0)
        {
            return;
        }

        if (_emptyRun == 1)
        {
            _row.Append("<table:table-cell/>");
        }
        else
        {
            _row.Append("<table:table-cell table:number-columns-repeated=\"");
            _row.AppendFormatted(_emptyRun, default, CultureInfo.InvariantCulture);
            _row.Append("\"/>");
        }

        _emptyRun = 0;
    }

    private string DateStyleName(int style, bool hasTime)
    {
        if (style == 0)
        {
            return hasTime ? OdsParts.DateTimeStyle : OdsParts.DateStyle;
        }

        return _styles.Cell(style, hasTime ? ValueKind.DateTime : ValueKind.Date).Name;
    }

    private void StartNumber(int style, ValueKind kind)
    {
        if (style == 0)
        {
            OpenCell();
            _row.Append(" office:value-type=\"float\" office:value=\"");
            return;
        }

        OdsCellStyle cell = _styles.Cell(style, kind);
        OpenCell();
        _row.Append(StyleAttribute);
        _row.Append(cell.Name);
        _row.Append(cell.Percent ? "\" office:value-type=\"percentage\" office:value=\"" : "\" office:value-type=\"float\" office:value=\"");
    }

    private string? WriteString(string value, int style)
    {
        if (value.Length > MaxTextChars || (value.Length * 6L) + TokenMargin > SheetScanner.MaxBufferChars && LongestToken(value) > SheetScanner.MaxBufferChars - TokenMargin)
        {
            return ErrorCodes.Write.TextTooLong;
        }

        OpenCell();

        if (style != 0)
        {
            _row.Append(StyleAttribute);
            _row.Append(_styles.Cell(style, ValueKind.Text).Name);
            _row.Append('"');
        }

        _row.Append(" office:value-type=\"string\"");

        if (value.Contains('\r', StringComparison.Ordinal))
        {
            _row.Append(" office:string-value=\"");
            AppendAttribute(value);
            _row.Append('"');
        }

        _row.Append("><text:p>");
        AppendParagraphs(value);
        _row.Append("</text:p></table:table-cell>");
        return null;
    }

    /// <summary>
    /// The longest token the reader will buffer for a value: its text escaped, or — when it holds a
    /// carriage return — the start tag carrying it as an attribute, escaped.
    /// </summary>
    private static long LongestToken(string value)
    {
        long text = 0;
        long attribute = 0;

        foreach (char c in value)
        {
            switch (c)
            {
                case '&':
                    text += 5;
                    attribute += 5;
                    break;
                case '<' or '>':
                    text += 4;
                    attribute += 4;
                    break;
                case '"':
                    text++;
                    attribute += 6;
                    break;
                case '\r' or '\n':
                    text++;
                    attribute += 5;
                    break;
                case '\t':
                    text++;
                    attribute += 4;
                    break;
                default:
                    text++;
                    attribute++;
                    break;
            }
        }

        return value.Contains('\r', StringComparison.Ordinal) ? Math.Max(text, attribute + TagOverhead) : text;
    }

    /// <summary>Writes text as paragraph content: line breaks as paragraphs, tabs and collapsible spaces as elements.</summary>
    private void AppendParagraphs(ReadOnlySpan<char> text)
    {
        bool boundary = true;     // at the start of a paragraph or after a tab, where ODF drops a space

        while (!text.IsEmpty)
        {
            int at = text.IndexOfAny(NeedsMarkup);

            if (at < 0)
            {
                _row.Append(text);
                return;
            }

            if (at > 0)
            {
                _row.Append(text[..at]);
                boundary = false;
            }

            char c = text[at];
            text = text[(at + 1)..];

            switch (c)
            {
                case '&':
                    _row.Append("&amp;");
                    boundary = false;
                    break;
                case '<':
                    _row.Append("&lt;");
                    boundary = false;
                    break;
                case '>':
                    _row.Append("&gt;");
                    boundary = false;
                    break;
                case '\t':
                    _row.Append("<text:tab/>");
                    boundary = true;
                    break;
                case '\r' when !text.IsEmpty && text[0] == '\n':
                    break;        // CR LF is one line break; the LF that follows writes it
                case '\r' or '\n':
                    _row.Append("</text:p><text:p>");
                    boundary = true;
                    break;
                default:
                    int run = 1 + (text.Length - text.TrimStart(' ').Length);
                    text = text[(run - 1)..];

                    if (!boundary)
                    {
                        _row.Append(' ');
                        run--;
                    }

                    if (run > 0)
                    {
                        _row.Append("<text:s text:c=\"");
                        _row.AppendFormatted(run, default, CultureInfo.InvariantCulture);
                        _row.Append("\"/>");
                    }

                    boundary = false;
                    break;
            }
        }
    }

    /// <summary>Writes text into a double-quoted attribute, keeping CR, LF and tab as character references.</summary>
    private void AppendAttribute(ReadOnlySpan<char> text)
    {
        foreach (char c in text)
        {
            switch (c)
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
                case '"':
                    _row.Append("&quot;");
                    break;
                case '\r':
                    _row.Append("&#13;");
                    break;
                case '\n':
                    _row.Append("&#10;");
                    break;
                case '\t':
                    _row.Append("&#9;");
                    break;
                default:
                    _row.Append(c);
                    break;
            }
        }
    }

    private void AppendColumns(ReadOnlySpan<WriteColumn> columns)
    {
        foreach (WriteColumn column in columns)
        {
            if (column.Width is { } width)
            {
                _row.Append("<table:table-column table:style-name=\"");
                _row.Append(OdsParts.ColumnStyleFor(width));
                _row.Append("\"/>");
            }
            else
            {
                _row.Append("<table:table-column/>");
            }
        }
    }

    /// <summary>Encodes what the row buffer holds into the bytes waiting for content.xml, and empties it.</summary>
    private void Emit()
    {
        _row.WriteUtf8To(_bytes);
        _row.Clear();
    }
}
