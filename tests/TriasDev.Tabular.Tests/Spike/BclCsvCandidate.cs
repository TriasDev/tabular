using System.Text;

namespace TriasDev.Tabular.Tests.Spike;

/// <summary>
/// Parses csv records with a hand-written state machine reading straight from a
/// <see cref="TextReader"/>.
/// </summary>
/// <remarks>
/// <para>
/// The prototype of the csv half of the library's own cursor, and the counterpart to
/// <see cref="BclXlsxCandidate"/>: if both halves can be ours, the library carries no third-party
/// dependency at all.
/// </para>
/// <para>
/// It reads character by character out of a buffer rather than out of a decoded string, because the
/// files that decide this are hundreds of megabytes and a string of that size is a heap object
/// nothing can move. A record is defined by quoting, not by lines — which is the single rule the
/// prior art's row counting breaks.
/// </para>
/// </remarks>
public sealed class BclCsvCandidate : IParserCandidate
{
    private const int BufferSize = 64 * 1024;

    public string Name => "Own cursor, csv (BCL only)";

    public CandidateFormats Formats => CandidateFormats.Csv;

    public IEnumerable<IReadOnlyList<string?>> Rows(Stream stream)
    {
        (Encoding encoding, char delimiter) = SpikeDialect.Detect(stream);

        using StreamReader source = new(stream, encoding, detectEncodingFromByteOrderMarks: true, BufferSize);

        char[] buffer = new char[BufferSize];
        List<string?> row = [];
        StringBuilder field = new();

        bool inQuotes = false;
        bool fieldWasQuoted = false;
        bool rowHasContent = false;
        bool atFieldStart = true;       // a quote opens a quoted field only here, nowhere else
        bool pendingQuote = false;      // a quote that ended the previous buffer, meaning unknown yet
                                        // whether it closes the field or is the first of a doubled pair
        int count;

        while ((count = source.Read(buffer, 0, buffer.Length)) > 0)
        {
            for (int i = 0; i < count; i++)
            {
                char c = buffer[i];

                if (pendingQuote)
                {
                    pendingQuote = false;

                    if (c == '"')
                    {
                        field.Append('"');
                        continue;
                    }

                    inQuotes = false;
                    // fall through: this character is outside the quotes
                }
                else if (inQuotes)
                {
                    if (c != '"')
                    {
                        field.Append(c);
                        continue;
                    }

                    if (i + 1 < count)
                    {
                        if (buffer[i + 1] == '"')
                        {
                            field.Append('"');
#pragma warning disable S127 // a doubled quote is two characters of input and one of value
                            i++;
#pragma warning restore S127
                        }
                        else
                        {
                            inQuotes = false;
                        }
                    }
                    else
                    {
                        pendingQuote = true;
                    }

                    continue;
                }

                switch (c)
                {
                    case '"' when atFieldStart:
                        inQuotes = true;
                        fieldWasQuoted = true;
                        atFieldStart = false;
                        continue;
                    case '\r':
                        continue;                   // the '\n' closes the record
                    case '\n':
                        row.Add(Complete(field, fieldWasQuoted));
                        yield return row;
                        row = [];
                        fieldWasQuoted = false;
                        rowHasContent = false;
                        atFieldStart = true;
                        continue;
                }

                if (c == delimiter)
                {
                    row.Add(Complete(field, fieldWasQuoted));
                    fieldWasQuoted = false;
                    rowHasContent = true;
                    atFieldStart = true;
                    continue;
                }

                // Any other character, a quote included, is content. A quote that is not where a
                // field begins is an inch mark or a letter name, not syntax: real address data is
                // full of `100 21"st AVE`, and treating that quote as syntax makes the reader
                // swallow every line up to the next one.
                field.Append(c);
                rowHasContent = true;
                atFieldStart = false;
            }
        }

        // A file that does not end with a line break still has a final record.
        if (field.Length > 0 || fieldWasQuoted || rowHasContent || row.Count > 0)
        {
            row.Add(Complete(field, fieldWasQuoted));
            yield return row;
        }
    }

    /// <summary>
    /// Takes the accumulated field and resets the buffer. An unquoted empty field is an absent value;
    /// a quoted empty field is a present empty string, and the two are not the same thing.
    /// </summary>
    private static string? Complete(StringBuilder field, bool wasQuoted)
    {
        string value = field.ToString();
        field.Clear();

        return value.Length == 0 && !wasQuoted ? null : value;
    }
}
