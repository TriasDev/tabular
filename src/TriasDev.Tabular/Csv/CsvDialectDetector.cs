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
public static class CsvDialectDetector
{
    private static readonly char[] DelimiterCandidates = [';', ',', '\t', '|'];

    private const int LinesInspected = 20;

    /// <summary>
    /// Reads the head of a seekable stream, decides the dialect, and leaves the stream where it found
    /// it.
    /// </summary>
    public static CsvDialect Detect(Stream stream, CsvCursorOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(stream);

        int probeBytes = (options ?? CsvCursorOptions.Default).Checked().DialectProbeBytes;

        if (!stream.CanSeek)
        {
            throw new ArgumentException("Detection rewinds the stream, so it must be seekable.", nameof(stream));
        }

        long origin = stream.Position;

        byte[] probe = new byte[probeBytes];
        int read = stream.ReadAtLeast(probe, probeBytes, throwOnEndOfStream: false);
        stream.Position = origin;

        // Trimmed before the encoding is judged, not after. A multi-byte character cut in half by the
        // end of the probe is not invalid UTF-8, it is an incomplete view of it — and judging it
        // invalid demotes the whole file to the single-byte fallback, which never fails and quietly
        // turns every umlaut into two characters.
        ReadOnlySpan<byte> head = TrimIncompleteSequence(probe.AsSpan(0, read));

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

        (Encoding encoding, DialectSource encodingSource) = DetectEncoding(head);

        // The probe may end mid-character, and decoding a partial one would fail on the tail rather
        // than on real content. Trimming to the last line ending removes that risk for nothing: the
        // delimiter is decided from whole lines anyway.
        string text = encoding.GetString(head).TrimStart('﻿');
        int lastBreak = text.LastIndexOf('\n');

        if (lastBreak >= 0)
        {
            text = text[..lastBreak];
        }

        (char delimiter, DialectSource delimiterSource) = DetectDelimiter(text);

        return new CsvDialect
        {
            Encoding = encoding,
            EncodingSource = encodingSource,
            Delimiter = delimiter,
            DelimiterSource = delimiterSource,
            Quote = '"',
        };
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
        int nulls = head.Count((byte)0);

        if (nulls >= 4 && nulls * 1000L >= head.Length)
        {
            return Utf16WithoutMark(head) is { } utf16
                ? (utf16, DialectSource.Detected)
                : throw new TabularFormatException(TabularFormatException.Unsupported,
                    "The file is not text: it holds NUL bytes, which a csv file does not. It may be a "
                    + "binary document (a PDF, an image, an archive) uploaded as a table.");
        }

        return IsValidUtf8(head)
            ? (new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), DialectSource.Detected)
            : (Windows1252Encoding.Instance, DialectSource.Fallback);
    }

    /// <summary>
    /// Whether the file opens with an XML declaration, after a UTF-8 byte order mark and whitespace.
    /// </summary>
    /// <remarks>
    /// A spreadsheet can be written as one XML document — flat OpenDocument, Excel 2003's XML — and
    /// read as csv it profiles as a column of tags. No csv starts with <c>&lt;?xml</c>; a table of
    /// markup that does not declare itself is left to be read as the text it is.
    /// </remarks>
    private static bool IsXmlDocument(ReadOnlySpan<byte> head)
    {
        if (head.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]))
        {
            head = head[3..];
        }

        return head.TrimStart(" \t\r\n"u8).StartsWith("<?xml"u8);
    }

    /// <summary>The OLE2 compound-file signature: a .xls workbook, or an encrypted .xlsx.</summary>
    private static ReadOnlySpan<byte> CompoundFileSignature => [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1];

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
    /// Counts each candidate outside quoted runs across the first lines and prefers the one that
    /// divides every line into the same number of fields.
    /// </summary>
    /// <remarks>
    /// Counting alone is not enough, and a real German export is why: its rows hold three times more
    /// commas than semicolons, because every decimal number contains one. What identifies the real
    /// delimiter is not how often it occurs but that it occurs equally often on every line.
    /// </remarks>
    private static (char Delimiter, DialectSource Source) DetectDelimiter(string text)
    {
        // Quote-aware line splitting is right for a well-formed file and destructive for one with an
        // unbalanced quote, where it swallows everything after it and leaves nothing to count. An odd
        // number of quotes in the probe says which case this is.
        bool quotesBalanced = text.AsSpan().Count('"') % 2 == 0;
        List<string> lines = FirstLines(text, quotesBalanced);

        if (lines.Count == 0)
        {
            return (';', DialectSource.Fallback);
        }

        char best = ';';
        int bestScore = 0;

        foreach (char candidate in DelimiterCandidates)
        {
            int[] counts = lines.Select(line => CountOutsideQuotes(line, candidate, quotesBalanced)).ToArray();
            int present = counts.Count(c => c > 0);

            if (present * 2 <= counts.Length)
            {
                // Absent from most lines. Requiring it on the *first* line instead is what made a
                // file opening with a title row fall back to a guess, after which every row read as
                // one undivided field.
                continue;
            }

            int[] onLinesThatHaveIt = [.. counts.Where(c => c > 0)];
            bool consistent = Array.TrueForAll(onLinesThatHaveIt, c => c == onLinesThatHaveIt[0]);

            // Consistency outranks frequency, and a real German export is why: its rows hold three
            // times more commas than semicolons, because every decimal number contains one. What
            // identifies the delimiter is not how often it occurs but that it divides every line the
            // same way.
            int score = (consistent ? 1_000_000 : 0) + (present * 1_000) + onLinesThatHaveIt[0];

            if (score > bestScore)
            {
                bestScore = score;
                best = candidate;
            }
        }

        return bestScore == 0 ? (';', DialectSource.Fallback) : (best, DialectSource.Detected);
    }

    private static List<string> FirstLines(string text, bool quotesBalanced)
    {
        List<string> lines = [];
        bool inQuotes = false;
        int start = 0;

        for (int i = 0; i < text.Length && lines.Count < LinesInspected; i++)
        {
            char c = text[i];

            if (c == '"' && quotesBalanced)
            {
                inQuotes = !inQuotes;
            }
            else if (c == '\n' && !inQuotes)
            {
                lines.Add(text[start..i].TrimEnd('\r'));
                start = i + 1;
            }
        }

        if (start < text.Length && lines.Count < LinesInspected)
        {
            lines.Add(text[start..].TrimEnd('\r'));
        }

        return lines;
    }

    private static int CountOutsideQuotes(string line, char delimiter, bool quotesBalanced)
    {
        int count = 0;
        bool inQuotes = false;

        foreach (char c in line)
        {
            if (c == '"' && quotesBalanced)
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
