using System.Reflection;

using Xunit;

namespace TriasDev.Tabular.Tests;

/// <summary>
/// One table written by each real producer reads as that table: the same values of the same kinds,
/// whoever wrote it (see <c>Fixtures/Producers/README.md</c>).
/// </summary>
public sealed class ProducerFixtureTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static readonly DateTime Day = new(2024, 1, 15, 0, 0, 0, DateTimeKind.Unspecified);

    public static TheoryData<string, string, bool, bool> Spreadsheets => new()
    {
        // file, what a typed "_x000D_ as typed" reads as, whether the merged row and the second sheet are there
        { "closedxml.xlsx", "_x000D_ as typed", true, true },
        { "epplus4.xlsx", "_x000D_ as typed", true, true },
        { "libreoffice.xlsx", "_x000D_ as typed", true, true },
        { "libreoffice.ods", "_x000D_ as typed", true, true },
        { "sylvan.xlsx", "_x000D_ as typed", false, false },
        { "miniexcel.xlsx", "_x000D_ as typed", false, true },

        // These two write the text as it is, without escaping the format's own escape, so it reads
        // as a carriage return — as it does in Excel — which trimming then removes.
        { "npoi.xlsx", "as typed", true, true },
        { "openxml-sdk.xlsx", "as typed", true, true },
    };

    [Theory]
    [MemberData(nameof(Spreadsheets))]
    public void ReadsTheTableEveryProducerWrote(string file, string typedEscape, bool merged, bool secondSheet)
    {
        using ITabularCursor cursor = TabularFile.Open(Fixture(file), file, cancellationToken: Token);

        Assert.Equal(secondSheet ? ["Data", "Zweite & <Seite>"] : ["Data"], cursor.Sheets.Select(s => s.Name));

        List<(int, RawCell[])> expected =
        [
            (1, [.. new[] { "text", "integer", "decimal", "date", "datetime", "boolean", "gap", "note" }.Select(RawCell.FromText)]),
            (2, [Text("plain"), Number(42), Number(1.5), Date(Day), Date(Day.AddHours(10.5)), RawCell.FromBoolean(true), RawCell.Empty, Text("line one\nline two")]),
            (3, [Text("padded"), Number(-7), Number(0.1), Date(new DateTime(1900, 3, 1, 0, 0, 0, DateTimeKind.Unspecified)), Date(new DateTime(1999, 12, 31, 23, 59, 59, DateTimeKind.Unspecified)), RawCell.FromBoolean(false), RawCell.Empty, Text("tab\there")]),
            (4, [Text("<&>\"'"), Number(1e15), Number(123456.789), Date(new DateTime(2000, 2, 29, 0, 0, 0, DateTimeKind.Unspecified)), Date(new DateTime(2038, 1, 19, 3, 14, 7, DateTimeKind.Unspecified)), RawCell.FromBoolean(true), RawCell.Empty, Text("😀 ä ß €")]),
            (5, [Text(typedEscape), Number(0), Number(-0.5), Date(new DateTime(9999, 12, 31, 0, 0, 0, DateTimeKind.Unspecified)), Date(new DateTime(1970, 1, 1, 0, 0, 1, DateTimeKind.Unspecified)), RawCell.FromBoolean(false), RawCell.Empty, Text("carriage\nreturn")]),
        ];

        if (merged)
        {
            expected.Add((7, [Text("merged")]));
        }

        AssertSheet(expected, Rows(cursor, 0), file);

        if (secondSheet)
        {
            AssertSheet([(1, [Text("second")])], Rows(cursor, 1), file);
        }
    }

    [Fact]
    public void ReadsTheTableLibreOfficeWroteAsCsv()
    {
        // LibreOffice writes values as it displays them, here under a German interface: every cell text.
        using ITabularCursor cursor = TabularFile.Open(Fixture("libreoffice.csv"), "libreoffice.csv", cancellationToken: Token);

        Assert.Equal(
            [
                (1, "text|integer|decimal|date|datetime|boolean|gap|note"),
                (2, "plain|42|1,5|2024-01-15|2024-01-15 10:30:00|WAHR||line one\nline two"),
                (3, "padded|-7|0,1|1900-03-01|1999-12-31 23:59:59|FALSCH||tab\there"),
                (4, "<&>\"'|1000000000000000|123456,789|2000-02-29|2038-01-19 03:14:07|WAHR||😀 ä ß €"),
                (5, "_x000D_ as typed|0|-0,5|9999-12-31|1970-01-01 00:00:01|FALSCH||carriage\nreturn"),
                (7, "merged"),
            ],
            Rows(cursor, 0).Select(r => (r.Number, string.Join('|', r.Cells.Select(c => c.AsText())).TrimEnd('|'))));
        Assert.True(cursor.Diagnostics.IsClean);
    }

    private static RawCell Text(string text) => RawCell.FromText(text);

    private static RawCell Number(double value) => RawCell.FromNumber(value);

    private static RawCell Date(DateTime value) => RawCell.FromDate(value);

    private static Stream Fixture(string name) =>
        Assembly.GetExecutingAssembly().GetManifestResourceStream($"Producers/{name}")
        ?? throw new InvalidOperationException($"{name} is not embedded in the test assembly.");

    /// <summary>The sheet's rows that hold anything, numbered as the sheet numbers them.</summary>
    private static List<(int Number, RawCell[] Cells)> Rows(ITabularCursor cursor, int sheet)
    {
        Assert.True(cursor.MoveToSheet(sheet, Token));
        List<(int, RawCell[])> rows = [];

        while (cursor.ReadRow(Token))
        {
            RawCell[] cells = cursor.CurrentRow.ToArray();

            if (cells.Any(c => !c.IsEmpty))
            {
                rows.Add((cursor.CurrentRowNumber, cells));
            }
        }

        return rows;
    }

    private static void AssertSheet(List<(int Number, RawCell[] Cells)> expected, List<(int Number, RawCell[] Cells)> read, string file)
    {
        Assert.True(expected.Count == read.Count, $"{file}: {read.Count} rows for {expected.Count}: {string.Join(", ", read.Select(r => r.Number))}");

        for (int r = 0; r < expected.Count; r++)
        {
            Assert.True(expected[r].Number == read[r].Number, $"{file}: row {read[r].Number} for {expected[r].Number}");

            for (int c = 0; c < Math.Max(expected[r].Cells.Length, read[r].Cells.Length); c++)
            {
                RawCell want = c < expected[r].Cells.Length ? expected[r].Cells[c] : RawCell.Empty;
                RawCell got = c < read[r].Cells.Length ? read[r].Cells[c] : RawCell.Empty;

                Assert.True(want.Equals(got), $"{file}: row {expected[r].Number}, column {c + 1}: expected {want.Kind} [{want}], read {got.Kind} [{got}]");
            }
        }
    }
}
