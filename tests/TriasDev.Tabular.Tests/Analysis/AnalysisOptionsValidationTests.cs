
using Xunit;

namespace TriasDev.Tabular.Tests.Analysis;

/// <summary>Options are checked when the analyzer is made, not discovered deep inside a run.</summary>
public sealed class AnalysisOptionsValidationTests
{
    [Fact]
    public void RefusesAnEmptyCultureList()
    {
        // It used to reach the profiler, which took the first culture and threw IndexOutOfRange.
        Assert.Throws<ArgumentException>(() => new TabularAnalyzer(new AnalysisOptions { Cultures = [] }));
    }

    [Fact]
    public void RefusesACultureNameThisRuntimeDoesNotKnow()
    {
        // A typo would otherwise be dropped in silence, and the column read under fewer cultures than
        // the caller asked for.
        Assert.Throws<ArgumentException>(() => new TabularAnalyzer(new AnalysisOptions { Cultures = ["", "not-a-culture"] }));
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

        Assert.ThrowsAny<ArgumentException>(() => new TabularAnalyzer(options));
    }

    [Theory]
    [InlineData(-0.1)]
    [InlineData(1.1)]
    public void RefusesAConfidenceOrStepOutsideZeroToOne(double value)
    {
        Assert.ThrowsAny<ArgumentException>(() => new TabularAnalyzer(new AnalysisOptions { MinimumHypothesisConfidence = value }));
        Assert.ThrowsAny<ArgumentException>(() => new TabularAnalyzer(new AnalysisOptions { ProgressStep = value }));
    }

    [Fact]
    public void AcceptsTheDefaults() => Assert.NotNull(new TabularAnalyzer());
}
