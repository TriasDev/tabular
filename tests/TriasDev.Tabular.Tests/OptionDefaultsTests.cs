
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

        // A new instance rather than the shared Default, which was built before this test ran: the
        // property initialisers are what ships, and this way they run under the test.
        OdsCursorOptions ods = new();

        Assert.Equal(8L * 1024 * 1024 * 1024, ods.MaxUncompressedBytes);
        Assert.Equal(16_384, ods.MaxPackageEntries);
        Assert.Equal(4_096, ods.MaxSheets);
        Assert.Equal(16_384, ods.MaxColumns);
        Assert.Equal(1_048_576, ods.MaxRows);
        Assert.Equal(16 * 1024 * 1024, ods.MaxValueChars);
        Assert.Equal(100_000_000, ods.MaxRepeatedCells);
        Assert.Equal(ods, OdsCursorOptions.Default);

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

    [Fact]
    public void GivesANewWorkbookOptionsInstanceTheSameDefaults()
    {
        // Options built with an initialiser start from a new instance, not from Default — and Default
        // is made once, before any test can look at how it was made.
        XlsxCursorOptions xlsx = new();

        Assert.Equal(XlsxCursorOptions.Default, xlsx);
        Assert.Equal(2L * 1024 * 1024 * 1024, xlsx.MaxUncompressedBytes);
        Assert.Equal(16_384, xlsx.MaxPackageEntries);
        Assert.Equal(4_096, xlsx.MaxSheets);
        Assert.Equal(2_048, xlsx.MaxMetadataChars);
        Assert.Equal(8_192, xlsx.MaxRelationships);
        Assert.Equal(1_048_576, xlsx.MaxSharedStrings);
        Assert.Equal(64 * 1024 * 1024, xlsx.MaxSharedStringChars);
        Assert.Equal(100_000, xlsx.MaxCellFormats);
        Assert.Equal(16 * 1024 * 1024, xlsx.MaxValueChars);
    }
}
