using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;

using TriasDev.Tabular.Tests.Fixtures;

using Xunit;

namespace TriasDev.Tabular.Tests.Writing;

/// <summary>LibreOffice reads the row outline we write: its groups, and the rows it hides.</summary>
public sealed class OutlineInteropTests
{
    private static readonly int[] Levels = [0, 1, 2, 1, 0];

    private static Task<byte[]> Write(TabularFormat format, SummaryRows summary) =>
        SheetLayoutTests.Write(format, writer =>
        {
            writer.BeginSheet(
                "data",
                [new("name"), new("count")],
                new SheetOptions { Outline = new RowOutline { Levels = 2, SummaryRows = summary, ShownLevels = 1 } });

            for (int i = 0; i < Levels.Length; i++)
            {
                writer.BeginRow(Levels[i]);
                writer.Write($"row {i}");
                writer.Write((long)i);
                writer.EndRow();
            }
        });

    private static string Content(byte[] ods)
    {
        using ZipArchive archive = new(new MemoryStream(ods, writable: false), ZipArchiveMode.Read);
        using StreamReader reader = new(archive.GetEntry("content.xml")!.Open(), Encoding.UTF8);
        return reader.ReadToEnd();
    }

    [Theory]
    [InlineData(TabularFormat.Xlsx, "xlsx", SummaryRows.Above)]
    [InlineData(TabularFormat.Xlsx, "xlsx", SummaryRows.Below)]
    [InlineData(TabularFormat.Ods, "ods", SummaryRows.Above)]
    public async Task LibreOfficeUnderstandsTheOutline(TabularFormat format, string extension, SummaryRows summary)
    {
        byte[] file = await Write(format, summary);

        string content = Content(LibreOffice.Resave(file, extension));

        // A level-1 group holding a level-2 group, around rows 3 to 5 (rows 1 and 2 are the header and the level-0
        // parent): the outer group opens first, the inner closes before the outer, rows 3 to 5 are hidden, and both
        // groups are collapsed. From xlsx, LibreOffice sees a group collapsed only by the mark on the row after it,
        // whichever side the summary rows are declared on, so the data ends on a level-0 row.
        Assert.Matches(
            "<table:table-row-group[^>]*>(?:(?!</table:table-row-group>).)*<table:table-row-group[^>]*>(?:(?!</table:table-row-group>).)*</table:table-row-group>(?:(?!</table:table-row-group>).)*</table:table-row-group>",
            content);
        Assert.Equal(2, Regex.Count(content, "<table:table-row-group"));
        Assert.Equal(3, Regex.Count(content, "table:visibility=\"collapse\""));
        Assert.Equal(2, Regex.Count(content, "<table:table-row-group table:display=\"false\""));
    }

    [Theory]
    [InlineData(TabularFormat.Xlsx, "xlsx", SummaryRows.Above)]
    [InlineData(TabularFormat.Xlsx, "xlsx", SummaryRows.Below)]
    [InlineData(TabularFormat.Ods, "ods", SummaryRows.Above)]
    public async Task LibreOfficeShowsEveryValue(TabularFormat format, string extension, SummaryRows summary)
    {
        byte[] file = await Write(format, summary);

        string[] lines = LibreOffice.ConvertToCsv(file, extension);

        Assert.Equal(["\"name\",\"count\"", "\"row 0\",0", "\"row 1\",1", "\"row 2\",2", "\"row 3\",3", "\"row 4\",4"], lines);
    }
}
