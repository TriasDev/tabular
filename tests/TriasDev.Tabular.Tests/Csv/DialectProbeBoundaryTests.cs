using TriasDev.Tabular.Csv;
using TriasDev.Tabular.Tests.Fixtures;

using Xunit;

namespace TriasDev.Tabular.Tests.Csv;

/// <summary>
/// A well-formed csv longer than the dialect probe, whose probe ends inside a quoted field that spans
/// lines: the odd number of quotes it sees is a cut-off field, not a stray quote, and the delimiter must
/// still be found with quotes honoured (#79).
/// </summary>
public sealed class DialectProbeBoundaryTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>
    /// A de-DE file as the writer produces it: ';' between fields, every decimal quoted for its comma,
    /// and a note of several lines with commas in it — the shape whose commas, counted with quotes
    /// ignored, outvote the semicolons.
    /// </summary>
    private static async Task<(byte[] File, List<(long Id, string Note, decimal Amount)> Rows)> Write(int noteLines, int lineLength)
    {
        WriteTarget target = new();
        List<(long, string, decimal)> rows = [];

        await using (TabularWriter writer = TabularWriter.Create(target, TabularFormat.Csv, new TabularWriterOptions { Csv = new CsvWriterOptions { Culture = "de-DE" } }))
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

    /// <summary>Whether the first 64 KB, trimmed to their last line break, hold an odd number of quotes.</summary>
    private static bool ProbeEndsInsideAQuotedField(byte[] file)
    {
        byte[] probe = file[..Math.Min(file.Length, CsvCursorOptions.Default.DialectProbeBytes)];
        int lastBreak = Array.LastIndexOf(probe, (byte)'\n');
        return probe[..lastBreak].Count(b => b == (byte)'"') % 2 == 1;
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

    [Theory]
    [InlineData(3, 10)]
    [InlineData(5, 120)]
    public async Task ReadsBackWhatWasWritten(int noteLines, int lineLength)
    {
        (byte[] file, List<(long Id, string Note, decimal Amount)> rows) = await Write(noteLines, lineLength);
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
            Assert.Equal(id.ToString(System.Globalization.CultureInfo.InvariantCulture), row[0].Text);
            Assert.Equal(note, row[1].Text);
            Assert.Equal(amount.ToString(System.Globalization.CultureInfo.GetCultureInfo("de-DE")), row[2].Text);
        }

        Assert.False(cursor.ReadRow(Token));
    }
}
