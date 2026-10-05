using System.Text;

namespace TriasDev.Tabular.Csv;

/// <summary>
/// Decides how a csv file is encoded and punctuated by looking at its head.
/// </summary>
/// <remarks>
/// This has to exist here because no csv library offers it: they take a decoded reader and a stated
/// delimiter. A prefix is enough for both questions and costs one seek, where reading the file twice
/// would cost a second pass over half a gigabyte.
/// </remarks>
internal static class CsvDialectDetector
{
    private static readonly char[] DelimiterCandidates = [';', ',', '\t', '|'];

    private const int LinesInspected = 20;

    /// <summary>Whether the character is one of the delimiters the reader detects.</summary>
    internal static bool IsDelimiter(char c) => c is ';' or ',' or '\t' or '|';

    /// <summary>
    /// The dialect the options state outright — the delimiter and the encoding both — or null when
    /// anything is left to detect from the file's head.
    /// </summary>
    internal static CsvDialect? Stated(CsvCursorOptions options) =>
        options is { Delimiter: { } delimiter, Encoding: { } encoding }
            ? new CsvDialect(encoding, DialectSource.Specified, delimiter, DialectSource.Specified, options.Quote ?? '"')
            : null;

    /// <summary>
    /// Reads the head of a seekable stream, decides the dialect, and leaves the stream where it found
    /// it. What the options state is taken as stated; only the rest is detected.
    /// </summary>
    internal static CsvDialect Detect(Stream stream, CsvCursorOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(stream);

        CsvCursorOptions checkedOptions = (options ?? CsvCursorOptions.Default).Checked();
        int probeBytes = checkedOptions.DialectProbeBytes;

        if (!stream.CanSeek)
        {
            throw new ArgumentException("Detection rewinds the stream, so it must be seekable.", nameof(stream));
        }

        long origin = stream.Position;

        byte[] probe = new byte[probeBytes];
        int read = stream.ReadAtLeast(probe, probeBytes, throwOnEndOfStream: false);
        stream.Position = origin;

        return Detect(probe.AsSpan(0, read), checkedOptions);
    }

    /// <summary>
    /// Decides the dialect from a file's head already read — for a stream that cannot be rewound,
    /// such as a file inside an archive, whose head is read once and the file then opened afresh.
    /// What <paramref name="options"/> states is taken as stated, and only the rest is detected; the
    /// head is still judged for being csv at all.
    /// </summary>
    internal static CsvDialect Detect(ReadOnlySpan<byte> probe, CsvCursorOptions options)
    {
        // Trimmed before the encoding is judged, not after. A multi-byte character cut in half by the
        // end of the probe is not invalid UTF-8, it is an incomplete view of it — and judging it
        // invalid demotes the whole file to the single-byte fallback, which never fails and quietly
        // turns every umlaut into two characters.
        ReadOnlySpan<byte> head = TrimIncompleteSequence(probe);

        // Every file that is not a zip arrives here, so this is where a file that is not csv at all
        // has to be told apart from one that is — or it is profiled as columns of mojibake and
        // reported as a successful analysis.
        if (head.StartsWith(CompoundFileSignature))
        {
            throw new TabularFormatException(TabularFormatException.Unsupported,
                "This is a legacy Excel workbook (.xls) or an Excel file protected with a password; "
                + "neither is supported. Save it as .xlsx without a password, or as .csv.");
        }

        if (IsXmlDocument(head))
        {
            throw new TabularFormatException(TabularFormatException.Unsupported,
                "The file is an XML document, not csv. Flat OpenDocument spreadsheets (.fods) and Excel "
                + "2003 XML spreadsheets are not supported; save it as .ods, .xlsx or .csv.");
        }

        (Encoding encoding, DialectSource encodingSource) = options.Encoding is { } stated
            ? (RefuseBinaryUnlessWide(head, stated), DialectSource.Specified)
            : DetectEncoding(head);

        char quote = options.Quote ?? '"';

        if (options.Delimiter is { } delimiterStated)
        {
            return new CsvDialect(encoding, encodingSource, delimiterStated, DialectSource.Specified, quote);
        }

        // The probe may end mid-character, and decoding a partial one would fail on the tail rather
        // than on real content. Trimming to the last line ending removes that risk for nothing: the
        // delimiter is decided from whole lines anyway.
        string text = encoding.GetString(head).TrimStart('﻿');
        int lastBreak = text.LastIndexOf('\n');

        if (lastBreak >= 0)
        {
            text = text[..lastBreak];
        }

        (char delimiter, DialectSource delimiterSource) = DetectDelimiter(text, quote);

        return new CsvDialect(encoding, encodingSource, delimiter, delimiterSource, quote);
    }

    /// <summary>
    /// A byte order mark settles the encoding; otherwise the content is tested for valid UTF-8, and
    /// anything else is read as Windows-1252.
    /// </summary>
    /// <remarks>
    /// The order matters more than it looks. Windows-1252 assigns a character to nearly every byte,
    /// so it never fails — which is exactly why it has to be the last resort and never the
    /// assumption. A UTF-8 file read as Windows-1252 raises nothing; it just turns every umlaut into
    /// two characters, which is the defect this replaces.
    /// </remarks>
    private static (Encoding Encoding, DialectSource Source) DetectEncoding(ReadOnlySpan<byte> head)
    {
        if (head.Length >= 3 && head[0] == 0xEF && head[1] == 0xBB && head[2] == 0xBF)
        {
            return (new UTF8Encoding(encoderShouldEmitUTF8Identifier: true), DialectSource.ByteOrderMark);
        }

        if (head.Length >= 2 && head[0] == 0xFF && head[1] == 0xFE)
        {
            return (Encoding.Unicode, DialectSource.ByteOrderMark);
        }

        if (head.Length >= 2 && head[0] == 0xFE && head[1] == 0xFF)
        {
            return (Encoding.BigEndianUnicode, DialectSource.ByteOrderMark);
        }

        // No text encoding the library reads puts a NUL byte in a csv — except UTF-16, which writes
        // one beside every ASCII character, and which some "Unicode text" exports write without its
        // mark. NUL bytes in any number anywhere else say the file is not text at all: compressed or
        // binary data carries one in every few hundred bytes. A stray one in a text export does not
        // make it binary, so a handful is tolerated.
        if (HoldsNulBytes(head))
        {
            return Utf16WithoutMark(head) is { } utf16
                ? (utf16, DialectSource.Detected)
                : throw NotText();
        }

        return IsValidUtf8(head)
            ? (new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), DialectSource.Detected)
            : (Windows1252Encoding.Instance, DialectSource.Fallback);
    }

    /// <summary>NUL bytes in a number no text export has; see <see cref="DetectEncoding"/>.</summary>
    private static bool HoldsNulBytes(ReadOnlySpan<byte> head)
    {
        int nulls = head.Count((byte)0);
        return nulls >= 4 && nulls * 1000L >= head.Length;
    }

    private static TabularFormatException NotText() =>
        new(TabularFormatException.Unsupported,
            "The file is not text: it holds NUL bytes, which a csv file does not. It may be a "
            + "binary document (a PDF, an image, an archive) uploaded as a table.");

    /// <summary>
    /// A stated encoding, after the head was judged to be text at all: a caller who knows the
    /// encoding of the files they expect still gets a binary upload refused, not read as mojibake.
    /// UTF-16 and UTF-32 put NUL bytes beside every ASCII character, so for them the test is moot.
    /// </summary>
    private static Encoding RefuseBinaryUnlessWide(ReadOnlySpan<byte> head, Encoding stated) =>
        stated.CodePage is not (1200 or 1201 or 12000 or 12001) && HoldsNulBytes(head)
            ? throw NotText()
            : stated;

    /// <summary>
    /// Whether the file opens with an XML declaration, after whitespace: in UTF-8, with or without its
    /// byte order mark, or in UTF-16 after its byte order mark, either way round.
    /// </summary>
    /// <remarks>
    /// A spreadsheet can be written as one XML document — flat OpenDocument, Excel 2003's XML — and
    /// read as csv it profiles as a column of tags. No csv starts with <c>&lt;?xml</c>; a table of
    /// markup that does not declare itself is left to be read as the text it is. Excel 2003 XML is
    /// also saved in UTF-16, which declares itself by its byte order mark.
    /// </remarks>
    internal static bool IsXmlDocument(ReadOnlySpan<byte> head)
    {
        if (head.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]))
        {
            head = head[3..];
        }
        else if (head.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xFE]))
        {
            return IsUtf16XmlDeclaration(head[2..], bigEndian: false);
        }
        else if (head.StartsWith((ReadOnlySpan<byte>)[0xFE, 0xFF]))
        {
            return IsUtf16XmlDeclaration(head[2..], bigEndian: true);
        }

        return head.TrimStart(" \t\r\n"u8).StartsWith("<?xml"u8);
    }

    /// <summary>Whether UTF-16 code units, after whitespace, spell <c>&lt;?xml</c>.</summary>
    private static bool IsUtf16XmlDeclaration(ReadOnlySpan<byte> units, bool bigEndian)
    {
        const string Declaration = "<?xml";
        int at = 0;

        while (at + 1 < units.Length && Unit(units, at, bigEndian) is ' ' or '\t' or '\r' or '\n')
        {
            at += 2;
        }

        if (units.Length - at < 2 * Declaration.Length)
        {
            return false;
        }

        for (int i = 0; i < Declaration.Length; i++)
        {
            if (Unit(units, at + (2 * i), bigEndian) != Declaration[i])
            {
                return false;
            }
        }

        return true;
    }

    private static char Unit(ReadOnlySpan<byte> units, int at, bool bigEndian) =>
        (char)(bigEndian ? (units[at] << 8) | units[at + 1] : units[at] | (units[at + 1] << 8));

    /// <summary>The OLE2 compound-file signature: a .xls workbook, or an encrypted .xlsx.</summary>
    internal static ReadOnlySpan<byte> CompoundFileSignature => [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1];

    /// <summary>
    /// UTF-16 without a byte order mark, recognised by where its NUL bytes stand: beside ASCII
    /// characters, so in the odd positions for little-endian and the even ones for big-endian.
    /// </summary>
    private static UnicodeEncoding? Utf16WithoutMark(ReadOnlySpan<byte> head)
    {
        int pairs = Math.Min(head.Length, 8192) / 2;

        if (pairs == 0)
        {
            return null;
        }

        int evenZeros = 0;
        int oddZeros = 0;

        for (int i = 0; i < pairs; i++)
        {
            evenZeros += head[2 * i] == 0 ? 1 : 0;
            oddZeros += head[(2 * i) + 1] == 0 ? 1 : 0;
        }

        // Mostly zero on one side, almost never on the other.
        if (oddZeros * 2 >= pairs && evenZeros * 20 < pairs)
        {
            return new UnicodeEncoding(bigEndian: false, byteOrderMark: false);
        }

        if (evenZeros * 2 >= pairs && oddZeros * 20 < pairs)
        {
            return new UnicodeEncoding(bigEndian: true, byteOrderMark: false);
        }

        return null;
    }

    /// <summary>
    /// Drops a UTF-8 sequence left incomplete by the end of the probe.
    /// </summary>
    /// <remarks>
    /// A sequence is at most four bytes, so at most three can be pending. Anything longer than that
    /// without a following continuation byte is genuinely malformed and is left in place to be judged
    /// as such.
    /// </remarks>
    private static ReadOnlySpan<byte> TrimIncompleteSequence(ReadOnlySpan<byte> head)
    {
        for (int back = 1; back <= 3 && back <= head.Length; back++)
        {
            byte b = head[^back];

            if ((b & 0b1100_0000) == 0b1000_0000)
            {
                continue;                       // a continuation byte; the lead is further back
            }

            int expected = b switch
            {
                >= 0b1111_0000 => 4,
                >= 0b1110_0000 => 3,
                >= 0b1100_0000 => 2,
                _ => 1,
            };

            return expected > back ? head[..^back] : head;
        }

        return head;
    }

    private static bool IsValidUtf8(ReadOnlySpan<byte> content)
    {
        try
        {
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true)
                .GetString(content);
            return true;
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }

    /// <summary>
    /// Finds the delimiter in the probe's text, deciding first whether its quotes can be trusted.
    /// </summary>
    /// <remarks>
    /// Quote-aware line splitting is right for a well-formed file and destructive for one with an
    /// unbalanced quote, where it swallows everything after it and leaves nothing to count. An odd
    /// number of quotes in the probe says which case this is — usually. It is also odd, in a
    /// well-formed file, when the probe ends inside a quoted field that spans lines: a note with line
    /// breaks cut off by the probe's last byte. The whole records before that field still read
    /// correctly with quotes honoured, so they are asked first.
    /// </remarks>
    private static (char Delimiter, DialectSource Source) DetectDelimiter(string text, char quote)
    {
        if (text.AsSpan().Count(quote) % 2 == 0)
        {
            return Decide(FirstLines(text, quote, honourQuotes: true, keepOpenTail: true), quote, honourQuotes: true).Choice;
        }

        // The whole records' answer stands when its delimiter divides every one of them the same way
        // and every quote in them stands where a quote can stand around that delimiter. A stray quote fails the
        // second test even where it passes the first: paired with the opening quote of a later field
        // that spans lines, it glues pieces of records together, and the commas inside that field can
        // then look as consistent as the real delimiter. A single whole record is enough — the header,
        // when the first row's note is longer than the probe. Only when the whole records fail, or
        // there is none, is the odd count taken for a stray quote and quotes ignored.
        List<string> whole = FirstLines(text, quote, honourQuotes: true, keepOpenTail: false);

        if (Decide(whole, quote, honourQuotes: true) is { Consistent: true } cut
            && whole.TrueForAll(record => IsWellQuoted(record, cut.Choice.Delimiter, quote)))
        {
            return cut.Choice;
        }

        return Decide(FirstLines(text, quote, honourQuotes: false, keepOpenTail: true), quote, honourQuotes: false).Choice;
    }

    /// <summary>
    /// Counts each candidate outside quoted runs across the lines and prefers the one that divides
    /// every line into the same number of fields; says whether the choice was that consistent.
    /// </summary>
    /// <remarks>
    /// Counting alone is not enough, and a real German export is why: its rows hold three times more
    /// commas than semicolons, because every decimal number contains one. What identifies the real
    /// delimiter is not how often it occurs but that it occurs equally often on every line.
    /// </remarks>
    private static ((char Delimiter, DialectSource Source) Choice, bool Consistent) Decide(List<string> lines, char quote, bool honourQuotes)
    {
        char best = ';';
        int bestScore = 0;
        bool bestConsistent = false;

        foreach (char candidate in DelimiterCandidates)
        {
            int[] counts = lines.Select(line => CountOutsideQuotes(line, candidate, quote, honourQuotes)).ToArray();
            int present = counts.Count(c => c > 0);

            if (present * 2 <= counts.Length)
            {
                // Absent from most lines (or there are no lines). Requiring it on the *first* line
                // instead is what made a file opening with a title row fall back to a guess, after
                // which every row read as one undivided field.
                continue;
            }

            int[] onLinesThatHaveIt = [.. counts.Where(c => c > 0)];
            bool consistent = Array.TrueForAll(onLinesThatHaveIt, c => c == onLinesThatHaveIt[0]);

            // Consistency outranks frequency.
            int score = (consistent ? 1_000_000 : 0) + (present * 1_000) + onLinesThatHaveIt[0];

            if (score > bestScore)
            {
                bestScore = score;
                best = candidate;
                bestConsistent = consistent;
            }
        }

        return bestScore == 0
            ? ((';', DialectSource.Fallback), false)
            : ((best, DialectSource.Detected), bestConsistent);
    }

    /// <summary>
    /// The first lines of the text — records, when quotes are honoured. <paramref name="keepOpenTail"/>
    /// false drops a last record still inside an open quote: the probe's end cut it off.
    /// </summary>
    private static List<string> FirstLines(string text, char quote, bool honourQuotes, bool keepOpenTail)
    {
        List<string> lines = [];
        bool inQuotes = false;
        int start = 0;

        for (int i = 0; i < text.Length && lines.Count < LinesInspected; i++)
        {
            char c = text[i];

            if (c == quote && honourQuotes)
            {
                inQuotes = !inQuotes;
            }
            else if (c == '\n' && !inQuotes)
            {
                lines.Add(text[start..i].TrimEnd('\r'));
                start = i + 1;
            }
        }

        if (start < text.Length && lines.Count < LinesInspected && (keepOpenTail || !inQuotes))
        {
            lines.Add(text[start..].TrimEnd('\r'));
        }

        return lines;
    }

    /// <summary>
    /// Whether every quote in the record stands where a quote can stand with <paramref name="delimiter"/>
    /// between fields: opening a field, at the record's start or after a delimiter; closing it, before
    /// a delimiter, a carriage return or the record's end; or doubled inside a quoted field.
    /// </summary>
    private static bool IsWellQuoted(string record, char delimiter, char quote)
    {
        bool inQuotes = false;
        int i = record.IndexOf(quote);

        while (i >= 0)
        {
            char next = i + 1 < record.Length ? record[i + 1] : delimiter;     // the end closes a field too

            if (!inQuotes)
            {
                if (i > 0 && record[i - 1] != delimiter)
                {
                    return false;
                }

                inQuotes = true;
            }
            else if (next == quote)
            {
                i++;                            // an escaped quote; both halves are read
            }
            else if (next == delimiter || next == '\r')
            {
                inQuotes = false;
            }
            else
            {
                return false;
            }

            i = record.IndexOf(quote, i + 1);
        }

        return true;
    }

    private static int CountOutsideQuotes(string line, char delimiter, char quote, bool honourQuotes)
    {
        int count = 0;
        bool inQuotes = false;

        foreach (char c in line)
        {
            if (c == quote && honourQuotes)
            {
                inQuotes = !inQuotes;
            }
            else if (c == delimiter && !inQuotes)
            {
                count++;
            }
        }

        return count;
    }
}
