using System.Xml;

using TriasDev.Tabular.Tests.Fixtures;

using Xunit;

namespace TriasDev.Tabular.Tests.Writing;

/// <summary>The row outline in xlsx: the sheet head, each row's level, the rows hidden and the groups collapsed.</summary>
public sealed class XlsxOutlineTests
{
    private const string Sheet1 = "xl/worksheets/sheet1.xml";

    /// <summary>The data rows' levels: rows 2 to 6, the header being row 1.</summary>
    private static readonly int[] Levels = [0, 1, 2, 1, 0];

    private static readonly TextImportField NameField = ImportField.Text("name");
    private static readonly IntegerImportField CountField = ImportField.Integer("count");
    private static readonly ImportSchema Schema = new() { Fields = [NameField, CountField] };

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static RowOutline Outline(SummaryRows summary = SummaryRows.Above, int? shown = null, int levels = 2) =>
        new() { Levels = levels, SummaryRows = summary, ShownLevels = shown };

    /// <summary>One sheet of two columns, a header, then one row per level; an outline of null writes every row with <c>BeginRow()</c>.</summary>
    private static Task<byte[]> Write(RowOutline? outline, int[]? levels = null, SheetOptions? options = null) =>
        SheetLayoutTests.Write(TabularFormat.Xlsx, writer => Sheet(writer, "data", outline, levels ?? Levels, options));

    private static void Sheet(TabularWriter writer, string name, RowOutline? outline, int[] levels, SheetOptions? options = null)
    {
        writer.BeginSheet(name, [new("name"), new("count")], (options ?? new SheetOptions()) with { Outline = outline });

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

    /// <summary>Each <c>&lt;row&gt;</c>'s attributes by row number, read by a conforming XML parser.</summary>
    private static Dictionary<int, Dictionary<string, string>> RowAttributes(string xml)
    {
        Dictionary<int, Dictionary<string, string>> rows = [];
        using XmlReader reader = XmlReader.Create(new StringReader(xml));

        while (reader.Read())
        {
            if (reader.NodeType != XmlNodeType.Element || reader.LocalName != "row")
            {
                continue;
            }

            Dictionary<string, string> attributes = [];

            while (reader.MoveToNextAttribute())
            {
                attributes[reader.LocalName] = reader.Value;
            }

            rows[int.Parse(attributes["r"], System.Globalization.CultureInfo.InvariantCulture)] = attributes;
        }

        return rows;
    }

    /// <summary>The numbers of the rows that carry <paramref name="attribute"/>, with its value.</summary>
    private static List<(int Row, string Value)> Carrying(byte[] xlsx, string attribute) =>
        [.. RowAttributes(SheetLayoutTests.Entry(xlsx, Sheet1))
            .Where(row => row.Value.ContainsKey(attribute))
            .Select(row => (row.Key, row.Value[attribute]))
            .Order()];

    private static List<(string? Name, long? Count)> Import(byte[] file)
    {
        MappingPlan plan = new() { Bindings = [.. Schema.Fields.Select((f, i) => new ColumnBinding { ColumnIndex = i, Header = f.Name, FieldName = f.Name })] };
        using ImportRun<(string?, long?)> run = TabularImporter.Import(new MemoryStream(file, writable: false), "t.xlsx", plan, Schema, row => (row[NameField], row[CountField]), cancellationToken: Token);
        List<(string?, long?)> rows = [.. run.ReadRows(Token).Select(outcome => outcome.Value)];
        Assert.Equal(0, run.Summary.RowsSkipped);
        return rows;
    }

    [Fact]
    public async Task HeadKeepsSchemaOrderWithFreezeAndWidths()
    {
        byte[] xlsx = await SheetLayoutTests.Write(TabularFormat.Xlsx, writer =>
        {
            writer.BeginSheet("data", [new("name", 20), new("count")], new SheetOptions { FreezeRows = 1, Outline = Outline() });
            writer.BeginRow(1);
            writer.Write("x");
            writer.EndRow();
        });
        string sheet = SheetLayoutTests.Entry(xlsx, Sheet1);

        int[] at =
        [
            sheet.IndexOf("<sheetPr><outlinePr summaryBelow=\"0\"/></sheetPr>", StringComparison.Ordinal),
            sheet.IndexOf("<sheetViews>", StringComparison.Ordinal),
            sheet.IndexOf("<sheetFormatPr defaultRowHeight=\"15\" outlineLevelRow=\"2\"/>", StringComparison.Ordinal),
            sheet.IndexOf("<cols>", StringComparison.Ordinal),
            sheet.IndexOf("<sheetData>", StringComparison.Ordinal),
        ];

        Assert.True(at[0] > 0, sheet);
        Assert.Equal(at.Order(), at);
        Assert.Equal(at.Length, at.Distinct().Count());
        Assert.Empty(OoxmlValidation.Errors(xlsx));
    }

    [Fact]
    public async Task SummaryBelowWritesNoSheetPr()
    {
        byte[] xlsx = await Write(Outline(SummaryRows.Below));
        string sheet = SheetLayoutTests.Entry(xlsx, Sheet1);

        Assert.DoesNotContain("<sheetPr", sheet, StringComparison.Ordinal);
        Assert.Contains("<sheetFormatPr defaultRowHeight=\"15\" outlineLevelRow=\"2\"/>", sheet, StringComparison.Ordinal);
        Assert.Empty(OoxmlValidation.Errors(xlsx));
    }

    [Theory]
    [InlineData(SummaryRows.Above)]
    [InlineData(SummaryRows.Below)]
    public async Task RowsCarryTheirLevel(SummaryRows summary)
    {
        byte[] xlsx = await Write(Outline(summary));
        string sheet = SheetLayoutTests.Entry(xlsx, Sheet1);

        Assert.Equal([(3, "1"), (4, "2"), (5, "1")], Carrying(xlsx, "outlineLevel"));
        Assert.DoesNotContain("hidden", sheet, StringComparison.Ordinal);
        Assert.DoesNotContain("collapsed", sheet, StringComparison.Ordinal);
        Assert.Empty(OoxmlValidation.Errors(xlsx));
    }

    [Theory]
    [InlineData(SummaryRows.Above, 1, new[] { 3, 4, 5 })]
    [InlineData(SummaryRows.Below, 1, new[] { 3, 4, 5 })]
    [InlineData(SummaryRows.Above, 2, new[] { 4 })]
    [InlineData(SummaryRows.Below, 2, new[] { 4 })]
    public async Task ShownLevelsHidesDeeperRows(SummaryRows summary, int shown, int[] hidden)
    {
        byte[] xlsx = await Write(Outline(summary, shown));

        Assert.Equal(hidden.Select(row => (row, "1")), Carrying(xlsx, "hidden"));
        Assert.Empty(OoxmlValidation.Errors(xlsx));
    }

    /// <summary>
    /// With summary rows below, a collapsed group's "+" sits on the row after it — and on a nested summary
    /// row inside a hidden group, so the group expanded by hand still shows its own child collapsed.
    /// </summary>
    [Theory]
    [InlineData(1, new[] { 5, 6 })]
    [InlineData(2, new[] { 5 })]
    public async Task CollapsedBelowMarksTheRowAfterTheGroup(int shown, int[] collapsed)
    {
        byte[] xlsx = await Write(Outline(SummaryRows.Below, shown));

        Assert.Equal(collapsed.Select(row => (row, "1")), Carrying(xlsx, "collapsed"));
        Assert.Empty(OoxmlValidation.Errors(xlsx));
    }

    /// <summary>
    /// A collapsed group that ends the sheet has no row below it to carry its "+". Excel would write a
    /// cell-less row after it; the cursor reads that as one more, empty row, so the writer leaves it out
    /// (docs/KNOWN-ISSUES.md) and the group opens showing "−" over its hidden rows.
    /// </summary>
    [Fact]
    public async Task TrailingGroupBelow()
    {
        int[] levels = [0, 1, 2, 1];
        byte[] outlined = await Write(Outline(SummaryRows.Below, 1), levels);
        byte[] plain = await Write(null, levels);

        Assert.Equal([1, 2, 3, 4, 5], RowAttributes(SheetLayoutTests.Entry(outlined, Sheet1)).Keys.Order());
        Assert.Equal([(5, "1")], Carrying(outlined, "collapsed"));
        Assert.Equal(SheetLayoutTests.Rows(plain), SheetLayoutTests.Rows(outlined));
        Assert.Equal(Import(plain), Import(outlined));
        Assert.Empty(OoxmlValidation.Errors(outlined));
    }

    [Fact]
    public async Task OutlineWithoutDeeperRowsWritesNoGroups()
    {
        byte[] headerOnly = await Write(Outline(levels: 1), []);
        byte[] levelZero = await Write(Outline(levels: 1, shown: 1), [0, 0, 0]);

        foreach (byte[] xlsx in new[] { headerOnly, levelZero })
        {
            string sheet = SheetLayoutTests.Entry(xlsx, Sheet1);
            Assert.DoesNotContain("outlineLevel=", sheet, StringComparison.Ordinal);
            Assert.DoesNotContain("hidden", sheet, StringComparison.Ordinal);
            Assert.DoesNotContain("collapsed", sheet, StringComparison.Ordinal);
            Assert.Contains("outlineLevelRow=\"1\"", sheet, StringComparison.Ordinal);
            Assert.Empty(OoxmlValidation.Errors(xlsx));
        }

        Assert.Single(SheetLayoutTests.Rows(headerOnly));
        Assert.Equal(4, SheetLayoutTests.Rows(levelZero).Count);
    }

    [Theory]
    [InlineData(SummaryRows.Above)]
    [InlineData(SummaryRows.Below)]
    public async Task NextSheetStartsWithoutOutlineState(SummaryRows summary)
    {
        // The first sheet ends inside a hidden group, so state carried over would mark the next sheet's rows.
        byte[] both = await SheetLayoutTests.Write(TabularFormat.Xlsx, writer =>
        {
            Sheet(writer, "outlined", Outline(summary, 1), [0, 1, 2, 1]);
            Sheet(writer, "plain", null, Levels);
        });
        byte[] alone = await SheetLayoutTests.Write(TabularFormat.Xlsx, writer => Sheet(writer, "plain", null, Levels));

        Assert.Equal(SheetLayoutTests.Entry(alone, Sheet1), SheetLayoutTests.Entry(both, "xl/worksheets/sheet2.xml"));
        Assert.Empty(OoxmlValidation.Errors(both));
    }

    public static TheoryData<SummaryRows, int?> Outlines() => new()
    {
        { SummaryRows.Above, null },
        { SummaryRows.Above, 1 },
        { SummaryRows.Above, 2 },
        { SummaryRows.Below, null },
        { SummaryRows.Below, 1 },
        { SummaryRows.Below, 2 },
    };

    [Theory]
    [MemberData(nameof(Outlines))]
    public async Task ReadsBackTheSameRows(SummaryRows summary, int? shown)
    {
        int[] levels = [0, 1, 2, 2, 1, 0, 2, 0, 1];
        byte[] outlined = await Write(Outline(summary, shown), levels, new SheetOptions { AutoFilter = true, FreezeRows = 1 });
        byte[] plain = await Write(null, levels, new SheetOptions { AutoFilter = true, FreezeRows = 1 });

        Assert.Equal(levels.Length + 1, SheetLayoutTests.Rows(outlined).Count);
        Assert.Equal(SheetLayoutTests.Rows(plain), SheetLayoutTests.Rows(outlined));
        Assert.Equal(Import(plain), Import(outlined));
        Assert.Equal(SheetLayoutTests.Entry(plain, "xl/workbook.xml"), SheetLayoutTests.Entry(outlined, "xl/workbook.xml"));
        Assert.Empty(OoxmlValidation.Errors(outlined));
    }

    /// <summary>
    /// With summary rows above, a collapsed group's "+" sits on the row before it — and on a nested
    /// parent row inside a hidden group, so the group expanded by hand still shows its own child collapsed —,
    /// and for LibreOffice on the row after it too (rows 5 and 6 with one level shown, row 5 with two).
    /// </summary>
    [Theory]
    [InlineData(1, new[] { 2, 3, 5, 6 })]
    [InlineData(2, new[] { 3, 5 })]
    public async Task CollapsedAboveMarksTheParentRow(int shown, int[] collapsed)
    {
        byte[] xlsx = await Write(Outline(SummaryRows.Above, shown));

        Assert.Equal(collapsed.Select(row => (row, "1")), Carrying(xlsx, "collapsed"));
        Assert.Equal([(3, "1"), (4, "2"), (5, "1")], Carrying(xlsx, "outlineLevel"));
        Assert.Empty(OoxmlValidation.Errors(xlsx));
    }

    /// <summary>
    /// With summary rows above, a collapsed group's "+" sits on the row before it for Excel and on the row after
    /// it for LibreOffice, which ignores <c>summaryBelow</c> and reads the mark from the row after the group.
    /// </summary>
    [Fact]
    public async Task CollapsedAboveAlsoMarksTheRowAfterTheGroup()
    {
        byte[] xlsx = await Write(Outline(SummaryRows.Above, 1, levels: 1), [0, 1, 1, 0]);

        Assert.Equal([(2, "1"), (5, "1")], Carrying(xlsx, "collapsed"));
        Assert.Equal([1, 2, 3, 4, 5], RowAttributes(SheetLayoutTests.Entry(xlsx, Sheet1)).Keys.Order());
        Assert.Empty(OoxmlValidation.Errors(xlsx));
    }

    /// <summary>
    /// A row after a hidden group that heads a shown group of its own is not marked: LibreOffice would read the
    /// mark as its own group's state and collapse it, and Excel shows that group expanded.
    /// </summary>
    [Fact]
    public async Task RowAfterAHiddenGroupHeadingAShownGroupIsNotMarked()
    {
        byte[] xlsx = await Write(Outline(SummaryRows.Above, 2, levels: 2), [0, 2, 0, 1]);

        Assert.Equal([(2, "1")], Carrying(xlsx, "collapsed"));
        Assert.Equal([(3, "1")], Carrying(xlsx, "hidden"));
        Assert.Empty(OoxmlValidation.Errors(xlsx));
    }

    /// <summary>
    /// A collapsed group that ends the sheet has no row after it for LibreOffice's mark; a cell-less row there would
    /// read back as one more row, so nothing is written after it (docs/KNOWN-ISSUES.md).
    /// </summary>
    [Fact]
    public async Task TrailingCollapsedGroupAboveWritesNothingAfter()
    {
        byte[] xlsx = await Write(Outline(SummaryRows.Above, 1, levels: 1), [0, 1]);
        string sheet = SheetLayoutTests.Entry(xlsx, Sheet1);

        Assert.Equal([(2, "1")], Carrying(xlsx, "collapsed"));
        Assert.Equal([1, 2, 3], RowAttributes(sheet).Keys.Order());
        Assert.DoesNotContain("<row r=\"4\"", sheet, StringComparison.Ordinal);
        Assert.Empty(OoxmlValidation.Errors(xlsx));
    }

    /// <summary>The row held back for its <c>collapsed</c> is written when the sheet ends, without the mark: no row follows it.</summary>
    [Theory]
    [InlineData(new[] { 0, 1 }, new[] { 2 })]
    [InlineData(new[] { 0, 0 }, new int[0])]
    public async Task LastHeldRowIsWritten(int[] levels, int[] collapsed)
    {
        byte[] xlsx = await Write(Outline(SummaryRows.Above, 1), levels);
        string sheet = SheetLayoutTests.Entry(xlsx, Sheet1);

        int last = sheet.IndexOf("<row r=\"3\"", StringComparison.Ordinal);
        Assert.True(last > 0, sheet);
        Assert.True(last < sheet.IndexOf("</sheetData>", StringComparison.Ordinal), sheet);
        Assert.Equal([1, 2, 3], RowAttributes(sheet).Keys.Order());
        Assert.Equal(collapsed.Select(row => (row, "1")), Carrying(xlsx, "collapsed"));
        Assert.Equal(["name", "row 0", "row 1"], SheetLayoutTests.Rows(xlsx).Select(row => row[0].Text));
        Assert.Empty(OoxmlValidation.Errors(xlsx));
    }

    [Fact]
    public async Task HeldRowPrecedesFilterAndMerges()
    {
        byte[] xlsx = await SheetLayoutTests.Write(TabularFormat.Xlsx, writer =>
        {
            writer.BeginSheet("data", [new("name"), new("count")], new SheetOptions { AutoFilter = true, Outline = Outline(SummaryRows.Above, 1) });
            writer.BeginRow(0);
            writer.Merge(1, 2);
            writer.Write("title");
            writer.EndRow();
            writer.BeginRow(1);
            writer.Write("child");
            writer.Write(1L);
            writer.EndRow();
        });
        string sheet = SheetLayoutTests.Entry(xlsx, Sheet1);

        int[] at =
        [
            sheet.IndexOf("<row r=\"3\" hidden=\"1\" outlineLevel=\"1\">", StringComparison.Ordinal),
            sheet.IndexOf("</sheetData>", StringComparison.Ordinal),
            sheet.IndexOf("<autoFilter ref=\"A1:B3\"/>", StringComparison.Ordinal),
            sheet.IndexOf("<mergeCells count=\"1\"><mergeCell ref=\"A2:B2\"/></mergeCells>", StringComparison.Ordinal),
        ];

        Assert.True(at[0] > 0, sheet);
        Assert.Equal(at.Order(), at);
        Assert.Equal(at.Length, at.Distinct().Count());
        Assert.Equal([(2, "1")], Carrying(xlsx, "collapsed"));
        Assert.Empty(OoxmlValidation.Errors(xlsx));
    }

    /// <summary><c>FlushAsync</c> drains only what is already encoded: the held row waits on, and the sheet is the same as one written without the flush.</summary>
    [Fact]
    public async Task HeldRowSurvivesFlush()
    {
        int[] levels = [0, 1, 2, 1, 0, 1];
        byte[] unflushed = await Write(Outline(SummaryRows.Above, 1), levels);

        WriteTarget target = new();
        await using (TabularWriter writer = TabularWriter.Create(target, TabularFormat.Xlsx))
        {
            writer.BeginSheet("data", [new("name"), new("count")], new SheetOptions { Outline = Outline(SummaryRows.Above, 1) });

            for (int i = 0; i < levels.Length; i++)
            {
                if (i == 3)
                {
                    await writer.FlushAsync(Token);
                }

                writer.BeginRow(levels[i]);
                writer.Write($"row {i}");
                writer.Write((long)i);
                writer.EndRow();
            }

            await writer.CompleteAsync(Token);
        }

        byte[] flushed = target.ToArray();
        string sheet = SheetLayoutTests.Entry(flushed, Sheet1);

        Assert.Equal(
            [1, 2, 3, 4, 5, 6, 7],
            System.Text.RegularExpressions.Regex.Matches(sheet, "<row r=\"([0-9]+)\"").Select(match => int.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture)));
        Assert.Equal(SheetLayoutTests.Entry(unflushed, Sheet1), sheet);
        Assert.Empty(OoxmlValidation.Errors(flushed));
    }

    /// <summary>
    /// A batch begins its rows at level 0: its first row ends the groups, so the row held before it is not
    /// collapsed, and it is the row after the hidden group, which carries the mark for LibreOffice.
    /// </summary>
    [Fact]
    public async Task BatchRowEndsGroups()
    {
        byte[] xlsx = await SheetLayoutTests.Write(TabularFormat.Xlsx, writer =>
        {
            writer.BeginSheet("data", [new("name"), new("count")], new SheetOptions { Outline = Outline(SummaryRows.Above, 1) });

            for (int level = 0; level < 2; level++)
            {
                writer.BeginRow(level);
                writer.Write($"row {level}");
                writer.Write((long)level);
                writer.EndRow();
            }

            ColumnBatch batch = new();
            batch.Reset(2);
            batch.Add(new List<string?> { "batch 0", "batch 1" });
            batch.Add(new long[] { 10, 11 });
            writer.WriteBatch(batch);
        });

        Assert.Equal([(2, "1"), (4, "1")], Carrying(xlsx, "collapsed"));
        Assert.Equal([(3, "1")], Carrying(xlsx, "hidden"));
        Assert.Equal([(3, "1")], Carrying(xlsx, "outlineLevel"));
        Assert.Equal(5, SheetLayoutTests.Rows(xlsx).Count);
        Assert.Empty(OoxmlValidation.Errors(xlsx));
    }

    /// <summary>Without <c>ShownLevels</c> nothing is collapsed, so no row waits: the sheet is the one written before rows were held.</summary>
    [Fact]
    public async Task AboveWithoutShownLevelsHoldsNothing()
    {
        const string Expected =
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
            "<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\">" +
            "<sheetPr><outlinePr summaryBelow=\"0\"/></sheetPr>" +
            "<sheetFormatPr defaultRowHeight=\"15\" outlineLevelRow=\"2\"/>" +
            "<cols><col min=\"1\" max=\"1\" width=\"7\" customWidth=\"1\"/><col min=\"2\" max=\"2\" width=\"7\" customWidth=\"1\"/></cols>" +
            "<sheetData>" +
            "<row r=\"1\"><c t=\"inlineStr\"><is><t>name</t></is></c><c t=\"inlineStr\"><is><t>count</t></is></c></row>" +
            "<row r=\"2\"><c t=\"inlineStr\"><is><t>row 0</t></is></c><c s=\"3\"><v>0</v></c></row>" +
            "<row r=\"3\" outlineLevel=\"1\"><c t=\"inlineStr\"><is><t>row 1</t></is></c><c s=\"3\"><v>1</v></c></row>" +
            "<row r=\"4\" outlineLevel=\"2\"><c t=\"inlineStr\"><is><t>row 2</t></is></c><c s=\"3\"><v>2</v></c></row>" +
            "<row r=\"5\" outlineLevel=\"1\"><c t=\"inlineStr\"><is><t>row 3</t></is></c><c s=\"3\"><v>3</v></c></row>" +
            "<row r=\"6\"><c t=\"inlineStr\"><is><t>row 4</t></is></c><c s=\"3\"><v>4</v></c></row>" +
            "</sheetData></worksheet>";

        byte[] xlsx = await Write(Outline(SummaryRows.Above));

        Assert.Equal(Expected, SheetLayoutTests.Entry(xlsx, Sheet1));
    }
}
