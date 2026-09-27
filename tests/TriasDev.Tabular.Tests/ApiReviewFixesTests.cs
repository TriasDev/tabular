using System.IO.Compression;
using System.Text;
using System.Text.Json;

using TriasDev.Tabular.Archive;
using TriasDev.Tabular.Csv;
using TriasDev.Tabular.Ods;
using TriasDev.Tabular.Tests.Fixtures;

using Xunit;

namespace TriasDev.Tabular.Tests;

/// <summary>What the final reviews of the 1.0 API found, each pinned where it lives in the public API.</summary>
public sealed class ApiReviewFixesTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static string Row(string text) =>
        $"<table:table-row><table:table-cell office:value-type=\"string\"><text:p>{text}</text:p></table:table-cell></table:table-row>";

    [Fact]
    public void MovesAgainCleanlyAfterAnOdsMoveWasStoppedAtAnyNode()
    {
        // A move stopped by the token used to lose the node it had just read; when that node opened or
        // closed a table, the next move counted the tables wrongly and called a good file corrupt. The
        // padding walks the stop — the scan's first checkpoint, 4096 nodes in — across the nodes around
        // the second table's start.
        List<int> stopped = [];

        for (int padding = 4075; padding <= 4095; padding++)
        {
            string columns = string.Concat(Enumerable.Repeat("<table:table-column/>", padding));
            byte[] package = new OdsPackage()
                .WithRawTable($"""<table:table table:name="A">{columns}{Row("a")}</table:table>""")
                .WithTable("B", Row("b"))
                .WithTable("C", Row("c"))
                .Build();

            using CancellationTokenSource cancel = new();
            CancelAfterStream stream = new(package, cancel);
            using OdsCursor cursor = new(stream);
            Assert.True(cursor.MoveToSheet(2, Token));

            // Moving back reopens the part from the top; its first bytes cancel the token, after the
            // move's entry check and before the scan's first checkpoint.
            stream.CancelOnNextRead();
            bool reached;

            try
            {
                reached = cursor.MoveToSheet(1, cancel.Token);   // B lies before the first checkpoint
            }
            catch (OperationCanceledException)
            {
                reached = false;
                stopped.Add(padding);

                InvalidOperationException refused = Assert.Throws<InvalidOperationException>(() => cursor.ReadRow(Token));
                Assert.Contains("move", refused.Message, StringComparison.OrdinalIgnoreCase);
            }

            if (!reached)
            {
                Assert.True(cursor.MoveToSheet(1, Token), $"padding {padding}");
            }

            Assert.True(cursor.ReadRow(Token));
            Assert.Equal("b", cursor.CurrentRow[0].AsText());
        }

        // The checkpoint landed on the second table's start at 4083 when this was found.
        Assert.Contains(4083, stopped);
    }

    /// <summary>A stream that cancels a token on the first read after it is armed.</summary>
    private sealed class CancelAfterStream(byte[] content, CancellationTokenSource cancel) : MemoryStream(content, writable: false)
    {
        private bool _armed;

        public void CancelOnNextRead() => _armed = true;

        public override int Read(byte[] buffer, int offset, int count) => Fire(base.Read(buffer, offset, count));

        public override int Read(Span<byte> buffer) => Fire(base.Read(buffer));

        private int Fire(int read)
        {
            if (_armed)
            {
                cancel.Cancel();
            }

            return read;
        }
    }

    [Fact]
    public void RefusesToReadAfterAMoveInsideAWorkbookOfAnArchiveFailed()
    {
        // The workbook declares two sheets and holds the part of the first only. Moving to the second
        // opened the workbook and then failed inside it; the archive went on reading the workbook's
        // first sheet under the index of the sheet it had left.
        byte[] workbook = Without(
            new XlsxPackage()
                .WithSheet("One", """<row r="1"><c t="inlineStr"><is><t>one</t></is></c></row>""")
                .WithSheet("Two", """<row r="1"><c t="inlineStr"><is><t>two</t></is></c></row>""")
                .Build(),
            "xl/worksheets/sheet2.xml");

        using ArchiveCursor cursor = new(new MemoryStream(new ZipArchiveBuilder().With("a.csv", "h\n1\n").With("b.xlsx", workbook).Build()), null, Token);
        Assert.Equal(["a", "One", "Two"], cursor.Sheets.Select(s => s.Name));

        Assert.Throws<TabularFormatException>(() => cursor.MoveToSheet(2, Token));
        Assert.Throws<InvalidOperationException>(() => cursor.ReadRow(Token));
    }

    [Fact]
    public void RefusesATypedFieldDeclaredWithAnotherType()
    {
        DateImportField misdeclared = new() { Name = "d", Type = ColumnType.Text };
        ImportSchema schema = new() { Fields = [misdeclared] };
        MappingPlan plan = new() { Bindings = [new ColumnBinding { ColumnIndex = 0, FieldName = "d", Header = "d" }] };

        ArgumentException error = Assert.Throws<ArgumentException>(() => TabularImporter.Import(
            new MemoryStream(Encoding.UTF8.GetBytes("d\nhello\n")), "t.csv", plan, schema, row => row[misdeclared], cancellationToken: Token));

        Assert.Contains("d", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void NamesTheCountsAndIndicesItsArgumentsHold()
    {
        Assert.Equal("judgedCount", PrecheckArguments.JudgedCount);
        Assert.Equal("profileHeaderRowIndex", PrecheckArguments.ProfileHeaderRowIndex);
        Assert.Equal("planHeaderRowIndex", PrecheckArguments.PlanHeaderRowIndex);
        Assert.Equal("boundColumnCount", PrecheckArguments.BoundColumnCount);
    }

    [Fact]
    public void JudgesATypeMismatchAgainstEveryValueItJudged()
    {
        ImportSchema schema = new() { Fields = [ImportField.Integer("n")] };
        MappingPlan plan = new() { Bindings = [new ColumnBinding { ColumnIndex = 0, FieldName = "n", Header = "n" }] };

        PrecheckFinding finding = Assert.Single(MappingPrecheck.Check(plan, schema, Profile("n\n1\n2\nx\n")).Findings);

        Assert.Equal("1", finding.Arguments[PrecheckArguments.FailingCount]);
        Assert.Equal("3", finding.Arguments[PrecheckArguments.JudgedCount]);
        Assert.Equal("integer", finding.Arguments[PrecheckArguments.Type]);
        Assert.Equal(string.Empty, finding.Arguments[PrecheckArguments.Culture]);
    }

    [Fact]
    public void CountsTheColumnsBoundToAGroupNobodyFilled()
    {
        TranslatedImportField title = ImportField.Translated("title", ["en", "de"]).Require();
        ImportSchema schema = new() { Fields = [.. title] };
        MappingPlan plan = new()
        {
            Bindings =
            [
                new ColumnBinding { ColumnIndex = 0, FieldName = "title.en", Header = "en" },
                new ColumnBinding { ColumnIndex = 1, FieldName = "title.de", Header = "de" },
                new ColumnBinding { ColumnIndex = 2, FieldName = "other", Header = "x" },
            ],
        };

        PrecheckFinding finding = Assert.Single(
            MappingPrecheck.Check(plan with { Bindings = plan.Bindings.Take(2).ToList() }, schema, Profile("en;de;x\n;;1\n;;2\n")).Findings,
            f => f.Code == ErrorCodes.Group.Required);

        Assert.Equal("2", finding.Arguments[PrecheckArguments.BoundColumnCount]);
    }

    [Fact]
    public void NamesACultureTheRuntimeLacksAndOneTheProfileWasNotMeasuredUnder()
    {
        ImportSchema schema = new() { Fields = [ImportField.Decimal("n")] };
        MappingPlan plan = new() { Bindings = [new ColumnBinding { ColumnIndex = 0, FieldName = "n", Header = "n" }] };
        FileProfile profile = Profile("n\n1\n");

        PrecheckFinding unknown = Assert.Single(MappingPrecheck.Check(plan with { Culture = "xx-NOPE" }, schema, profile).Findings);
        Assert.Equal(ErrorCodes.Mapping.UnknownCulture, unknown.Code);
        Assert.Equal("xx-NOPE", unknown.Arguments[PrecheckArguments.Culture]);

        PrecheckFinding unprofiled = Assert.Single(MappingPrecheck.Check(plan with { Culture = "fr-FR" }, schema, profile).Findings);
        Assert.Equal(PrecheckReasons.NotProfiled, unprofiled.Arguments[PrecheckArguments.Reason]);
        Assert.Equal("fr-FR", unprofiled.Arguments[PrecheckArguments.Culture]);
    }

    [Fact]
    public void RoundTripsAPlanThroughJsonAsAnEqualOne()
    {
        MappingPlan plan = new()
        {
            SheetIndex = 1,
            Culture = "de-DE",
            SheetName = "Orders",
            Bindings = [new ColumnBinding { ColumnIndex = 0, FieldName = "a", Header = "A", TreatAsEmpty = ["k.A.", "-"] }],
        };

        MappingPlan? back = JsonSerializer.Deserialize<MappingPlan>(JsonSerializer.Serialize(plan));

        Assert.Equal(plan, back);
        Assert.Equal(plan.GetHashCode(), back!.GetHashCode());
    }

    [Fact]
    public void SaysAMoveWasStoppedWhenRefusingToReadAfterIt()
    {
        byte[] package = new OdsPackage().WithTable("A", Row("a")).WithTable("B", Row("b")).Build();
        using OdsCursor cursor = new(new MemoryStream(package));

        Assert.ThrowsAny<OperationCanceledException>(() => cursor.MoveToSheet(1, new CancellationToken(canceled: true)));

        // Stopped at its entry, the move changed nothing, and the cursor reads on where it was.
        Assert.True(cursor.ReadRow(Token));
        Assert.Equal("a", cursor.CurrentRow[0].AsText());
    }

    private static FileProfile Profile(string csv)
    {
        using CsvCursor cursor = new(new MemoryStream(Encoding.UTF8.GetBytes(csv)), "t.csv");
        return TabularAnalyzer.Analyze(cursor, cancellationToken: Token);
    }

    private static byte[] Without(byte[] zip, string entryName)
    {
        ZipArchiveBuilder builder = new();

        using ZipArchive source = new(new MemoryStream(zip), ZipArchiveMode.Read);

        foreach (ZipArchiveEntry entry in source.Entries.Where(e => e.FullName != entryName))
        {
            using Stream content = entry.Open();
            using MemoryStream copy = new();
            content.CopyTo(copy);
            builder.With(entry.FullName, copy.ToArray());
        }

        return builder.Build();
    }
}
