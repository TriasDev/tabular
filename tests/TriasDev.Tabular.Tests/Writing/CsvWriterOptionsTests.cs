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
}
