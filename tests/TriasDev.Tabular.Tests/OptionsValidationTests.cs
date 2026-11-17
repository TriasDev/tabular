using System.Text;

using TriasDev.Tabular.Csv;
using TriasDev.Tabular.Ods;
using TriasDev.Tabular.Tests.Fixtures;
using TriasDev.Tabular.Xlsx;

using Xunit;

namespace TriasDev.Tabular.Tests;

/// <summary>
/// An option that cannot work is refused where it is handed over, naming the option — not discovered
/// deep inside a read as a limit that is always exceeded.
/// </summary>
public sealed class OptionsValidationTests
{
    private static readonly TextImportField Name = ImportField.Text("name");

    private static readonly ImportSchema Schema = new() { Fields = [Name] };

    private static readonly MappingPlan Plan = new()
    {
        Bindings = [new ColumnBinding { ColumnIndex = 0, Header = "name", FieldName = "name" }],
    };

    private static MemoryStream Csv() => new(Encoding.UTF8.GetBytes("name;x\na;b\n"), writable: false);

    public static TheoryData<CsvCursorOptions, string> BadCsvOptions => new()
    {
        { new CsvCursorOptions { MaxQuotedFieldLines = -1 }, nameof(CsvCursorOptions.MaxQuotedFieldLines) },
        { new CsvCursorOptions { MaxFieldChars = 0 }, nameof(CsvCursorOptions.MaxFieldChars) },
        { new CsvCursorOptions { DialectProbeBytes = -1 }, nameof(CsvCursorOptions.DialectProbeBytes) },
        { new CsvCursorOptions { MaxColumns = 0 }, nameof(CsvCursorOptions.MaxColumns) },
        { new CsvCursorOptions { Delimiter = ':' }, nameof(CsvCursorOptions.Delimiter) },
        { new CsvCursorOptions { Delimiter = '"' }, nameof(CsvCursorOptions.Delimiter) },
        { new CsvCursorOptions { Quote = '\n' }, nameof(CsvCursorOptions.Quote) },
        { new CsvCursorOptions { Quote = '\r' }, nameof(CsvCursorOptions.Quote) },
        { new CsvCursorOptions { Quote = ';' }, nameof(CsvCursorOptions.Quote) },
        { new CsvCursorOptions { Delimiter = '|', Quote = '|' }, nameof(CsvCursorOptions.Quote) },
    };

    public static TheoryData<XlsxCursorOptions, string> BadXlsxOptions => new()
    {
        { new XlsxCursorOptions { MaxUncompressedBytes = 0 }, nameof(XlsxCursorOptions.MaxUncompressedBytes) },
        { new XlsxCursorOptions { MaxSharedStrings = -1 }, nameof(XlsxCursorOptions.MaxSharedStrings) },
        { new XlsxCursorOptions { MaxSharedStringChars = -1 }, nameof(XlsxCursorOptions.MaxSharedStringChars) },
        { new XlsxCursorOptions { MaxPackageEntries = 0 }, nameof(XlsxCursorOptions.MaxPackageEntries) },
        { new XlsxCursorOptions { MaxSheets = 0 }, nameof(XlsxCursorOptions.MaxSheets) },
        { new XlsxCursorOptions { MaxMetadataChars = 0 }, nameof(XlsxCursorOptions.MaxMetadataChars) },
        { new XlsxCursorOptions { MaxRelationships = 0 }, nameof(XlsxCursorOptions.MaxRelationships) },
        { new XlsxCursorOptions { MaxCellFormats = -1 }, nameof(XlsxCursorOptions.MaxCellFormats) },
        { new XlsxCursorOptions { MaxValueChars = 0 }, nameof(XlsxCursorOptions.MaxValueChars) },
    };

    public static TheoryData<OdsCursorOptions, string> BadOdsOptions => new()
    {
        { new OdsCursorOptions { MaxUncompressedBytes = 0 }, nameof(OdsCursorOptions.MaxUncompressedBytes) },
        { new OdsCursorOptions { MaxPackageEntries = 0 }, nameof(OdsCursorOptions.MaxPackageEntries) },
        { new OdsCursorOptions { MaxSheets = 0 }, nameof(OdsCursorOptions.MaxSheets) },
        { new OdsCursorOptions { MaxColumns = 0 }, nameof(OdsCursorOptions.MaxColumns) },
        { new OdsCursorOptions { MaxRows = 0 }, nameof(OdsCursorOptions.MaxRows) },
        { new OdsCursorOptions { MaxValueChars = 0 }, nameof(OdsCursorOptions.MaxValueChars) },
        { new OdsCursorOptions { MaxRepeatedCells = -1 }, nameof(OdsCursorOptions.MaxRepeatedCells) },
    };

    /// <summary>
    /// A repeat budget of nothing is a setting that works: a file without repeats reads, and the first
    /// copy a repeat would add is refused.
    /// </summary>
    [Fact]
    public void AcceptsAnOdsRepeatBudgetOfNothing()
    {
        const string Value = "<table:table-cell office:value-type=\"string\"><text:p>a</text:p></table:table-cell>";
        OdsCursorOptions options = new() { MaxRepeatedCells = 0 };

        byte[] plain = new OdsPackage().WithTable("S", $"<table:table-row>{Value}</table:table-row>").Build();
        using (OdsCursor cursor = new(new MemoryStream(plain), options, cancellationToken: TestContext.Current.CancellationToken))
        {
            Assert.True(cursor.ReadRow(TestContext.Current.CancellationToken));
        }

        byte[] repeated = new OdsPackage().WithTable("S", $"<table:table-row table:number-rows-repeated=\"2\">{Value}</table:table-row>").Build();
        using OdsCursor refused = new(new MemoryStream(repeated), options, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(nameof(OdsCursorOptions.MaxRepeatedCells), Assert.Throws<TabularLimitException>(() => refused.ReadRow(TestContext.Current.CancellationToken)).Limit);
    }

    [Theory]
    [MemberData(nameof(BadOdsOptions))]
    public void RefusesAnOdsOptionThatCannotWork(OdsCursorOptions options, string option)
    {
        byte[] spreadsheet = new OdsPackage().WithTable("S", "<table:table-row><table:table-cell office:value-type=\"string\"><text:p>a</text:p></table:table-cell></table:table-row>").Build();

        ArgumentOutOfRangeException error = Assert.Throws<ArgumentOutOfRangeException>(
            () => new OdsCursor(new MemoryStream(spreadsheet), options, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains(option, error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(BadCsvOptions))]
    public void RefusesACsvOptionThatCannotWork(CsvCursorOptions options, string option)
    {
        ArgumentOutOfRangeException error = Assert.Throws<ArgumentOutOfRangeException>(() => new CsvCursor(Csv(), "t.csv", options));

        Assert.Contains(option, error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(BadXlsxOptions))]
    public void RefusesAnXlsxOptionThatCannotWork(XlsxCursorOptions options, string option)
    {
        byte[] workbook = new XlsxPackage().WithSheet("S", """<row r="1"><c r="A1" t="inlineStr"><is><t>a</t></is></c></row>""").Build();

        ArgumentOutOfRangeException error = Assert.Throws<ArgumentOutOfRangeException>(
            () => new XlsxCursor(new MemoryStream(workbook), options, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains(option, error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// For each kind of file an archive or a gzip file may hold, an option that file would never be
    /// read with: a csv's options when it holds a workbook, a workbook's when it holds a csv, the
    /// workbook size bound when it holds no workbook.
    /// </summary>
    public static TheoryData<bool, bool, TabularOpenOptions, string> OptionsTheContentNeverUses => new()
    {
        { false, false, new TabularOpenOptions { Archive = new ArchiveCursorOptions { MaxEmbeddedWorkbookBytes = 0 } }, nameof(ArchiveCursorOptions.MaxEmbeddedWorkbookBytes) },
        { false, false, new TabularOpenOptions { Xlsx = new XlsxCursorOptions { MaxSharedStrings = -1 } }, nameof(XlsxCursorOptions.MaxSharedStrings) },
        { false, false, new TabularOpenOptions { Ods = new OdsCursorOptions { MaxRows = 0 } }, nameof(OdsCursorOptions.MaxRows) },
        { false, true, new TabularOpenOptions { Csv = new CsvCursorOptions { MaxQuotedFieldLines = -1 } }, nameof(CsvCursorOptions.MaxQuotedFieldLines) },
        { true, false, new TabularOpenOptions { Archive = new ArchiveCursorOptions { MaxEmbeddedWorkbookBytes = 0 } }, nameof(ArchiveCursorOptions.MaxEmbeddedWorkbookBytes) },
        { true, false, new TabularOpenOptions { Xlsx = new XlsxCursorOptions { MaxSharedStrings = -1 } }, nameof(XlsxCursorOptions.MaxSharedStrings) },
        { true, false, new TabularOpenOptions { Ods = new OdsCursorOptions { MaxRows = 0 } }, nameof(OdsCursorOptions.MaxRows) },
        { true, true, new TabularOpenOptions { Csv = new CsvCursorOptions { MaxQuotedFieldLines = -1 } }, nameof(CsvCursorOptions.MaxQuotedFieldLines) },
    };

    [Theory]
    [MemberData(nameof(OptionsTheContentNeverUses))]
    public void RefusesAnOptionThatCannotWorkWhenAnArchiveOrGzipFileIsOpenedWhateverItHolds(bool gzip, bool workbook, TabularOpenOptions options, string option)
    {
        // Checked where they are handed over, all of them: otherwise a bad option passes for as long as
        // the uploads happen to hold no file of its kind.
        byte[] content = workbook
            ? new XlsxPackage().WithSheet("S", """<row r="1"><c r="A1" t="inlineStr"><is><t>a</t></is></c></row>""").Build()
            : Encoding.UTF8.GetBytes("name;x\na;b\n");
        string entry = workbook ? "t.xlsx" : "t.csv";
        byte[] file = gzip ? GzipFile.Of(content) : new ZipArchiveBuilder().With(entry, content).Build();

        ArgumentOutOfRangeException error = Assert.Throws<ArgumentOutOfRangeException>(
            () => TabularFile.Open(new MemoryStream(file, writable: false), gzip ? "t.gz" : "t.zip", options, TestContext.Current.CancellationToken));

        Assert.Contains(option, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RefusesAnErrorCeilingOfNothing()
    {
        // Zero used to stop a run at its first failure, which is what AllOrNothing is for.
        using CsvCursor cursor = new(Csv(), "t.csv");

        ArgumentOutOfRangeException error = Assert.Throws<ArgumentOutOfRangeException>(() => TabularExtractor.Extract(
            cursor, Plan, Schema, new ExtractionOptions { MaxErrorRows = 0 }, TestContext.Current.CancellationToken));

        Assert.Contains(nameof(ExtractionOptions.MaxErrorRows), error.Message, StringComparison.Ordinal);
    }

    public static TheoryData<ReviewOptions, string> BadReviewOptions => new()
    {
        { new ReviewOptions { MaxErrors = 0 }, nameof(ReviewOptions.MaxErrors) },
        { new ReviewOptions { MaxErrors = ExtractionOptions.ReportedRowsCeiling + 1 }, nameof(ReviewOptions.MaxErrors) },
        { new ReviewOptions { MaxReportedRows = 0 }, nameof(ExtractionOptions.MaxReportedRows) },
        { new ReviewOptions { MaxReportedRows = ExtractionOptions.ReportedRowsCeiling + 1 }, nameof(ExtractionOptions.MaxReportedRows) },
        { new ReviewOptions { ProgressInterval = 0 }, nameof(ReviewOptions.ProgressInterval) },
        { new ReviewOptions { ProgressStep = -0.1 }, nameof(ReviewOptions.ProgressStep) },
        { new ReviewOptions { ProgressStep = 1.5 }, nameof(ReviewOptions.ProgressStep) },
        { new ReviewOptions { ProgressStep = double.NaN }, nameof(ReviewOptions.ProgressStep) },
    };

    [Theory]
    [MemberData(nameof(BadReviewOptions))]
    public void RefusesAReviewOptionThatCannotWork(ReviewOptions options, string option)
    {
        using CsvCursor cursor = new(Csv(), "t.csv");

        ArgumentOutOfRangeException error = Assert.Throws<ArgumentOutOfRangeException>(() => TabularExtractor.Review(
            cursor, Plan, Schema, options, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains(option, error.Message, StringComparison.Ordinal);
    }

    public static TheoryData<ReviewOptions> ReviewOptionsAtTheirEdges => new()
    {
        new ReviewOptions { MaxErrors = ExtractionOptions.ReportedRowsCeiling },
        new ReviewOptions { MaxReportedRows = ExtractionOptions.ReportedRowsCeiling },
        new ReviewOptions { MaxErrors = 1, MaxReportedRows = 1, ProgressInterval = 1 },
        new ReviewOptions { ProgressStep = 0 },
        new ReviewOptions { ProgressStep = 1 },
    };

    [Theory]
    [MemberData(nameof(ReviewOptionsAtTheirEdges))]
    public void AcceptsAReviewOptionAtTheEdgeOfItsRange(ReviewOptions options)
    {
        using CsvCursor cursor = new(Csv(), "t.csv");

        ImportReview review = TabularExtractor.Review(cursor, Plan, Schema, options, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(1, review.Summary.RowsProduced);
    }

    [Fact]
    public void AcceptsAsManyReportedRowsAsTheCeilingAndNoMore()
    {
        using (CsvCursor cursor = new(Csv(), "t.csv"))
        {
            ExtractionRun run = TabularExtractor.Extract(
                cursor, Plan, Schema, new ExtractionOptions { MaxReportedRows = ExtractionOptions.ReportedRowsCeiling }, TestContext.Current.CancellationToken);

            Assert.True(run.ReadRow(TestContext.Current.CancellationToken));
        }

        using CsvCursor refused = new(Csv(), "t.csv");

        ArgumentOutOfRangeException error = Assert.Throws<ArgumentOutOfRangeException>(() => TabularExtractor.Extract(
            refused, Plan, Schema, new ExtractionOptions { MaxReportedRows = ExtractionOptions.ReportedRowsCeiling + 1 }, TestContext.Current.CancellationToken));

        Assert.Contains(nameof(ExtractionOptions.MaxReportedRows), error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RefusesANegativePreview()
    {
        using CsvCursor cursor = new(Csv(), "t.csv");

        ArgumentOutOfRangeException error = Assert.Throws<ArgumentOutOfRangeException>(() => TabularImporter.Import(
            cursor, Plan, Schema, row => row[Name], new ImportOptions { PreviewRows = -1 }, TestContext.Current.CancellationToken));

        Assert.Contains(nameof(ImportOptions.PreviewRows), error.Message, StringComparison.Ordinal);
    }
}
