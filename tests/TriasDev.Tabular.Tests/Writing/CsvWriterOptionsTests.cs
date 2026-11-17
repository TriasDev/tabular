using System.Globalization;

using TriasDev.Tabular.Csv;

using Xunit;

namespace TriasDev.Tabular.Tests.Writing;

/// <summary>
/// The csv options resolve to a format the import reads back — or are refused where they are handed
/// over, naming the option.
/// </summary>
public sealed class CsvWriterOptionsTests
{
    [Fact]
    public void DefaultsToInvariantCommaIsoDatesAndAByteOrderMark()
    {
        CsvFormat format = CsvWriterOptions.Default.Resolve();

        Assert.Same(CultureInfo.InvariantCulture, format.Culture);
        Assert.Equal(',', format.Delimiter);
        Assert.Equal("yyyy'-'MM'-'dd", format.DateFormat);
        Assert.Equal("yyyy'-'MM'-'dd'T'HH':'mm':'ss'.'fff", format.DateTimeFormat);
        Assert.True(format.ByteOrderMark);
        Assert.False(format.FormulaGuard);
    }

    [Fact]
    public void ACultureWithADecimalCommaDelimitsWithASemicolon()
    {
        CsvFormat format = new CsvWriterOptions { Culture = "de-DE" }.Resolve();

        Assert.Equal(';', format.Delimiter);
        Assert.Equal("dd.MM.yyyy", format.DateFormat);
        Assert.Equal("dd.MM.yyyy HH:mm:ss.fff", format.DateTimeFormat);
    }

    [Fact]
    public void ACultureWithADecimalPointKeepsTheComma()
    {
        Assert.Equal(',', new CsvWriterOptions { Culture = "en-US" }.Resolve().Delimiter);
    }

    [Theory]
    [InlineData(',')]
    [InlineData(';')]
    [InlineData('\t')]
    [InlineData('|')]
    public void TakesADelimiterTheReaderDetects(char delimiter)
    {
        Assert.Equal(delimiter, new CsvWriterOptions { Delimiter = delimiter }.Resolve().Delimiter);
    }

    [Theory]
    [InlineData(':')]
    [InlineData('"')]
    [InlineData('\n')]
    [InlineData('\r')]
    [InlineData(' ')]
    public void RefusesADelimiterTheReaderDoesNotDetect(char delimiter)
    {
        ArgumentException refused = Assert.ThrowsAny<ArgumentException>(() => new CsvWriterOptions { Delimiter = delimiter }.Resolve());

        Assert.Contains(nameof(CsvWriterOptions.Delimiter), refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RefusesADelimiterThatIsTheCulturesDecimalSeparator()
    {
        ArgumentException refused = Assert.ThrowsAny<ArgumentException>(() => new CsvWriterOptions { Culture = "de-DE", Delimiter = ',' }.Resolve());

        Assert.Contains(nameof(CsvWriterOptions.Delimiter), refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RefusesACultureThatDoesNotExist()
    {
        ArgumentException refused = Assert.ThrowsAny<ArgumentException>(() => new CsvWriterOptions { Culture = "xx-NOWHERE" }.Resolve());

        Assert.Contains(nameof(CsvWriterOptions.Culture), refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RefusesACultureWhoseCalendarIsNotGregorian()
    {
        // th-TH counts years in the Buddhist era by default; the import reads dates in the Gregorian calendar.
        ArgumentException refused = Assert.ThrowsAny<ArgumentException>(() => new CsvWriterOptions { Culture = "th-TH" }.Resolve());

        Assert.Contains(nameof(CsvWriterOptions.Culture), refused.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("de-DE")]
    [InlineData("en-US")]
    [InlineData("en-GB")]
    [InlineData("fr-FR")]
    public void ResolvesTheCulturesTheImportReadsBack(string culture)
    {
        Assert.NotNull(new CsvWriterOptions { Culture = culture }.Resolve());
    }

    [Fact]
    public async Task EveryCultureItAcceptsReadsBackTheValuesItsProbeWrites()
    {
        // The probe is what stands between a culture's formats and a file the import misreads, and no
        // single culture shows whether it works: the ones that need it are the odd ones, and which
        // those are depends on the ICU data of the system the tests run on. So every culture this
        // runtime has is tried, none named: each one the options accept writes the probe's values and
        // reads them back equal; each one they refuse is refused as a culture.
        ImportField[] fields = [ImportField.Decimal("Amount"), ImportField.Decimal("Half"), ImportField.Integer("Count"), ImportField.Date("Moment"), ImportField.Date("Last")];
        DateTime moment = new(1801, 2, 13, 14, 5, 6, 789, DateTimeKind.Unspecified);
        object?[] expected = [-1_234_567.891m, -0.5m, -42L, moment, new DateTime(9999, 12, 31, 0, 0, 0, DateTimeKind.Unspecified)];
        List<string> misread = [];
        int accepted = 0;

        foreach (CultureInfo culture in CultureInfo.GetCultures(CultureTypes.AllCultures))
        {
            try
            {
                _ = new CsvWriterOptions { Culture = culture.Name }.Resolve();
            }
            catch (ArgumentException refused)
            {
                Assert.Equal(nameof(CsvWriterOptions.Culture), refused.ParamName);
                continue;
            }

            accepted++;

            byte[] file = await CsvRoundTrip.WriteAsync(
                culture.Name,
                fields.Select(f => f.Name),
                writer =>
                {
                    writer.BeginRow();
                    writer.Write(-1_234_567.891m);
                    writer.Write(-0.5d);
                    writer.Write(-42L);
                    writer.Write(moment);
                    writer.Write(new DateOnly(9999, 12, 31));
                    writer.EndRow();
                },
                TestContext.Current.CancellationToken);

            (List<object?[]> rows, List<string> errors, _) = CsvRoundTrip.Import(file, culture.Name, fields, TestContext.Current.CancellationToken);

            if (errors.Count > 0 || rows.Count != 1 || !rows[0].SequenceEqual(expected))
            {
                misread.Add($"'{culture.Name}': {string.Join(" | ", errors)} {string.Join(" | ", rows.SelectMany(r => r))}");
            }
        }

        Assert.True(accepted > 0);
        Assert.Empty(misread);
    }
}
