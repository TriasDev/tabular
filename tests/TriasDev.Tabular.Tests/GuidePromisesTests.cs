using System.Text;

using TriasDev.Tabular.Csv;
using TriasDev.Tabular.Tests.Fixtures;
using TriasDev.Tabular.Xlsx;

using Xunit;

namespace TriasDev.Tabular.Tests;

/// <summary>
/// Behaviour the documentation (docs/*.md) promises that nothing else pins.
/// </summary>
public sealed class GuidePromisesTests
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private static FileProfile Profile(string csv, AnalysisOptions? options = null)
    {
        using CsvCursor cursor = new(new MemoryStream(Utf8NoBom.GetBytes(csv), writable: false), "t.csv");
        return new TabularAnalyzer(options).Analyze(cursor, cancellationToken: TestContext.Current.CancellationToken);
    }

    private static MappingPlan OneBinding(string field, string? culture = null, int headerRow = 0) => new()
    {
        Culture = culture,
        HeaderRowIndex = headerRow,
        Bindings = [new ColumnBinding { SourceColumnIndex = 0, SourceHeader = "v", TargetFieldName = field }],
    };

    [Fact]
    public void RefusesASheetNameLongerThanTheMetadataCeiling()
    {
        byte[] workbook = new XlsxPackage()
            .WithSheet(new string('n', 40), """<row r="1"><c r="A1" t="inlineStr"><is><t>a</t></is></c></row>""")
            .Build();

        TabularLimitException error = Assert.Throws<TabularLimitException>(() =>
            new XlsxCursor(new MemoryStream(workbook), new XlsxCursorOptions { MaxMetadataChars = 31 }, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal((nameof(XlsxCursorOptions.MaxMetadataChars), 31L), (error.Limit, error.Maximum));
    }

    [Fact]
    public void RefusesAHeaderRowBeyondTheSheetsEnd()
    {
        using CsvCursor cursor = new(new MemoryStream(Utf8NoBom.GetBytes("v\na\n"), writable: false), "t.csv");
        TargetSchema schema = new() { Fields = [ImportField.Text("f")] };

        TabularStructureException error = Assert.Throws<TabularStructureException>(() =>
            TabularExtractor.Start(cursor, OneBinding("f", headerRow: 5), schema, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(TabularStructureException.HeaderRowMissing, error.Code);
    }

    [Fact]
    public void LeavesATypeUndeterminedUnderACultureTheFileWasNotProfiledUnder()
    {
        // Profiled under the invariant culture alone; the plan reads German. Nothing measured says
        // how the values read under de-DE, and a guess either way would be a claim about the file.
        FileProfile profile = Profile("v\n1,5\n2,5\n", new AnalysisOptions { Cultures = [""] });
        TargetSchema schema = new() { Fields = [ImportField.Decimal("amount")] };

        PrecheckResult result = MappingPrecheck.Check(OneBinding("amount", "de-DE"), schema, profile);

        PrecheckFinding finding = Assert.Single(result.Findings);
        Assert.Equal((ErrorCodes.Value.TypeMismatch, PrecheckSeverity.Undetermined), (finding.Code, finding.Severity));
        Assert.True(result.CanImport);
    }

    [Fact]
    public void LeavesUniquenessUndeterminedWhenTheDistinctBudgetRanOut()
    {
        FileProfile profile = Profile(
            "v\n" + string.Concat(Enumerable.Range(0, 50).Select(i => $"id{i}\n")),
            new AnalysisOptions { DistinctTrackingBudget = 10 });
        TargetSchema schema = new() { Fields = [ImportField.Text("id").Unique()] };

        PrecheckResult result = MappingPrecheck.Check(OneBinding("id"), schema, profile);

        PrecheckFinding finding = Assert.Single(result.Findings);
        Assert.Equal((ErrorCodes.Value.NotUnique, PrecheckSeverity.Undetermined), (finding.Code, finding.Severity));
    }
}
