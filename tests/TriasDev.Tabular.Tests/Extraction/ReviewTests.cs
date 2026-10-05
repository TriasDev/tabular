using System.Text;

using Xunit;

namespace TriasDev.Tabular.Tests.Extraction;

/// <summary>
/// The whole file through the mapping, keeping nothing, before anything is written: what a review
/// screen shows between mapping and import.
/// </summary>
public sealed class ReviewTests
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private static readonly IntegerImportField Amount = ImportField.Integer("amount");

    private static readonly MappingPlan Plan = new()
    {
        Bindings = [new ColumnBinding { ColumnIndex = 0, Header = "amount", FieldName = "amount" }],
    };

    [Fact]
    public void ReadsEveryRowAndCountsEveryError()
    {
        ImportReview review = Review("amount\n1\nx\n2\ny\n", new ImportSchema { Fields = [Amount] });

        Assert.Equal(4, review.Summary.RowsRead);
        Assert.Equal(2, review.Summary.RowsFailed);
        Assert.Equal([3, 5], review.Errors.Select(e => e.RowNumber));
        Assert.True(review.ErrorsComplete);
        Assert.False(review.Summary.StoppedEarly);
    }

    [Fact]
    public void ReadsToTheEndUnderAllOrNothing()
    {
        // The policy decides an import; a review that stopped at the first error would show one
        // problem of many and call it the file's.
        ImportReview review = Review("amount\nx\ny\n", new ImportSchema { Fields = [Amount], Policy = ImportPolicy.AllOrNothing });

        Assert.Equal(2, review.Summary.RowsFailed);
        Assert.False(review.Summary.StoppedEarly);
    }

    [Fact]
    public void ReadsPastTheExtractionErrorLimit()
    {
        string csv = "amount\n" + string.Concat(Enumerable.Repeat("x\n", 1_200));

        ImportReview review = Review(csv, new ImportSchema { Fields = [Amount] });

        Assert.Equal(1_200, review.Summary.RowsFailed);
        Assert.False(review.Summary.StoppedEarly);
    }

    [Fact]
    public void KeepsTheFirstErrorsAndCountsTheRest()
    {
        ImportReview review = Review("amount\nx\ny\nz\n", new ImportSchema { Fields = [Amount] }, new ReviewOptions { MaxErrors = 2 });

        Assert.Equal(2, review.Errors.Count);
        Assert.False(review.ErrorsComplete);
        Assert.Equal(3, review.Summary.ErrorCount);
    }

    [Fact]
    public void ReportsProgressAndCompletion()
    {
        Recorder progress = new();

        Review("amount\n1\n2\n3\n", new ImportSchema { Fields = [Amount] }, new ReviewOptions { ProgressInterval = 1, ProgressStep = 0 }, progress);

        Assert.True(progress.Reports.Count > 1);
        Assert.True(progress.Reports[^1].IsComplete);
        Assert.Equal(3, progress.Reports[^1].RowsRead);
    }

    [Fact]
    public void CarriesTheAlternatives()
    {
        DecimalImportField lat = ImportField.Decimal("lat");
        ImportSchema schema = new()
        {
            Fields = [lat],
            Alternatives = [new FieldAlternatives("location", [AlternativeGroup.AllOf("coordinates", lat)])],
        };
        MappingPlan plan = new() { Bindings = [new ColumnBinding { ColumnIndex = 0, Header = "lat", FieldName = "lat" }] };

        using ITabularCursor cursor = Open("lat\n1\n\n2\n");
        ImportReview review = TabularExtractor.Review(cursor, plan, schema, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(2, Assert.Single(review.Alternatives).Groups[0].Won);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(10_001)]
    public void RefusesAnErrorCapOutsideItsRange(int cap)
    {
        using ITabularCursor cursor = Open("amount\n1\n");

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            TabularExtractor.Review(cursor, Plan, new ImportSchema { Fields = [Amount] }, new ReviewOptions { MaxErrors = cap }, cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public void RefusesAPlanThatDoesNotFitItsSchema()
    {
        using ITabularCursor cursor = Open("amount\n1\n");
        MappingPlan plan = new() { Bindings = [new ColumnBinding { ColumnIndex = 0, Header = "amount", FieldName = "nope" }] };

        Assert.Throws<MappingPlanException>(() =>
            TabularExtractor.Review(cursor, plan, new ImportSchema { Fields = [Amount] }, cancellationToken: TestContext.Current.CancellationToken));
    }

    private static ImportReview Review(string csv, ImportSchema schema, ReviewOptions? options = null, IProgress<AnalysisProgress>? progress = null)
    {
        using ITabularCursor cursor = Open(csv);
        return TabularExtractor.Review(cursor, Plan, schema, options, progress, TestContext.Current.CancellationToken);
    }

    private static ITabularCursor Open(string csv) =>
        TabularFile.Open(new MemoryStream(Utf8NoBom.GetBytes(csv), writable: false), "test.csv");

    private sealed class Recorder : IProgress<AnalysisProgress>
    {
        public List<AnalysisProgress> Reports { get; } = [];

        public void Report(AnalysisProgress value) => Reports.Add(value);
    }
}
