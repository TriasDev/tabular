using System.Globalization;
using System.Text;

using TriasDev.Tabular.Csv;

using Xunit;

namespace TriasDev.Tabular.Tests.Analysis;

/// <summary>
/// When the file's distinct budget runs out, it goes where a uniqueness answer is still open, rather
/// than running dry for every column at once (#46).
/// </summary>
/// <remarks>
/// The columns spend the budget row by row, side by side, so they used to exhaust it at the same row
/// and every one of them — the identifier column first among them — came out undetermined. Now a
/// column that has already repeated a value, whose answer is settled, gives its room back first;
/// failing that, the rightmost column still counting does, so the columns to the left, where keys
/// usually stand, keep an answer.
/// </remarks>
public sealed class DistinctBudgetSharingTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private const int Rows = 800;

    private static SheetProfile Analyze(Func<int, string> row, int budget = 1_000) => AnalyzeFile(row, budget).Sheets[0];

    private static FileProfile AnalyzeFile(Func<int, string> row, int budget = 1_000)
    {
        StringBuilder csv = new("id;name;note\n");

        for (int i = 1; i <= Rows; i++)
        {
            csv.Append(row(i)).Append('\n');
        }

        using CsvCursor cursor = new(new MemoryStream(Encoding.UTF8.GetBytes(csv.ToString())), "t.csv");
        return TabularAnalyzer.Analyze(cursor, new AnalysisOptions { DistinctTrackingBudget = budget }, cancellationToken: Token);
    }

    private static string N(int i) => i.ToString(CultureInfo.InvariantCulture);

    [Fact]
    public void KeepsTheKeyDeterminedWhenTheOtherColumnsRepeat()
    {
        // 800 + 300 + 300 distinct values against a budget of 1,000, the last two repeating from row
        // 301 on — before the budget runs out at the identifier's 400th value.
        SheetProfile sheet = Analyze(i => $"{N(i)};name{N(i % 300)};note{N((i * 7) % 300)}");

        ColumnFacts id = sheet.Columns[0].Facts;
        Assert.True(id.IsUnique);
        Assert.True(id.DistinctCountIsExact);
        Assert.Equal(Rows, id.DistinctCount);

        foreach (ColumnFacts other in sheet.Columns.Skip(1).Select(c => c.Facts))
        {
            // Settled by the repeat it saw, however far its count got.
            Assert.False(other.IsUnique);
        }
    }

    [Fact]
    public void GivesTheLeftmostColumnsAnAnswerWhenEveryColumnIsStillUnique()
    {
        // Three columns of 800 distinct values each, and room for 1,000.
        SheetProfile sheet = Analyze(i => $"{N(i)};R{N(i * 7)};x{N(i * 13)}");

        Assert.True(sheet.Columns[0].Facts.IsUnique);
        Assert.Null(sheet.Columns[1].Facts.IsUnique);
        Assert.Null(sheet.Columns[2].Facts.IsUnique);
        Assert.False(sheet.Columns[2].Facts.DistinctCountIsExact);
    }

    [Fact]
    public void KnowsAColumnWithAnEmptyCellIsNotUniqueWithoutCountingIt()
    {
        SheetProfile sheet = Analyze(i => i == 5 ? "5;;x" : $"{N(i)};R{N(i * 7)};x{N(i * 13)}", budget: 10);

        Assert.False(sheet.Columns[1].Facts.IsUnique);
    }

    [Fact]
    public void ChangesNothingWhileTheBudgetLasts()
    {
        SheetProfile sheet = Analyze(i => $"{N(i)};name{N(i % 400)};note{N((i * 7) % 400)}", budget: 10_000);

        Assert.All(sheet.Columns, c => Assert.True(c.Facts.DistinctCountIsExact));
        Assert.Equal([Rows, 400, 400], sheet.Columns.Select(c => c.Facts.DistinctCount));
    }

    [Fact]
    public void ReportsTheRepeatsOfAColumnThatGaveUpCountingAsABound()
    {
        FileProfile profile = AnalyzeFile(i => $"{N(i)};name{N(i % 300)};note{N((i * 7) % 300)}");
        SheetProfile sheet = profile.Sheets[0];
        TextImportField name = ImportField.Text("name").Unique();

        PrecheckResult result = MappingPrecheck.Check(
            new MappingPlan { Bindings = [new ColumnBinding { ColumnIndex = 1, FieldName = "name", Header = "name" }] },
            new ImportSchema { Fields = [name] },
            profile);

        PrecheckFinding finding = Assert.Single(result.Findings, f => f.Code == ErrorCodes.Value.NotUnique);
        Assert.Equal(PrecheckSeverity.Blocking, finding.Severity);
        Assert.False(sheet.Columns[1].Facts.DistinctCountIsExact);
        Assert.Equal(RowCountBound.AtMost, finding.AffectedRowsBound);
    }
}
