using System.Text;

using TriasDev.Tabular.Csv;
using TriasDev.Tabular.Tests.Fixtures;

using Xunit;

namespace TriasDev.Tabular.Tests.Writing;

/// <summary>Text the csv reader would not give back as written is refused; the formula guard is opt-in.</summary>
public sealed class CsvTextTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static async Task<string> One(string value, CsvWriterOptions? options = null)
    {
        WriteTarget target = new();

        await using (TabularWriter writer = TabularWriter.Create(
            target,
            TabularFormat.Csv,
            new TabularWriterOptions { Csv = (options ?? CsvWriterOptions.Default) with { ByteOrderMark = false } }))
        {
            writer.BeginSheet("data", [new("v")]);
            writer.BeginRow();
            writer.Write(value);
            writer.EndRow();
            await writer.CompleteAsync(Token);
        }

        return Encoding.UTF8.GetString(target.ToArray())["v\r\n".Length..^2];
    }

    private static async Task<TabularWriteException> Refused(string value)
    {
        await using TabularWriter writer = TabularWriter.Create(new WriteTarget(), TabularFormat.Csv);
        writer.BeginSheet("data", [new("v")]);
        writer.BeginRow();

        return Assert.Throws<TabularWriteException>(() => writer.Write(value));
    }

    [Theory]
    [InlineData("\u0000")]
    [InlineData("a\u0001b")]
    [InlineData("\u0008")]
    [InlineData("\u000B")]
    [InlineData("\u000C")]
    [InlineData("\u001F")]
    [InlineData("￾")]
    [InlineData("￿")]
    public async Task RefusesACharacterXmlForbids(string value)
    {
        Assert.Equal(ErrorCodes.Write.InvalidCharacter, (await Refused(value)).Code);
    }

    [Fact]
    public async Task RefusesAnUnpairedSurrogate()
    {
        // Unpaired surrogates in string literals (ldstr, UTF-16 user-string heap) survive compile-time.
        foreach (string value in new[] { "lone \uD800 high", "lone \uDC00 low", "swapped \uDC00\uD800", "ends high \uD83D" })
        {
            Assert.Equal(ErrorCodes.Write.InvalidCharacter, (await Refused(value)).Code);
        }
    }

    [Fact]
    public async Task WritesAPairedSurrogate()
    {
        // Valid surrogate pair in a literal.
        Assert.Equal("pair 👍 ok", await One("pair 👍 ok"));
    }

    [Theory]
    [InlineData("emoji 👍 pair")]
    [InlineData("_x0001_ literal")]
    public async Task WritesTheCharactersXmlAllows(string value)
    {
        Assert.Equal(value, await One(value));
    }

    [Theory]
    [InlineData("tab\there")]
    [InlineData("a;b")]
    [InlineData("a,b")]
    [InlineData("a|b")]
    public async Task QuotesEveryDelimiterTheReaderCouldDetectNotOnlyTheOneWritten(string value)
    {
        Assert.Equal('"' + value + '"', await One(value));
    }

    [Fact]
    public async Task TakesAsManyLineBreaksAsTheReaderDoesAndNoMore()
    {
        string hundred = string.Join('\n', Enumerable.Repeat("line", 101));
        Assert.Equal('"' + hundred + '"', await One(hundred));

        Assert.Equal(ErrorCodes.Write.TooManyLines, (await Refused(hundred + "\nline")).Code);
    }

    [Theory]
    [InlineData("\r\n", 100, false)]
    [InlineData("\r\n", 101, true)]
    [InlineData("\r", 101, true)]
    [InlineData("\n", 101, true)]
    public async Task CountsLineBreaksAsTheReaderDoes(string lineBreak, int count, bool refused)
    {
        string value = "a" + string.Concat(Enumerable.Repeat(lineBreak + "a", count));

        if (refused)
        {
            Assert.Equal(ErrorCodes.Write.TooManyLines, (await Refused(value)).Code);
        }
        else
        {
            Assert.Equal('"' + value + '"', await One(value));
        }
    }

    [Fact]
    public async Task RefusesTextLongerThanTheReaderReads()
    {
        Assert.Equal(ErrorCodes.Write.TextTooLong, (await Refused(new string('x', CsvCursorOptions.Default.MaxFieldChars + 1))).Code);
    }

    [Theory]
    [InlineData("=1+2", "'=1+2")]
    [InlineData("+1", "'+1")]
    [InlineData("-1", "'-1")]
    [InlineData("@SUM(A1)", "'@SUM(A1)")]
    [InlineData("\tcmd", "\"'\tcmd\"")]
    [InlineData("\rcmd", "\"'\rcmd\"")]
    [InlineData("plain", "plain")]
    [InlineData("a=b", "a=b")]
    public async Task GuardsTextASpreadsheetWouldRunWhenAsked(string value, string written)
    {
        Assert.Equal(written, await One(value, new CsvWriterOptions { FormulaGuard = true }));
    }

    [Fact]
    public async Task GuardsNeitherByDefaultNorANumber()
    {
        Assert.Equal("=1+2", await One("=1+2"));

        WriteTarget target = new();

        await using (TabularWriter writer = TabularWriter.Create(
            target,
            TabularFormat.Csv,
            new TabularWriterOptions { Csv = new CsvWriterOptions { FormulaGuard = true, ByteOrderMark = false } }))
        {
            writer.BeginSheet("data", [new("v")]);
            writer.BeginRow();
            writer.Write(-5L);
            writer.EndRow();
            await writer.CompleteAsync(Token);
        }

        Assert.Equal("v\r\n-5\r\n", Encoding.UTF8.GetString(target.ToArray()));
    }

    [Fact]
    public async Task RefusesAHeaderWithAForbiddenCharacter()
    {
        await using TabularWriter writer = TabularWriter.Create(new WriteTarget(), TabularFormat.Csv);

        Assert.ThrowsAny<ArgumentException>(() => writer.BeginSheet("data", [new("bad\u0001")]));
    }

    [Fact]
    public async Task AHugeRowDoesNotKeepItsBufferForTheRestOfTheFile()
    {
        using SpillBuffer buffer = new();
        CsvSheetWriter sheet = new(buffer, CsvWriterOptions.Default.Resolve());
        sheet.BeginSheet("data", [new("v")]);

        sheet.BeginRow();
        Assert.Null(sheet.WriteText(new string('x', 10_000_000), 0, 0));
        sheet.EndRow();

        Assert.True(sheet.RowBufferLength <= 4 * 1024);
        await buffer.DrainToAsync(Stream.Null, Token);
    }
}
