using System.Text.RegularExpressions;

using Xunit;

namespace TriasDev.Tabular.Tests.Writing;

/// <summary>The row outline in ods: nested row groups, collapsed groups and hidden rows.</summary>
public sealed partial class OdsOutlineTests
{
    private const string Open = "<table:table-row-group>";
    private const string OpenCollapsed = "<table:table-row-group table:display=\"false\">";
    private const string Close = "</table:table-row-group>";

    private static RowOutline Outline(SummaryRows summary = SummaryRows.Above, int? shown = null, int levels = 2) =>
        new() { Levels = levels, SummaryRows = summary, ShownLevels = shown };

    private static Task<byte[]> Write(RowOutline? outline, int[] levels, string[]? second = null) =>
        SheetLayoutTests.Write(TabularFormat.Ods, writer =>
        {
            Sheet(writer, "data", outline, levels);

            if (second is not null)
            {
                Sheet(writer, "more", outline, [0]);
            }
        });

    private static void Sheet(TabularWriter writer, string name, RowOutline? outline, int[] levels)
    {
        writer.BeginSheet(name, [new("name"), new("count")], new SheetOptions { Outline = outline });

        for (int i = 0; i < levels.Length; i++)
        {
            if (outline is null)
            {
                writer.BeginRow();
            }
            else
            {
                writer.BeginRow(levels[i]);
            }

            writer.Write($"row {i}");
            writer.Write((long)i);
            writer.EndRow();
        }
    }

    private static string Content(byte[] ods) => SheetLayoutTests.Entry(ods, "content.xml");

    /// <summary>The content reduced to its row and group markers, one per line.</summary>
    private static string Markers(byte[] ods) =>
        string.Join('\n', Marker().Matches(Content(ods)).Select(match => match.Value));

    [GeneratedRegex("<table:table-row-group[^>]*>|</table:table-row-group>|<table:table-row(?: [^>]*)?>|</table:table>")]
    private static partial Regex Marker();

    [Fact]
    public async Task JumpAndFallNestCorrectly()
    {
        byte[] ods = await Write(Outline(), [0, 2, 1, 0]);

        // The header row the sheet writes itself, then the rows at levels 0, 2, 1, 0.
        string expected = string.Join(
            '\n',
            "<table:table-row>",
            "<table:table-row>",
            Open,
            Open,
            "<table:table-row>",
            Close,
            "<table:table-row>",
            Close,
            "<table:table-row>",
            "</table:table>");
        Assert.Equal(expected, Markers(ods));
    }

    [Fact]
    public async Task ShownLevelsCollapses()
    {
        byte[] one = await Write(Outline(shown: 1), [0, 1, 2, 1, 0]);
        string text = Markers(one);
        Assert.Equal(2, text.Split(OpenCollapsed).Length - 1);
        Assert.DoesNotContain(Open, text);
        Assert.Equal(3, text.Split("table:visibility=\"collapse\"").Length - 1);

        byte[] two = await Write(Outline(shown: 2), [0, 1, 2, 1, 0]);
        text = Markers(two);
        Assert.Equal(1, text.Split(OpenCollapsed).Length - 1);
        Assert.Equal(1, text.Split(Open).Length - 1);
        Assert.Equal(1, text.Split("table:visibility=\"collapse\"").Length - 1);
        Assert.Contains("<table:table-row table:visibility=\"collapse\">", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SummaryRowsWritesNothing()
    {
        int[] levels = [0, 1, 2, 1, 0];
        Assert.Equal(
            Content(await Write(Outline(SummaryRows.Above, 1), levels)),
            Content(await Write(Outline(SummaryRows.Below, 1), levels)));
    }

    [Fact]
    public async Task GroupsCloseAtNextSheetAndAtCompletion()
    {
        byte[] ods = await Write(Outline(), [0, 1, 2], second: ["x"]);
        string content = Content(ods);
        int firstEnd = content.IndexOf("</table:table>", StringComparison.Ordinal);
        Assert.EndsWith(Close + Close, content[..firstEnd], StringComparison.Ordinal);

        byte[] last = await SheetLayoutTests.Write(TabularFormat.Ods, writer => Sheet(writer, "data", Outline(), [0, 2]));
        string lastContent = Content(last);
        int end = lastContent.IndexOf("</table:table>", StringComparison.Ordinal);
        Assert.EndsWith(Close + Close, lastContent[..end], StringComparison.Ordinal);
        Assert.Equal(2, lastContent.Split(Open).Length - 1);
    }

    [Fact]
    public async Task OutlineWithoutDeeperRowsWritesNoGroups()
    {
        Assert.DoesNotContain("table-row-group", Content(await Write(Outline(), [])), StringComparison.Ordinal);
        Assert.DoesNotContain("table-row-group", Content(await Write(Outline(shown: 1), [0, 0, 0])), StringComparison.Ordinal);
    }

    [Fact]
    public async Task BatchRowEndsGroups()
    {
        byte[] ods = await SheetLayoutTests.Write(TabularFormat.Ods, writer =>
        {
            writer.BeginSheet("data", [new("name"), new("count")], new SheetOptions { Outline = Outline() });

            foreach (int level in new[] { 0, 2 })
            {
                writer.BeginRow(level);
                writer.Write("r");
                writer.Write((long)level);
                writer.EndRow();
            }

            ColumnBatch batch = new();
            batch.Reset(1);
            batch.Add(new List<string?> { "batch" });
            batch.Add(new long[] { 10 });
            writer.WriteBatch(batch);
        });

        string content = Content(ods);
        int batchRow = content.LastIndexOf("<table:table-row>", StringComparison.Ordinal);
        Assert.EndsWith(Close + Close, content[..batchRow], StringComparison.Ordinal);
        Assert.Equal(2, content.Split(Close).Length - 1);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task ReadsBackTheSameRows(int? shown)
    {
        int[] levels = [0, 1, 2, 2, 1, 0, 2, 0, 1];
        byte[] outlined = await Write(Outline(shown: shown), levels);
        byte[] plain = await Write(null, levels);

        Assert.Equal(SheetLayoutTests.Rows(plain), SheetLayoutTests.Rows(outlined));
    }
}
