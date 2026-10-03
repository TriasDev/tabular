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
    private const int RetainedBytes = 3 * RowText.RetainedChars;

    /// <summary>Room left under the reader's token limit for markup around the text.</summary>
    private const int TokenMargin = 1024;

    private const int TagOverhead = 80;

    private static readonly int MaxTextChars = OdsCursorOptions.Default.MaxValueChars;

    private static readonly SearchValues<char> NeedsMarkup = SearchValues.Create("&<> \t\n\r");

    private readonly ZipWriter _zip;
    private readonly RowText _row = new();
    private ArrayBufferWriter<byte> _bytes = new(16 * 1024);
    private Stream? _content;

    public OdsSheetWriter(SpillBuffer output, OdsWriterOptions options)
    {
        _zip = new ZipWriter(output, options.CompressionLevel);

        // OpenDocument's rule: the mimetype is the first entry, stored, so a reader knows the file
        // from its first bytes.
        _zip.AddStored("mimetype", "application/vnd.oasis.opendocument.spreadsheet"u8);
    }

    public long MaxRows => 1_048_576;

    public bool AllowsSeveralSheets => true;

    public bool NamesSheets => true;

    public void BeginSheet(string name, ReadOnlySpan<WriteColumn> columns)
    {
        _row.Clear();

        if (_content is null)
        {
            _content = _zip.BeginDeflated("content.xml");
            _row.Append(OdsParts.ContentStart);
        }
        else
        {
            _row.Append("</table:table>");
        }

        _row.Append("<table:table table:name=\"");
        AppendAttribute(name);
        _row.Append("\">");
        AppendColumns(columns);
        Emit();
    }

    public void BeginRow()
    {
        _row.Clear();
        _row.Append("<table:table-row>");
    }

    public string? WriteHeader(string value) => WriteString(value);

    public string? WriteText(string value, int column) => WriteString(value);

    public string? WriteLong(long value) => throw new NotSupportedException("Numbers arrive with the next change.");

    public string? WriteDecimal(decimal value) => throw new NotSupportedException("Numbers arrive with the next change.");

    public string? WriteDouble(double value) => throw new NotSupportedException("Numbers arrive with the next change.");

    public string? WriteDate(DateTime value, bool hasTime) => throw new NotSupportedException("Dates arrive with the next change.");

    public void WriteBoolean(bool value)
    {
        _row.Append("<table:table-cell table:style-name=\"");
        _row.Append(OdsParts.BooleanStyle);
        _row.Append("\" office:value-type=\"boolean\" office:boolean-value=\"");
        _row.Append(value ? "true" : "false");
        _row.Append("\"><text:p>");
        _row.Append(value ? "TRUE" : "FALSE");
        _row.Append("</text:p></table:table-cell>");
    }

    public void WriteEmpty() => _row.Append("<table:table-cell/>");

    public void EndRow()
    {
        _row.Append("</table:table-row>");
        Emit();
    }

    public void Complete()
    {
        if (_content is not null)
        {
            _row.Clear();
            _row.Append(OdsParts.ContentEnd);
            Emit();
            _zip.EndEntry();
            _content = null;
        }

        _zip.AddStored("META-INF/manifest.xml", OdsParts.Manifest);
        _zip.AddStored("styles.xml", OdsParts.Styles);
        _zip.Complete();
    }

    public void Dispose()
    {
        // An abandoned spreadsheet: release the open entry's deflate state, write nothing more.
        _content?.Dispose();
        _content = null;
    }

    private string? WriteString(string value)
    {
        if (value.Length > MaxTextChars || (value.Length * 5L) + TokenMargin > SheetScanner.MaxBufferChars && LongestToken(value) > SheetScanner.MaxBufferChars - TokenMargin)
        {
            return ErrorCodes.Write.TextTooLong;
        }

        _row.Append("<table:table-cell office:value-type=\"string\"");

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

    /// <summary>Encodes what the row buffer holds into content.xml, and empties it.</summary>
    private void Emit()
    {
        _row.WriteUtf8To(_bytes);
        _content!.Write(_bytes.WrittenSpan);
        _bytes.ResetWrittenCount();
        _row.Clear();

        if (_bytes.Capacity > RetainedBytes)
        {
            _bytes = new ArrayBufferWriter<byte>(16 * 1024);
        }
    }
}
