using TriasDev.Tabular.Csv;
using TriasDev.Tabular.Xlsx;

using Xunit;

namespace TriasDev.Tabular.Tests;

/// <summary>The options ship the defaults the documentation promises.</summary>
public sealed class OptionDefaultsTests
{
    [Fact]
    public void ShipsTheDefaultsTheDocumentsPromise()
    {
        // The ceilings are tested through small overrides, which proves the mechanism and says
        // nothing about what actually ships. These numbers are quoted in the guide's bounds table
        // and reasoned about in the options' own remarks, so a silent change to one of them would
        // make those documents wrong with nothing to notice.
        XlsxCursorOptions xlsx = XlsxCursorOptions.Default;

        Assert.Equal(2L * 1024 * 1024 * 1024, xlsx.MaxUncompressedBytes);
        Assert.Equal(16_384, xlsx.MaxPackageEntries);
        Assert.Equal(4_096, xlsx.MaxSheets);
        Assert.Equal(8_192, xlsx.MaxRelationships);
        Assert.Equal(1_048_576, xlsx.MaxSharedStrings);
        Assert.Equal(64 * 1024 * 1024, xlsx.MaxSharedStringChars);
        Assert.Equal(100_000, xlsx.MaxCellFormats);
        Assert.Equal(16 * 1024 * 1024, xlsx.MaxValueChars);

        CsvCursorOptions csv = CsvCursorOptions.Default;

        Assert.Equal(16_384, csv.MaxColumns);
        Assert.Equal(16 * 1024 * 1024, csv.MaxFieldChars);
        Assert.Equal(100, csv.MaxQuotedFieldLines);

        Assert.Equal(1_000, ExtractionOptions.Default.MaxErrorRows);
        Assert.Equal(2_000_000, AnalysisOptions.Default.DistinctTrackingBudget);
        Assert.Equal(1_000, AnalysisOptions.Default.RetainedDistinctValues);

        CsvWriterOptions csvWriter = TabularWriterOptions.Default.Csv;

        Assert.Null(csvWriter.Culture);
        Assert.Null(csvWriter.Delimiter);
        Assert.True(csvWriter.ByteOrderMark);
        Assert.False(csvWriter.FormulaGuard);
        Assert.False(TabularWriterOptions.Default.LeaveOpen);
    }
}
