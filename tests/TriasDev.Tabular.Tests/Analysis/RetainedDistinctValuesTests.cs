using TriasDev.Tabular.Abstractions;
using TriasDev.Tabular.Analysis;

using Xunit;

namespace TriasDev.Tabular.Tests.Analysis;

/// <summary>
/// The facts that let a caller ask whether a column holds its reference data — country codes, legal
/// forms — without the library being told what any of those are.
/// </summary>
public sealed class RetainedDistinctValuesTests
{
    [Fact]
    public void KeepsEveryValueOfALowCardinalityColumn()
    {
        ColumnFacts facts = Profile(["DE", "AT", "DE", "CH", "DE", "AT"]);

        Assert.True(facts.DistinctValuesAreComplete);
        Assert.Equal(["AT", "CH", "DE"], facts.DistinctValues.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void SaysSoRatherThanHandingBackASubset()
    {
        // The difference between "no value here is a country code" and "none of the ones I kept is".
        // Only the first is an answer, so the second is not offered in a shape that can be mistaken
        // for one.
        ColumnFacts facts = Profile(
            [.. Enumerable.Range(0, 50).Select(i => i.ToString())],
            new AnalysisOptions { RetainedDistinctValues = 10 });

        Assert.False(facts.DistinctValuesAreComplete);
        Assert.Empty(facts.DistinctValues);

        // The count is still exact. Only the values were given up.
        Assert.Equal(50, facts.DistinctCount);
        Assert.True(facts.DistinctCountIsExact);
    }

    [Fact]
    public void IsIncompleteWhenTheDistinctBudgetRanOut()
    {
        // A set cannot be every value of the column when the count behind it is a lower bound.
        DistinctBudget budget = new(3);
        ColumnProfiler profiler = new(0, "code", budget, new AnalysisOptions { RetainedDistinctValues = 1_000 });

        for (int i = 0; i < 10; i++)
        {
            profiler.Accept(RawCell.FromText(i.ToString()), i + 1);
        }

        ColumnFacts facts = profiler.ToFacts();

        Assert.False(facts.DistinctCountIsExact);
        Assert.False(facts.DistinctValuesAreComplete);
        Assert.Empty(facts.DistinctValues);
    }

    [Fact]
    public void AnswersMembershipAgainstAReferenceSetTheLibraryNeverSees()
    {
        // What this whole fact exists for. The caller owns the reference data; the library only
        // reports what is in the column, and the two meet here.
        HashSet<string> iso = ["DE", "AT", "CH", "FR"];

        ColumnFacts facts = Profile(["DE", "AT", "ZZ", "DE", "CH", "QQ"]);

        Assert.True(facts.DistinctValuesAreComplete);

        string[] strangers = [.. facts.DistinctValues.Where(v => !iso.Contains(v)).Order(StringComparer.Ordinal)];

        Assert.Equal(["QQ", "ZZ"], strangers);
    }

    [Fact]
    public void KeepsNothingWhenRetentionIsTurnedOff()
    {
        ColumnFacts facts = Profile(["DE", "AT"], new AnalysisOptions { RetainedDistinctValues = 0 });

        Assert.False(facts.DistinctValuesAreComplete);
        Assert.Empty(facts.DistinctValues);
        Assert.Equal(2, facts.DistinctCount);
    }

    [Fact]
    public void DoesNotCountEmptyValuesAsOneOfThem()
    {
        ColumnFacts facts = Profile(["DE", "", "AT", "   ", "DE"]);

        Assert.Equal(["AT", "DE"], facts.DistinctValues.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void StopsAllocatingOnceItHasGivenUp()
    {
        // Giving up is permanent, and the emptied list must not read as room to start again.
        // Measured against the same work with retention switched off, because an absolute budget
        // would only be pinning what the rest of the profiler happens to allocate today. Refilling
        // changes no answer this class returns — it just quietly spends memory, 33 MB on a
        // five-million-row column when it was found.
        long withRetention = AllocatedProfiling(new AnalysisOptions { RetainedDistinctValues = 100 });
        long without = AllocatedProfiling(new AnalysisOptions { RetainedDistinctValues = 0 });

        // An absolute allowance, not a share of the baseline. The baseline is dominated by hash-set
        // growth over two hundred thousand inserts, which has nothing to do with what is being
        // measured, so a percentage of it was a percentage of the wrong number — wide enough that the
        // defect this test exists for would have landed inside it. The real difference between the
        // two runs is under a hundred bytes.
        Assert.True(
            withRetention < without + 64 * 1024,
            $"Retention kept allocating after giving up: {withRetention} against {without} bytes.");
    }

    /// <summary>Profiles a column of nothing but new values, and says what that allocated.</summary>
    private static long AllocatedProfiling(AnalysisOptions options)
    {
        DistinctBudget budget = new(1_000_000);
        ColumnProfiler profiler = new(0, "id", budget, options);

        // Past the cap before measuring, so what is measured is only the steady state after it.
        string[] values = [.. Enumerable.Range(0, 200_000).Select(i => $"v{i}")];

        for (int i = 0; i < 200; i++)
        {
            profiler.Accept(RawCell.FromText(values[i]), i + 1);
        }

        long before = GC.GetAllocatedBytesForCurrentThread();

        for (int i = 200; i < values.Length; i++)
        {
            profiler.Accept(RawCell.FromText(values[i]), i + 1);
        }

        long spent = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.False(profiler.ToFacts().DistinctValuesAreComplete);

        return spent;
    }

    private static ColumnFacts Profile(IEnumerable<string> values, AnalysisOptions? options = null)
    {
        DistinctBudget budget = new(1_000_000);
        ColumnProfiler profiler = new(0, "code", budget, options);
        int row = 0;

        foreach (string value in values)
        {
            profiler.Accept(RawCell.FromText(value), ++row);
        }

        return profiler.ToFacts();
    }
}
