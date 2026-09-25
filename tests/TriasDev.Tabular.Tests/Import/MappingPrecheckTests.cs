using System.Text;

using TriasDev.Tabular.Csv;

using Xunit;

namespace TriasDev.Tabular.Tests.Import;

/// <summary>
/// Pins what the measurements can settle before a file is read a second time.
/// </summary>
/// <remarks>
/// The file was profiled once, over every row. Discovering the same facts row by row, after some
/// records have already been written, is the expensive way to learn what was known before the import
/// began.
/// </remarks>
public sealed class MappingPrecheckTests
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private static class Fields
    {
        public static readonly TextField Cid = ImportField.Text("cid").Require().Unique();

        public static readonly TextField Country = ImportField.Text("countryCode").Require().ExactLength(2);

        public static readonly DecimalField Amount = ImportField.Decimal("amount");
    }

    private static TargetSchema Schema(ImportPolicy policy = ImportPolicy.BestEffort) =>
        new() { Fields = [Fields.Cid, Fields.Country, Fields.Amount], Policy = policy };

    private static MappingPlan Plan() =>
        new()
        {
            Culture = "de-DE",
            Bindings =
            [
                new ColumnBinding { SourceColumnIndex = 0, SourceHeader = "cid", TargetFieldName = "cid" },
                new ColumnBinding { SourceColumnIndex = 1, SourceHeader = "country", TargetFieldName = "countryCode" },
                new ColumnBinding { SourceColumnIndex = 2, SourceHeader = "amount", TargetFieldName = "amount" },
            ],
        };

    [Fact]
    public void NamesTheValuesThatAreNotInTheReferenceSet()
    {
        // The country column, judged against the caller's list. The library is never told what a
        // country is: the set arrives as a constraint on the field, and the profile supplies the
        // column's distinct values, exactly, because a column of codes holds few of them.
        FileProfile profile = Profile("code\nDE\nAT\nZZ\nDE\nCH\nQQ\n");

        TargetSchema schema = new()
        {
            Fields = [ImportField.Text("country").AllowedValues(["DE", "AT", "CH", "FR"])],
        };

        PrecheckResult result = MappingPrecheck.Check(OneBinding("country"), schema, profile);

        PrecheckFinding finding = Assert.Single(result.Findings);

        Assert.Equal("value.not-allowed", finding.Code);
        Assert.Equal(PrecheckSeverity.Warning, finding.Severity);
        Assert.Contains("QQ, ZZ", finding.Detail);
        Assert.Contains("2 of 5", finding.Detail);
        Assert.True(result.CanImport);
    }

    [Fact]
    public void BlocksWhenNoValueInTheColumnIsAllowed()
    {
        // What binding the wrong column looks like. Every value is a stranger, so no row can
        // succeed — and saying it here costs nothing, whereas discovering it costs a full read.
        FileProfile profile = Profile("code\nAcme GmbH\nTechCorp AG\n");

        TargetSchema schema = new()
        {
            Fields = [ImportField.Text("country").AllowedValues(["DE", "AT"])],
        };

        PrecheckResult result = MappingPrecheck.Check(OneBinding("country"), schema, profile);

        Assert.False(result.CanImport);
        Assert.Equal(PrecheckSeverity.Blocking, Assert.Single(result.Findings).Severity);
    }

    [Fact]
    public void SaysNothingWhenEveryValueIsAllowed()
    {
        FileProfile profile = Profile("code\nDE\nAT\nDE\n");

        TargetSchema schema = new()
        {
            Fields = [ImportField.Text("country").AllowedValues(["DE", "AT", "CH"])],
        };

        Assert.Empty(MappingPrecheck.Check(OneBinding("country"), schema, profile).Findings);
    }

    [Fact]
    public void RefusesToJudgeAColumnWithMoreVarietyThanWasKept()
    {
        // The honest answer, and the reason it is honest: what was kept is a subset, and no
        // conclusion about a column follows from a subset of it. A column this varied is not a
        // column of codes anyway, and the import's per-row check is what stands behind it.
        FileProfile profile = Profile(
            "code\n" + string.Concat(Enumerable.Range(0, 60).Select(i => $"v{i}\n")),
            new AnalysisOptions { RetainedDistinctValues = 10 });

        TargetSchema schema = new()
        {
            Fields = [ImportField.Text("country").AllowedValues(["DE", "AT"])],
        };

        PrecheckResult result = MappingPrecheck.Check(OneBinding("country"), schema, profile);

        PrecheckFinding finding = Assert.Single(result.Findings);

        Assert.Equal(PrecheckSeverity.Undetermined, finding.Severity);
        Assert.Contains("60 distinct values", finding.Detail);

        // Undetermined is not a refusal.
        Assert.True(result.CanImport);
    }

    private static FileProfile Profile(string csv, AnalysisOptions? options = null)
    {
        using MemoryStream stream = new(Utf8NoBom.GetBytes(csv), writable: false);
        using CsvCursor cursor = new(stream, "test.csv");

        return new TabularAnalyzer(options).Analyze(cursor);
    }

    /// <summary>A plan binding one column to one field, for the reference-set cases.</summary>
    private static MappingPlan OneBinding(string field) =>
        new()
        {
            Culture = "de-DE",
            Bindings = [new ColumnBinding { SourceColumnIndex = 0, SourceHeader = "code", TargetFieldName = field }],
        };

    [Fact]
    public void SaysNothingAboutAFileThatFits()
    {
        PrecheckResult result = MappingPrecheck.Check(
            Plan(),
            Schema(),
            Profile("cid;country;amount\nA1;DE;1,50\nA2;AT;2,50\n"));

        Assert.True(result.CanImport);
        Assert.DoesNotContain(result.Findings, f => f.Severity != PrecheckSeverity.Undetermined);
    }

    [Fact]
    public void RefusesAFileWhoseIdentifierRepeats()
    {
        // The question no row can answer about itself. The profile counted the distinct values
        // exactly, so this costs nothing and is known before a single record is written.
        PrecheckResult result = MappingPrecheck.Check(
            Plan(),
            Schema(),
            Profile("cid;country;amount\nA1;DE;1,50\nA2;AT;2,50\nA1;CH;3,50\n"));

        Assert.False(result.CanImport);

        PrecheckFinding finding = result.Findings.First(f => f.Code == "value.not-unique");

        Assert.Equal(PrecheckSeverity.Blocking, finding.Severity);
        Assert.Equal("cid", finding.TargetFieldName);
        Assert.Equal(1, finding.AffectedRows);
    }

    [Fact]
    public void CountsTheRowsThatWillFailARequiredField()
    {
        PrecheckResult result = MappingPrecheck.Check(
            Plan(),
            Schema(),
            Profile("cid;country;amount\nA1;DE;1,50\nA2;;2,50\nA3;;3,50\n"));

        PrecheckFinding finding = result.Findings.First(f => f.Code == "value.required");

        Assert.Equal(PrecheckSeverity.Warning, finding.Severity);
        Assert.Equal(2, finding.AffectedRows);
        Assert.True(result.CanImport);
    }

    [Fact]
    public void CountsTheValuesThatWillNotReadAsTheFieldsType()
    {
        PrecheckResult result = MappingPrecheck.Check(
            Plan(),
            Schema(),
            Profile("cid;country;amount\nA1;DE;1,50\nA2;AT;k.A.\nA3;CH;nonsense\n"));

        PrecheckFinding finding = result.Findings.First(f => f.Code == "value.type-mismatch");

        Assert.Equal(2, finding.AffectedRows);
        Assert.Contains("de-DE", finding.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void RefusesAColumnNoRowCouldSatisfy()
    {
        // Every value is three characters and the field needs two. Not "some rows will fail" — none
        // can succeed, and finding that out row by row would import nothing after reading everything.
        PrecheckResult result = MappingPrecheck.Check(
            Plan(),
            Schema(),
            Profile("cid;country;amount\nA1;DEU;1,50\nA2;AUT;2,50\n"));

        PrecheckFinding finding = result.Findings.First(f => f.Code == "value.exact-length");

        Assert.Equal(PrecheckSeverity.Blocking, finding.Severity);
        Assert.False(result.CanImport);
        Assert.Contains("No row can satisfy it", finding.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void WarnsWhenOnlySomeRowsBreakALengthRule()
    {
        PrecheckResult result = MappingPrecheck.Check(
            Plan(),
            Schema(),
            Profile("cid;country;amount\nA1;DE;1,50\nA2;AUT;2,50\n"));

        PrecheckFinding finding = result.Findings.First(f => f.Code == "value.exact-length");

        Assert.Equal(PrecheckSeverity.Warning, finding.Severity);
        Assert.True(result.CanImport);
    }

    [Fact]
    public void RefusesAnythingLessThanCleanWhenTheTargetSaysAllOrNothing()
    {
        // The programmer's decision, not the uploader's. Two rows out of three would import; under
        // this policy that is not an outcome the target accepts.
        PrecheckResult result = MappingPrecheck.Check(
            Plan(),
            Schema(ImportPolicy.AllOrNothing),
            Profile("cid;country;amount\nA1;DE;1,50\nA2;;2,50\n"));

        Assert.False(result.CanImport);
        Assert.Contains(result.Findings, f => f.Code == "value.required");
    }

    [Fact]
    public void JudgesAllowedValuesNowThatTheProfileKeepsThem()
    {
        // This case used to answer Undetermined, because the profile counted distinct values without
        // keeping them. It keeps them now, so the same question gets a real answer — and a warning
        // rather than a block, because one row is wrong and the rest are not.
        TargetSchema schema = new()
        {
            Fields = [ImportField.Text("status").AllowedValues(["ACTIVE", "INACTIVE"])],
        };

        MappingPlan plan = new()
        {
            Bindings = [new ColumnBinding { SourceColumnIndex = 0, SourceHeader = "status", TargetFieldName = "status" }],
        };

        PrecheckResult result = MappingPrecheck.Check(plan, schema, Profile("status\nACTIVE\nWEIRD\n"));

        PrecheckFinding finding = Assert.Single(result.Findings);

        Assert.Equal(PrecheckSeverity.Warning, finding.Severity);
        Assert.Contains("WEIRD", finding.Detail);
        Assert.True(result.CanImport);
    }

    [Fact]
    public void RefusesASheetTheFileDoesNotHave()
    {
        MappingPlan plan = Plan() with { SheetIndex = 4 };

        PrecheckResult result = MappingPrecheck.Check(plan, Schema(), Profile("cid;country;amount\nA1;DE;1,50\n"));

        Assert.False(result.CanImport);
        Assert.Equal("mapping.invalid-sheet", Assert.Single(result.Findings).Code);
    }
}
