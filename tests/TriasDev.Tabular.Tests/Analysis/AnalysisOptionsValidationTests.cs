using System.Text;

using TriasDev.Tabular.Csv;

using Xunit;

namespace TriasDev.Tabular.Tests.Analysis;

/// <summary>Options are checked when they are handed to the analysis, not discovered deep inside a run.</summary>
public sealed class AnalysisOptionsValidationTests
{
    private static FileProfile Analyze(AnalysisOptions? options = null)
    {
        using CsvCursor cursor = new(new MemoryStream(Encoding.UTF8.GetBytes("a\n1\n")), "t.csv");
        return TabularAnalyzer.Analyze(cursor, options, cancellationToken: TestContext.Current.CancellationToken);
    }

    [Fact]
    public void RefusesAnEmptyCultureList()
    {
        // It used to reach the profiler, which took the first culture and threw IndexOutOfRange.
        Assert.Throws<ArgumentException>(() => Analyze(new AnalysisOptions { Cultures = [] }));
    }

    [Fact]
    public void RefusesACultureNameThisRuntimeDoesNotKnow()
    {
        // A typo would otherwise be dropped in silence, and the column read under fewer cultures than
        // the caller asked for.
        Assert.Throws<ArgumentException>(() => Analyze(new AnalysisOptions { Cultures = ["", "not-a-culture"] }));
    }

    [Theory]
    [InlineData("DistinctTrackingBudget")]
    [InlineData("RetainedDistinctValues")]
    [InlineData("HeaderRowIndex")]
    [InlineData("ProgressInterval")]
    [InlineData("OutlierSampleSize")]
    public void RefusesANegativeCountOrBound(string property)
    {
        AnalysisOptions options = property switch
        {
            "DistinctTrackingBudget" => new AnalysisOptions { DistinctTrackingBudget = -1 },
            "RetainedDistinctValues" => new AnalysisOptions { RetainedDistinctValues = -1 },
            "HeaderRowIndex" => new AnalysisOptions { HeaderRowIndex = -1 },
            "ProgressInterval" => new AnalysisOptions { ProgressInterval = 0 },
            _ => new AnalysisOptions { OutlierSampleSize = -1 },
        };

        Assert.ThrowsAny<ArgumentException>(() => Analyze(options));
    }

    [Theory]
    [InlineData(-0.1)]
    [InlineData(1.1)]
    public void RefusesAConfidenceOrStepOutsideZeroToOne(double value)
    {
        Assert.ThrowsAny<ArgumentException>(() => Analyze(new AnalysisOptions { MinimumHypothesisConfidence = value }));
        Assert.ThrowsAny<ArgumentException>(() => Analyze(new AnalysisOptions { ProgressStep = value }));
    }

    [Fact]
    public void AcceptsTheDefaults() => Assert.NotNull(Analyze());
}
