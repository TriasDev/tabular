using System.Text;

using TriasDev.Tabular.Csv;

using Xunit;

namespace TriasDev.Tabular.Tests.Import;

/// <summary>
/// The precheck answers from the profile, so the profile has to have measured what the import will
/// do. Where it cannot, it says so instead of answering.
/// </summary>
public sealed class PrecheckAgreesWithTheImportTests
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    [Fact]
    public void DoesNotFaultPaddingTheImportWouldHaveTrimmed()
    {
        // It used to: the profile measured " DE " as four characters and the import saw two, so a
        // length rule was judged against values the read would never produce. Trimming is part of
        // reading now, so both halves see one string.
        TargetSchema schema = new() { Fields = [ImportField.Text("iso").ExactLength(2)] };

        PrecheckResult result = MappingPrecheck.Check(Plan(), schema, Profile("iso\n DE \n AT\nCH \n"));

        Assert.Empty(result.Findings);
        Assert.True(result.CanImport);
    }

    [Fact]
    public void DoesNotFaultTheSpellingsOfNothingTheBindingDeclares()
    {
        // "k.A." is not a country that failed to be allowed. Extraction reads it as absent, so
        // judging it against the allowed set faults a file for saying it has nothing to say.
        TargetSchema schema = new() { Fields = [ImportField.Text("iso").AllowedValues(["DE", "AT"])] };

        MappingPlan plan = new()
        {
            Bindings =
            [
                new ColumnBinding
                {
                    SourceColumnIndex = 0,
                    SourceHeader = "iso",
                    TargetFieldName = "iso",
                    TreatAsEmpty = ["k.A."],
                },
            ],
        };

        PrecheckResult result = MappingPrecheck.Check(plan, schema, Profile("iso\nDE\nk.A.\nAT\n"));

        Assert.Empty(result.Findings);
    }

    [Fact]
    public void SaysItsCountIsALowerBoundWhenTheBindingDeclaresSpellingsOfNothing()
    {
        // Those rows were counted as values and will be read as absent, so the true number is this
        // one or higher — and the profile keeps no frequencies to say by how much.
        // Every column bound, so the only uncertainty is the one direction: those rows were counted
        // as values and will be read as absent. (A single-column sheet cannot show this at all — a
        // row empty in its only column is blank, and analysis no longer counts blank rows.)
        TargetSchema schema = new()
        {
            Fields = [ImportField.Text("iso").Require(), ImportField.Text("x")],
        };

        MappingPlan plan = new()
        {
            Bindings =
            [
                new ColumnBinding
                {
                    SourceColumnIndex = 0,
                    SourceHeader = "iso",
                    TargetFieldName = "iso",
                    TreatAsEmpty = ["k.A."],
                },
                new ColumnBinding { SourceColumnIndex = 1, SourceHeader = "x", TargetFieldName = "x" },
            ],
        };

        PrecheckResult result = MappingPrecheck.Check(plan, schema, Profile("iso;x\nDE;1\n;2\nk.A.;3\n"));

        Assert.Contains("At least 1 rows", Assert.Single(result.Findings, f => f.Code == "value.required").Detail);
    }

    [Fact]
    public void OffersNoNumberWhenItIsUncertainInBothDirections()
    {
        // The two uncertainties pull opposite ways. Empty-equivalents were counted as values, so the
        // true number is higher; and the import skips a row whose mapped columns are all empty while
        // analysis keeps any row with a value anywhere, so where the sheet has unbound columns the
        // true number is lower. Neither direction is known, so no number is offered as one.
        TargetSchema schema = new() { Fields = [ImportField.Text("iso").Require()] };

        MappingPlan plan = new()
        {
            Bindings =
            [
                new ColumnBinding
                {
                    SourceColumnIndex = 0,
                    SourceHeader = "iso",
                    TargetFieldName = "iso",
                    TreatAsEmpty = ["k.A."],
                },
            ],
        };

        PrecheckResult result = MappingPrecheck.Check(plan, schema, Profile("iso;x\nDE;1\n;2\nk.A.;3\n"));

        Assert.Contains("Some rows", Assert.Single(result.Findings, f => f.Code == "value.required").Detail);
    }

    [Fact]
    public void SaysTheCountIsAnUpperBoundWhenTheSheetHasColumnsTheMappingIgnores()
    {
        // Analysis kept the middle row because another column carried a value; the import will skip
        // it, because every column it was told about is empty there.
        TargetSchema schema = new() { Fields = [ImportField.Text("iso").Require()] };

        PrecheckResult result = MappingPrecheck.Check(Plan(), schema, Profile("iso;x\nDE;1\n;2\nAT;3\n"));

        Assert.Contains("Up to 1 rows", Assert.Single(result.Findings, f => f.Code == "value.required").Detail);
    }

    [Fact]
    public void RefusesToJudgeAProfileMeasuredAgainstAnotherHeaderRow()
    {
        // The real header and everything above it were measured as data, so every fact describes a
        // different file. Undetermined rather than blocking: the file is very likely fine and it is
        // the profile that is stale.
        TargetSchema schema = new() { Fields = [ImportField.Text("iso").ExactLength(2)] };

        MappingPlan plan = Plan() with { HeaderRowIndex = 2 };

        PrecheckResult result = MappingPrecheck.Check(plan, schema, Profile("Country list\niso\nDE\n"));

        PrecheckFinding finding = Assert.Single(result.Findings);

        Assert.Equal("mapping.stale-profile", finding.Code);
        Assert.Equal(PrecheckSeverity.Undetermined, finding.Severity);
        Assert.True(result.CanImport);
    }

    [Fact]
    public void JudgesAProfileMeasuredAgainstTheHeaderRowTheMappingNames()
    {
        // And once the file is analysed again under the right row, the answer is a real one.
        TargetSchema schema = new() { Fields = [ImportField.Text("iso").ExactLength(2)] };

        MappingPlan plan = Plan() with { HeaderRowIndex = 1 };
        FileProfile profile = Profile("Country list\niso\nDE\nAT\n", new AnalysisOptions { HeaderRowIndex = 1 });

        Assert.Empty(MappingPrecheck.Check(plan, schema, profile).Findings);
    }

    [Fact]
    public void WillNotSayNoRowCanSucceedWhileSomeRowsCarryNothing()
    {
        // The case that made the precheck refuse importable files. Every value in the column is a
        // stranger, but an empty cell in an optional field is not a failure, so the rows that hold
        // nothing import and only the one bad row fails.
        TargetSchema schema = new() { Fields = [ImportField.Text("iso").AllowedValues(["DE", "AT"])] };

        StringBuilder file = new("iso;other\n");

        for (int i = 0; i < 99; i++)
        {
            file.Append(";x\n");
        }

        file.Append("XX;x\n");

        PrecheckResult result = MappingPrecheck.Check(Plan(), schema, Profile(file.ToString()));

        PrecheckFinding finding = Assert.Single(result.Findings, f => f.Code == "value.not-allowed");

        Assert.Equal(PrecheckSeverity.Warning, finding.Severity);
        Assert.DoesNotContain("No row can satisfy", finding.Detail);
        Assert.True(result.CanImport);
    }

    [Fact]
    public void StillBlocksWhenEveryRowReallyDoesCarryABadValue()
    {
        // The certainty is still available where it is real: no empty cells, so every row carries one
        // of these values and none of them is allowed.
        TargetSchema schema = new() { Fields = [ImportField.Text("iso").AllowedValues(["DE", "AT"])] };

        PrecheckResult result = MappingPrecheck.Check(Plan(), schema, Profile("iso\nXX\nZZ\n"));

        PrecheckFinding finding = Assert.Single(result.Findings);

        Assert.Equal(PrecheckSeverity.Blocking, finding.Severity);
        Assert.Contains("No row can satisfy", finding.Detail);
        Assert.False(result.CanImport);
    }

    [Fact]
    public void StillBlocksWhenTheFieldIsRequiredAndNoValueFits()
    {
        // A required field turns an empty cell into a failure of its own, so "no value fits" does
        // settle the row.
        TargetSchema schema = new() { Fields = [ImportField.Text("iso").Require().AllowedValues(["DE"])] };

        PrecheckResult result = MappingPrecheck.Check(Plan(), schema, Profile("iso;x\n;1\nXX;2\n"));

        Assert.False(result.CanImport);
        Assert.Contains(result.Findings, f => f.Code == "value.not-allowed" && f.Severity == PrecheckSeverity.Blocking);
    }

    private static MappingPlan Plan() =>
        new()
        {
            Bindings = [new ColumnBinding { SourceColumnIndex = 0, SourceHeader = "iso", TargetFieldName = "iso" }],
        };

    private static FileProfile Profile(string csv, AnalysisOptions? options = null)
    {
        using MemoryStream stream = new(Utf8NoBom.GetBytes(csv), writable: false);
        using CsvCursor cursor = new(stream, "test.csv");

        return new TabularAnalyzer(options).Analyze(cursor);
    }
}
