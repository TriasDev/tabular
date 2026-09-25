using TriasDev.Tabular.Tests.Fixtures;
using TriasDev.Tabular.Xlsx;

using Xunit;

namespace TriasDev.Tabular.Tests.Xlsx;

/// <summary>
/// Shapes found by reading other readers' test corpora through this one — legal workbooks, written
/// by real producers, that the golden fixtures did not happen to contain.
/// </summary>
/// <remarks>
/// Each fixture is rebuilt here from raw OOXML in the smallest form that shows the case; the files
/// that revealed them are not copied.
/// </remarks>
public sealed class ProducerQuirksTests
{
    /// <summary>
    /// Reads every row, under a deadline, so a reader that loops forever fails the test instead of
    /// hanging the run.
    /// </summary>
    private static List<string?[]> ReadAll(byte[] content)
    {
        using CancellationTokenSource deadline =
            CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));

        using MemoryStream stream = new(content, writable: false);
        using XlsxCursor cursor = new(stream, cancellationToken: deadline.Token);

        List<string?[]> rows = [];

        while (cursor.ReadRow(deadline.Token))
        {
            string?[] row = new string?[cursor.CurrentRow.Length];

            for (int i = 0; i < row.Length; i++)
            {
                row[i] = cursor.CurrentRow[i].AsText();
            }

            rows.Add(row);
        }

        return rows;
    }

    [Fact]
    public void ReadsAnEmptySharedStringInsteadOfLoopingOnIt()
    {
        // An empty string in the shared table is written <si><t/></si>. The item reader took the
        // empty <t/> as consumed without advancing past it, found the same element again, and did so
        // forever: without a cancellation token the read never returned.
        byte[] content = new XlsxPackage()
            .WithSharedStrings("""<si><t>a</t></si><si><t/></si><si><t>b</t></si>""")
            .WithSheet(
                "Sheet1",
                """<row r="1"><c r="A1" t="s"><v>0</v></c><c r="B1" t="s"><v>1</v></c><c r="C1" t="s"><v>2</v></c></row>""")
            .Build();

        string?[] row = Assert.Single(ReadAll(content));

        Assert.Equal(3, row.Length);
        Assert.Equal("a", row[0]);
        Assert.True(string.IsNullOrEmpty(row[1]));
        Assert.Equal("b", row[2]);
    }
}
