using System.Text;

using TriasDev.Tabular.Analysis;
using TriasDev.Tabular.Import;
using TriasDev.Tabular.Mapping;
using TriasDev.Tabular.Tests.Fixtures;
using TriasDev.Tabular.Xlsx;

using Xunit;

namespace TriasDev.Tabular.Tests.Import;

/// <summary>
/// Every other precheck test in this suite reads a csv, and a csv cell has no declared kind — so the
/// suite whose job is keeping the precheck and the import in agreement was structurally blind to the
/// only place they disagreed. These read workbooks.
/// </summary>
public sealed class PrecheckReadsXlsxCellsTests
{
    [Fact]
    public void DoesNotRereadAWorkbooksOwnNumberUnderTheMappingsCulture()
    {
        // The profile keeps a distinct value as text, rendered invariantly. Read back under de-DE,
        // where the point is a group separator, 1234.5 becomes 12345 — so the precheck refused a
        // German workbook that imports perfectly. The extractor never parses such a cell at all.
        TargetSchema schema = new() { Fields = [ImportField.Decimal("amount").AtMost(9999)] };

        MappingPlan plan = Plan("amount") with { Culture = "de-DE" };

        PrecheckResult result = MappingPrecheck.Check(plan, schema, Profile("1234.5", "2345.5"));

        Assert.True(result.CanImport);
        Assert.Empty(result.Findings);
    }

    [Fact]
    public void StillRefusesAWorkbooksOwnNumberThatIsGenuinelyOutOfRange()
    {
        TargetSchema schema = new() { Fields = [ImportField.Decimal("amount").AtMost(100)] };

        MappingPlan plan = Plan("amount") with { Culture = "de-DE" };

        Assert.False(MappingPrecheck.Check(plan, schema, Profile("1234.5", "2345.5")).CanImport);
    }

    [Fact]
    public void SpeaksAboutAWorkbooksOwnNumberTooLargeToReadAsText()
    {
        // Rendered as 1E+17, which NumberStyles.Number rejects — so the value used to be dropped as
        // unreadable and every rule about it skipped, while the import read the double natively and
        // failed every row.
        TargetSchema schema = new() { Fields = [ImportField.Decimal("amount").AtMost(5)] };

        PrecheckResult result = MappingPrecheck.Check(
            Plan("amount"),
            schema,
            Profile("1E+17", "2E+17"));

        Assert.False(result.CanImport);
    }

    [Fact]
    public void ReadsAWorkbooksOwnDateAsTheDateItIs()
    {
        TargetSchema schema = new() { Fields = [ImportField.Date("when")] };

        PrecheckResult result = MappingPrecheck.Check(
            Plan("when") with { Culture = "de-DE" },
            schema,
            Profile("d:2023-01-15", "d:2023-02-20"));

        Assert.True(result.CanImport);
    }

    private static MappingPlan Plan(string field) =>
        new()
        {
            Bindings =
            [
                new ColumnBinding { SourceColumnIndex = 0, SourceHeader = "v", TargetFieldName = field },
            ],
        };

    /// <summary>A one-column workbook whose header is text and whose cells are as given.</summary>
    /// <param name="cells">
    /// Each entry is a cell's value. Prefix it with <c>d:</c> for a cell the file types as a date;
    /// anything else is a bare value, which in a workbook means a number.
    /// </param>
    private static FileProfile Profile(params string[] cells)
    {
        StringBuilder rows = new("<row><c t=\"inlineStr\"><is><t>v</t></is></c></row>");

        foreach (string cell in cells)
        {
            rows.Append("<row>");

            rows.Append(cell.StartsWith("d:", StringComparison.Ordinal)
                ? $"<c t=\"d\"><v>{cell[2..]}</v></c>"
                : $"<c><v>{cell}</v></c>");

            rows.Append("</row>");
        }

        byte[] package = new XlsxPackage().WithSheet("Sheet1", rows.ToString()).Build();

        using MemoryStream stream = new(package, writable: false);
        using XlsxCursor cursor = new(stream);

        return new TabularAnalyzer().Analyze(cursor);
    }
}
