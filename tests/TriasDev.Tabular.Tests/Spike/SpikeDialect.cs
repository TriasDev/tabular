using System.Text;

namespace TriasDev.Tabular.Tests.Spike;

/// <summary>
/// Detects the encoding and the delimiter of a csv file.
/// </summary>
/// <remarks>
/// None of the csv libraries under evaluation does this: each of them takes a
/// <see cref="TextReader"/> that is already decoded and a delimiter that is already decided. So this
/// code is ours whichever candidate wins, and the gate gives every candidate the same answer from
/// it — what is being compared is record parsing, not detection.
/// </remarks>
public static class SpikeDialect
{
    private static readonly char[] DelimiterCandidates = [';', ',', '\t', '|'];

    static SpikeDialect() =>
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    /// <summary>
    /// Reads enough of the head of a stream to decide both the encoding and the delimiter, then
    /// rewinds it.
    /// </summary>
    /// <remarks>
    /// Detection has to work from a prefix, because the alternative is reading a 572 MB file twice.
    /// A prefix is enough for both questions: an encoding is decided by its first bytes or by whether
    /// what follows decodes at all, and a delimiter is decided by how the first lines divide.
    /// </remarks>
    public static (Encoding Encoding, char Delimiter) Detect(Stream stream, int probeBytes = 64 * 1024)
    {
        if (!stream.CanSeek)
        {
            throw new ArgumentException("Detection rewinds the stream, so it must be seekable.", nameof(stream));
        }

        long origin = stream.Position;

        byte[] probe = new byte[probeBytes];
        int read = stream.ReadAtLeast(probe, probeBytes, throwOnEndOfStream: false);
        stream.Position = origin;

        byte[] head = read == probeBytes ? probe : probe[..read];
        Encoding encoding = DetectEncoding(head);

        // The probe may end mid-character; decoding it as a whole would fail on the tail rather than
        // on real content. Trimming to the last line ending removes that risk and costs nothing,
        // since the delimiter is decided from whole lines anyway.
        string text = encoding.GetString(head);
        int lastBreak = text.LastIndexOf('\n');
        if (lastBreak >= 0)
        {
            text = text[..lastBreak];
        }

        return (encoding, DetectDelimiter(text.TrimStart('\uFEFF')));
    }

    /// <summary>
    /// Decides the encoding: a byte order mark settles it, otherwise the content is tested for valid
    /// UTF-8, and anything else is read as Windows-1252.
    /// </summary>
    /// <remarks>
    /// The fallback matters more than it looks. Windows-1252 assigns a character to almost every
    /// byte, so it never throws — which is exactly why it must be the last resort and never the
    /// assumption. A file that is really UTF-8 read as Windows-1252 does not fail; it silently turns
    /// every umlaut into two characters.
    /// </remarks>
    public static Encoding DetectEncoding(byte[] content)
    {
        if (content.Length >= 3 && content[0] == 0xEF && content[1] == 0xBB && content[2] == 0xBF)
        {
            return new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);
        }

        if (content.Length >= 2 && content[0] == 0xFF && content[1] == 0xFE)
        {
            return Encoding.Unicode;
        }

        if (content.Length >= 2 && content[0] == 0xFE && content[1] == 0xFF)
        {
            return Encoding.BigEndianUnicode;
        }

        return IsValidUtf8(content)
            ? new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)
            : Encoding.GetEncoding(1252);
    }

    private static bool IsValidUtf8(byte[] content)
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
    /// Picks the delimiter by counting each candidate outside quoted runs, over the first few lines,
    /// and preferring the one whose count per line is both non-zero and consistent.
    /// </summary>
    /// <remarks>
    /// Counting alone is not enough, and the large German fixture is why: its rows hold far more
    /// commas than semicolons, because every decimal number contains one. What distinguishes the
    /// real delimiter is that it produces the same number of fields on every line.
    /// </remarks>
    public static char DetectDelimiter(string text)
    {
        List<string> lines = FirstLines(text, 20);

        if (lines.Count == 0)
        {
            return ';';
        }

        char best = ';';
        int bestScore = -1;

        foreach (char candidate in DelimiterCandidates)
        {
            List<int> counts = lines.Select(line => CountOutsideQuotes(line, candidate)).ToList();

            if (counts[0] == 0)
            {
                continue;                       // absent from the header: not the delimiter
            }

            bool consistent = counts.All(c => c == counts[0]);
            int score = (consistent ? 1000 : 0) + counts[0];

            if (score > bestScore)
            {
                bestScore = score;
                best = candidate;
            }
        }

        return best;
    }

    private static List<string> FirstLines(string text, int max)
    {
        List<string> lines = [];
        bool inQuotes = false;
        int start = 0;

        for (int i = 0; i < text.Length && lines.Count < max; i++)
        {
            char c = text[i];

            if (c == '"')
            {
                inQuotes = !inQuotes;
            }
            else if (c == '\n' && !inQuotes)
            {
                lines.Add(text[start..i].TrimEnd('\r'));
                start = i + 1;
            }
        }

        if (start < text.Length && lines.Count < max)
        {
            lines.Add(text[start..].TrimEnd('\r'));
        }

        return lines;
    }

    private static int CountOutsideQuotes(string line, char delimiter)
    {
        int count = 0;
        bool inQuotes = false;

        foreach (char c in line)
        {
            if (c == '"')
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
