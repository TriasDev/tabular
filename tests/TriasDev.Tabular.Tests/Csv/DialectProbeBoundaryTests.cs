using System.Globalization;
using System.Text;

using TriasDev.Tabular.Csv;
using TriasDev.Tabular.Tests.Fixtures;

using Xunit;

namespace TriasDev.Tabular.Tests.Csv;

/// <summary>
/// A well-formed csv longer than the dialect probe, whose probe ends inside a quoted field that spans
/// lines: the odd number of quotes it sees is a cut-off field, not a stray quote, and the delimiter must
/// still be found with quotes honoured (#79). A genuine stray quote must still be read as one.
/// </summary>
public sealed class DialectProbeBoundaryTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>
    /// A de-DE file as the writer produces it: ';' (or the delimiter given) between fields, every
    /// decimal quoted for its comma, and a note of several lines with commas in it — the shape whose
    /// commas, counted with quotes ignored, outvote the real delimiter.
    /// </summary>
    private static async Task<(byte[] File, List<(long Id, string Note, decimal Amount)> Rows)> Write(int noteLines, int lineLength, char? delimiter = null)
    {
        WriteTarget target = new();
        List<(long, string, decimal)> rows = [];
        TabularWriterOptions options = new() { Csv = new CsvWriterOptions { Culture = "de-DE", Delimiter = delimiter } };

        await using (TabularWriter writer = TabularWriter.Create(target, TabularFormat.Csv, options))
        {
            writer.BeginSheet("data", [new("Id"), new("Note"), new("Amount")]);

            long written = 0;
            long i = 0;

            while (written < 100_000)
            {
                string note = string.Join("\n", Enumerable.Repeat(new string('x', lineLength) + ", und so weiter", noteLines));
                decimal amount = 1.5m + i;
                writer.BeginRow();
                writer.Write(i);
                writer.Write(note);
                writer.Write(amount);
                writer.EndRow();
                rows.Add((i, note, amount));
                written += note.Length;
                i++;
            }

            await writer.CompleteAsync(Token);
        }

        return (target.ToArray(), rows);
    }

    /// <summary>The probe as the detector sees it: its first 64 KB, trimmed to their last line break.</summary>
    private static string Probe(byte[] file)
    {
        string text = Encoding.UTF8.GetString(file, 0, Math.Min(file.Length, CsvCursorOptions.Default.DialectProbeBytes));
        int lastBreak = text.LastIndexOf('\n');
        return lastBreak >= 0 ? text[..lastBreak] : text;
    }

    /// <summary>Whether the probe holds an odd number of quotes.</summary>
    private static bool ProbeEndsInsideAQuotedField(byte[] file) => Probe(file).AsSpan().Count('"') % 2 == 1;

    /// <summary>How many whole records, quotes honoured, the probe holds before the field it cuts off.</summary>
    private static int WholeRecordsInProbe(byte[] file)
    {
        int records = 0;
        bool inQuotes = false;

        foreach (char c in Probe(file))
        {
            inQuotes ^= c == '"';
            records += c == '\n' && !inQuotes ? 1 : 0;
        }

        return records;
    }

    [Theory]
    [InlineData(3, 10)]
    [InlineData(3, 41)]
    [InlineData(5, 120)]
    [InlineData(10, 40)]
    public async Task FindsTheDelimiterWhenTheProbeEndsInsideAMultiLineField(int noteLines, int lineLength)
    {
        (byte[] file, _) = await Write(noteLines, lineLength);
        Assert.True(ProbeEndsInsideAQuotedField(file), "the case under test: the probe must end inside a quoted field");

        Assert.Equal(';', CsvDialectDetector.Detect(new MemoryStream(file)).Delimiter);
    }

    /// <summary>
    /// Notes long enough that the probe ends within the records the detector inspects, so the field
    /// it cuts off is among them and must be left out — down to a probe holding only the header and
    /// the first row, and one holding only the header.
    /// </summary>
    [Theory]
    [InlineData(50, 100, ';', 12)]
    [InlineData(50, 100, '\t', 12)]
    [InlineData(100, 400, ';', 2)]
    [InlineData(100, 700, ';', 1)]
    [InlineData(100, 700, '\t', 1)]
    public async Task FindsTheDelimiterWhenTheProbeEndsWithinTheInspectedRecords(int noteLines, int lineLength, char delimiter, int wholeRecords)
    {
        (byte[] file, _) = await Write(noteLines, lineLength, delimiter);
        Assert.True(ProbeEndsInsideAQuotedField(file), "the case under test: the probe must end inside a quoted field");
        Assert.Equal(wholeRecords, WholeRecordsInProbe(file));

        Assert.Equal(delimiter, CsvDialectDetector.Detect(new MemoryStream(file)).Delimiter);
    }

    [Theory]
    [InlineData(3, 10, ';')]
    [InlineData(5, 120, ';')]
    [InlineData(50, 100, ';')]
    [InlineData(50, 100, '\t')]
    [InlineData(100, 700, ';')]
    public async Task ReadsBackWhatWasWritten(int noteLines, int lineLength, char delimiter)
    {
        (byte[] file, List<(long Id, string Note, decimal Amount)> rows) = await Write(noteLines, lineLength, delimiter);
        Assert.True(ProbeEndsInsideAQuotedField(file));

        using ITabularCursor cursor = TabularFile.Open(new MemoryStream(file, writable: false), "data.csv", cancellationToken: Token);
        Assert.True(cursor.MoveToSheet(0, Token));
        Assert.True(cursor.ReadRow(Token));
        Assert.Equal(["Id", "Note", "Amount"], cursor.CurrentRow.ToArray().Select(c => c.Text));

        foreach ((long id, string note, decimal amount) in rows)
        {
            Assert.True(cursor.ReadRow(Token));
            RawCell[] row = cursor.CurrentRow.ToArray();
            Assert.Equal(3, row.Length);
            Assert.Equal(id.ToString(CultureInfo.InvariantCulture), row[0].Text);
            Assert.Equal(note, row[1].Text);
            Assert.Equal(amount.ToString(CultureInfo.GetCultureInfo("de-DE")), row[2].Text);
        }

        Assert.False(cursor.ReadRow(Token));
    }

    /// <summary>
    /// A genuine stray quote, which a later quote — often the opening quote of a field that spans
    /// lines — closes in the wrong place. The "whole records" read with quotes honoured are then
    /// pieces of records, and the commas inside the quoted field can divide them as evenly as the real
    /// delimiter; the quote that opens or closes in the middle of a field is what gives them away.
    /// </summary>
    [Theory]
    // An inch mark in the header, closed by the opening quote of a note with line breaks.
    [InlineData("Col0;Col1;Col2 5\" wide\nv83;\"x;y, z\nmore, text\nend\";v71\nv38;\"x;y, z\";v86 a, b\n", ';')]
    // The same with tabs, and a CRLF header.
    [InlineData("Col0\tCol1\tCol2\tCol3\tCol4\tCol5 5\" wide\r\nv17\t\"x\ty, z\nmore, text\nend\"\tv57 a, b\tv62 a, b\tv21\tv96 a, b\nv4 a, b\tv42\t\"x\ty, z\"\t\"x\ty, z\"\tv50\tv78\n", '\t')]
    // A stray opening quote in the header, closed by a quote that opens a later field.
    [InlineData("Col0;\"Col1\nv64 a, b;v7\nv25;\"x;y, z\nmore, text\nend\"\r\n\"x;y, z\";v28\nv76 a, b;\"x;y, z\"\nv67;v20\r\nv32 a, b;\"x;y, z\"\nv43;v45 a, b\r\nv97;v65\n", ';')]
    // A stray quote that opens the header's last field and never closes: no whole record at all.
    [InlineData("Col0,Col1,Col2,Col3,Col4,Col5,\"Col6\nv33,v12,v38,v12,\"v62 a, b\",v38,\"v16 a, b\"\nv83,v29,v6,v41,v79,v88,v44\nv77,v31,v86,v83,v18,\"v77 a, b\",\"v94 a, b\"\nv82,\"v90 a, b\",v87,\"v86 a, b\",\"v81 a, b\",v91,\"v33 a, b\"\nv32,v96,v41,v19,v31,v0,v11\n", ',')]
    public void ReadsAStrayQuoteAsOneWhenTheWholeRecordsAreNotWellQuoted(string text, char delimiter)
    {
        Assert.Equal(delimiter, CsvDialectDetector.Detect(new MemoryStream(Encoding.UTF8.GetBytes(text))).Delimiter);
    }
}
