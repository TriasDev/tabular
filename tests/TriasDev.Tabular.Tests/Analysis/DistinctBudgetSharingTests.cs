using System.Globalization;
using System.Text;

using TriasDev.Tabular.Csv;
using TriasDev.Tabular.Tests.Fixtures;

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
    public void GivesUpTheColumnThatRepeatsRatherThanTheRightmostOneThatDoesNot()
    {
        // 800 + 300 + 800 distinct values against 1,700. The middle column repeats from row 301 on,
        // so its answer is settled and its room goes first; the rightmost keeps counting to the end.
        SheetProfile sheet = Analyze(i => $"{N(i)};c{N(i % 300)};r{N(i)}", budget: 1_700);

        Assert.True(sheet.Columns[2].Facts.IsUnique);
        Assert.True(sheet.Columns[2].Facts.DistinctCountIsExact);
        Assert.False(sheet.Columns[1].Facts.DistinctCountIsExact);
        Assert.True(sheet.Columns[0].Facts.IsUnique);
    }

    [Fact]
    public void AmongTheColumnsThatRepeatGivesUpTheOneHoldingMost()
    {
        // 300 names and 50 notes, both repeating; the budget runs dry with 50 rows to go. Giving up
        // the 300 frees enough for them, and the 50 keep their exact count.
        SheetProfile sheet = Analyze(i => $"{N(i)};name{N(i % 300)};note{N(i % 50)}", budget: 1_100);

        Assert.True(sheet.Columns[2].Facts.DistinctCountIsExact);
        Assert.Equal(50, sheet.Columns[2].Facts.DistinctCount);
        Assert.False(sheet.Columns[1].Facts.DistinctCountIsExact);
        Assert.True(sheet.Columns[0].Facts.IsUnique);
    }

    [Fact]
    public void HandsASheetsBudgetBackForTheNextSheet()
    {
        // Two sheets of 600 identifiers each, and room for 1,000: each fits alone, not both together.
        // On the second the identifier stands right of a constant column, so a first sheet's column
        // still held in the budget would not be the one made to give up.
        static string Ids(int from, bool besideAConstant)
        {
            string lead = besideAConstant ? "<c t=\"inlineStr\"><is><t>x</t></is></c>" : string.Empty;
            StringBuilder rows = new($"<row>{(besideAConstant ? "<c t=\"inlineStr\"><is><t>kind</t></is></c>" : string.Empty)}<c t=\"inlineStr\"><is><t>id</t></is></c></row>");

            for (int i = from; i < from + 600; i++)
            {
                rows.Append(CultureInfo.InvariantCulture, $"<row>{lead}<c><v>{i}</v></c></row>");
            }

            return rows.ToString();
        }

        byte[] workbook = new XlsxPackage().WithSheet("First", Ids(1, besideAConstant: false)).WithSheet("Second", Ids(1_001, besideAConstant: true)).Build();

        using ITabularCursor cursor = TabularFile.Open(new MemoryStream(workbook, writable: false), "t.xlsx", cancellationToken: Token);
        FileProfile profile = TabularAnalyzer.Analyze(cursor, new AnalysisOptions { DistinctTrackingBudget = 1_000 }, cancellationToken: Token);

        ColumnFacts first = profile.Sheets[0].Columns[0].Facts;
        ColumnFacts second = profile.Sheets[1].Columns[1].Facts;

        Assert.Equal("id", second.Header);
        Assert.All(new[] { first, second }, id =>
        {
            Assert.True(id.IsUnique);
            Assert.True(id.DistinctCountIsExact);
        });
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
