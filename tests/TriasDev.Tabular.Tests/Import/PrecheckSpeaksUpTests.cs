using System.Text;

using TriasDev.Tabular.Csv;

using Xunit;

namespace TriasDev.Tabular.Tests.Import;

/// <summary>
/// A precheck that stays silent is read as approval, so silence is the more dangerous failure. Each
/// case here is one the second review round found it saying nothing about while the import failed
/// every row.
/// </summary>
public sealed class PrecheckSpeaksUpTests
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    [Fact]
    public void SpeaksAboutAColumnThatIsNothingButSpellingsOfNothing()
    {
        // Neither check could see it: the required check found no empty cells, because the profiler
        // counted those values as values; the allowed-value check subtracted them and found nothing
        // left to disallow. Between them they held the proof and returned in silence.
        TargetSchema schema = new() { Fields = [ImportField.Text("iso").Require()] };

        MappingPlan plan = Plan(treatAsEmpty: ["k.A."]);

        PrecheckResult result = MappingPrecheck.Check(plan, schema, Profile("iso\nk.A.\nk.A.\nk.A.\n"));

        PrecheckFinding finding = Assert.Single(result.Findings, f => f.Code == "value.required");

        Assert.Equal(PrecheckSeverity.Blocking, finding.Severity);
        Assert.False(result.CanImport);
    }

    [Fact]
    public void SpeaksAboutAPatternNoValueMatches()
    {
        TargetSchema schema = new() { Fields = [ImportField.Text("iso").Matching("^[A-Z]{2}$")] };

        PrecheckResult result = MappingPrecheck.Check(Plan(), schema, Profile("iso\ngermany\nfrance\n"));

        PrecheckFinding finding = Assert.Single(result.Findings);

        Assert.Equal("value.pattern", finding.Code);
        Assert.Equal(PrecheckSeverity.Blocking, finding.Severity);
        Assert.Contains("france", finding.Detail);
    }

    [Fact]
    public void SpeaksAboutARangeNoValueReaches()
    {
        TargetSchema schema = new() { Fields = [ImportField.Decimal("amount").AtLeast(100)] };

        PrecheckResult result = MappingPrecheck.Check(
            Plan(field: "amount"),
            schema,
            Profile("amount\n1\n2\n3\n"));

        PrecheckFinding finding = Assert.Single(result.Findings, f => f.Code == "value.out-of-range");

        Assert.Equal(PrecheckSeverity.Blocking, finding.Severity);
        Assert.False(result.CanImport);
    }

    [Fact]
    public void SpeaksAboutARangeRuleOnAFieldThatNeverHoldsANumber()
    {
        // Decidable from the schema alone, and the sharpest of the three: MappedValue.Number reads a
        // non-number as zero, so this rule is one no value of this field could ever satisfy. A fault
        // in the schema rather than in the file, and this is the only place anybody would find out.
        //
        // Not expressible through ImportField — TextField has no range method, which is the better
        // defence — but TargetField is public and its constraint list is open, so the guard earns its
        // place for whoever builds a schema without the fluent API.
        TargetSchema schema = new()
        {
            Fields =
            [
                new TargetField
                {
                    Name = "iso",
                    Type = ColumnType.Text,
                    Constraints = [new FieldConstraint.MaxValue(100)],
                },
            ],
        };

        PrecheckResult result = MappingPrecheck.Check(Plan(), schema, Profile("iso\nAcme GmbH\n"));

        PrecheckFinding finding = Assert.Single(result.Findings, f => f.Code == "value.out-of-range");

        Assert.Equal(PrecheckSeverity.Blocking, finding.Severity);
    }

    [Fact]
    public void SpeaksAboutAHeaderThatNoLongerReadsAsItDid()
    {
        // Extraction refuses the whole run over this, and the precheck never mentioned it — the one
        // fault that kills a run outright was the one it could not see.
        TargetSchema schema = new() { Fields = [ImportField.Text("iso")] };

        MappingPlan plan = new()
        {
            Bindings =
            [
                new ColumnBinding { SourceColumnIndex = 0, SourceHeader = "country", TargetFieldName = "iso" },
            ],
        };

        PrecheckResult result = MappingPrecheck.Check(plan, schema, Profile("iso\nDE\n"));

        PrecheckFinding finding = Assert.Single(result.Findings);

        Assert.Equal("mapping.header-changed", finding.Code);
        Assert.Equal(PrecheckSeverity.Blocking, finding.Severity);
    }

    [Fact]
    public void StillJudgesAColumnTheSheetDoesNotHaveWhenTheProfileIsStale()
    {
        // A column absent from a stale profile is absent from a fresh one too — moving the header
        // down only removes rows from consideration — so this verdict survives what the rest does
        // not. Returning early on the stale profile threw it away and let the mapping pass.
        TargetSchema schema = new() { Fields = [ImportField.Text("iso").Require()] };

        MappingPlan plan = new()
        {
            HeaderRowIndex = 1,
            Bindings = [new ColumnBinding { SourceColumnIndex = 5, SourceHeader = string.Empty, TargetFieldName = "iso" }],
        };

        PrecheckResult result = MappingPrecheck.Check(plan, schema, Profile("iso\nDE\n"));

        Assert.Contains(result.Findings, f => f.Code == "mapping.stale-profile");
        Assert.Contains(result.Findings, f => f.Code == "mapping.invalid-column" && f.Severity == PrecheckSeverity.Blocking);
        Assert.False(result.CanImport);
    }

    [Theory]
    // Each row is a rule judged against a value the import renders rather than the file's characters,
    // and the expected verdict is simply what the import does. Every one of them disagreed before:
    // three were silent on files that failed entirely, two refused files that imported perfectly.
    [InlineData(ColumnType.Integer, "007\n042", null, "^[0-9]{3}$", false)]
    [InlineData(ColumnType.Date, "01.02.2024\n03.04.2024", "de-DE", @"^\d{4}-\d{2}-\d{2}$", true)]
    public void JudgesAPatternAgainstTheValueTheImportWillProduce(
        ColumnType type,
        string values,
        string? culture,
        string pattern,
        bool importable)
    {
        TargetSchema schema = new()
        {
            Fields = [Raw(type, new FieldConstraint.Pattern(pattern))],
        };

        MappingPlan plan = Plan() with { Culture = culture };

        Assert.Equal(importable, MappingPrecheck.Check(plan, schema, Profile($"iso\n{values}\n")).CanImport);
    }

    [Fact]
    public void JudgesALengthRuleAgainstTheValueTheImportWillProduce()
    {
        // A date written 1/2/2024 is eight characters in the file and ten as yyyy-MM-dd. The precheck
        // used to answer "not determined" here, which was honest but weak; reading the value the way
        // the extractor does makes it answerable.
        TargetSchema schema = new() { Fields = [Raw(ColumnType.Date, new FieldConstraint.MaxLength(9))] };

        MappingPlan plan = Plan() with { Culture = "en-US" };

        PrecheckResult result = MappingPrecheck.Check(plan, schema, Profile("iso\n1/2/2024\n3/4/2024\n"));

        Assert.False(result.CanImport);
        Assert.Equal("value.max-length", Assert.Single(result.Findings).Code);
    }

    [Fact]
    public void JudgesARangeUnderTheCultureTheImportWillRead()
    {
        // It compared the profile's best-reading culture against a plan that said otherwise, so a
        // German file of 1.500 and 2.500 was refused for holding 1.5 and 2.5.
        TargetSchema schema = new() { Fields = [ImportField.Decimal("iso").AtLeast(100)] };

        MappingPlan plan = Plan() with { Culture = "de-DE" };

        Assert.True(MappingPrecheck.Check(plan, schema, Profile("iso\n1.500\n2.500\n")).CanImport);
    }

    [Fact]
    public void DoesNotRefuseAUniqueColumnOverRowsTheImportWillNeverSee()
    {
        // The empty cell sits in a row whose only mapped column is this one — so the import skips the
        // row entirely and never sees the gap. Analysis kept it, because another column carried a
        // value. A warning about rows that may not exist, not a refusal.
        TargetSchema schema = new() { Fields = [ImportField.Text("iso").Unique()] };

        PrecheckResult result = MappingPrecheck.Check(Plan(), schema, Profile("iso;note\nA1;a\n;b\nA2;c\n"));

        PrecheckFinding finding = Assert.Single(result.Findings, f => f.Code == "value.not-unique");

        Assert.Equal(PrecheckSeverity.Warning, finding.Severity);
        Assert.True(result.CanImport);
    }

    [Fact]
    public void StillRefusesAUniqueColumnWhenEveryColumnIsMapped()
    {
        // Nothing is unbound, so the empty row is one the import will read and fail.
        TargetSchema schema = new()
        {
            Fields = [ImportField.Text("iso").Unique(), ImportField.Text("note")],
        };

        MappingPlan plan = new()
        {
            Bindings =
            [
                new ColumnBinding { SourceColumnIndex = 0, SourceHeader = string.Empty, TargetFieldName = "iso" },
                new ColumnBinding { SourceColumnIndex = 1, SourceHeader = string.Empty, TargetFieldName = "note" },
            ],
        };

        PrecheckResult result = MappingPrecheck.Check(plan, schema, Profile("iso;note\nA1;a\n;b\nA2;c\n"));

        Assert.Equal(PrecheckSeverity.Blocking, Assert.Single(result.Findings, f => f.Code == "value.not-unique").Severity);
    }

    [Fact]
    public void DoesNotCallASpellingOfNothingARepeatedValue()
    {
        // It reported "1 rows repeat a value already used" for a column saying k.A. twice. The import
        // reads both as absent and repeats nothing — but it does read them, and finds no value, which
        // is the other way a column fails to identify its rows.
        TargetSchema schema = new() { Fields = [ImportField.Text("iso").Unique()] };

        PrecheckFinding finding = Assert.Single(
            MappingPrecheck.Check(Plan(treatAsEmpty: ["k.A."]), schema, Profile("iso\n1\nk.A.\nk.A.\n2\n")).Findings,
            f => f.Code == "value.not-unique");

        Assert.DoesNotContain("repeat", finding.Detail);
        Assert.Contains("nothing", finding.Detail);
    }

    [Fact]
    public void DoesNotCountAColumnTheSheetDoesNotHaveAsBound()
    {
        // A binding naming a column that does not exist used to make the bound count reach the
        // sheet's width, so the hedge on an uncertain number dropped silently.
        TargetSchema schema = new() { Fields = [ImportField.Text("iso").Require(), ImportField.Text("ghost")] };

        MappingPlan plan = new()
        {
            Bindings =
            [
                new ColumnBinding { SourceColumnIndex = 0, SourceHeader = string.Empty, TargetFieldName = "iso" },
                new ColumnBinding { SourceColumnIndex = 7, SourceHeader = string.Empty, TargetFieldName = "ghost" },
            ],
        };

        PrecheckResult result = MappingPrecheck.Check(plan, schema, Profile("iso;a;b\nX;1;n\n;2;n\nZ;3;n\n"));

        Assert.Contains("Up to", Assert.Single(result.Findings, f => f.Code == "value.required").Detail);
    }

    private static TargetField Raw(ColumnType type, FieldConstraint constraint) =>
        new() { Name = "iso", Type = type, Constraints = [constraint] };

    [Fact]
    public void DoesNotBlockAGroupedFieldForARowAnotherLanguageCovers()
    {
        // Required on a group member means "at least one of the group", so an empty cell in this
        // column is not a failure of its own — the certainty rule was reading it as one.
        TranslatedField title = ImportField.Translated("title", ["en", "de"]).Require();

        TargetSchema schema = new() { Fields = [.. title] };

        MappingPlan plan = new()
        {
            Bindings =
            [
                new ColumnBinding { SourceColumnIndex = 0, SourceHeader = "en", TargetFieldName = "title.en" },
                new ColumnBinding { SourceColumnIndex = 1, SourceHeader = "de", TargetFieldName = "title.de" },
            ],
        };

        PrecheckResult result = MappingPrecheck.Check(plan, schema, Profile("en;de\nlonglong;\n;Deutsch\n"));

        Assert.True(result.CanImport);
    }

    [Fact]
    public void SaysWhichWayAColumnFailedToIdentifyItsRows()
    {
        // "0 rows repeat a value already used" is a sentence that refutes itself. Two different
        // faults were wearing one message.
        TargetSchema schema = new() { Fields = [ImportField.Text("id").Unique(), ImportField.Text("x")] };

        MappingPlan plan = new()
        {
            Bindings =
            [
                new ColumnBinding { SourceColumnIndex = 0, SourceHeader = "id", TargetFieldName = "id" },
                new ColumnBinding { SourceColumnIndex = 1, SourceHeader = "x", TargetFieldName = "x" },
            ],
        };

        PrecheckFinding finding = Assert.Single(
            MappingPrecheck.Check(plan, schema, Profile("id;x\nA1;1\n;2\nA2;3\n")).Findings,
            f => f.Code == "value.not-unique");

        Assert.Contains("leave this column empty", finding.Detail);
        Assert.Equal(1, finding.AffectedRows);
    }

    [Fact]
    public void SaysNothingWasMeasuredRatherThanBlamingTheBudget()
    {
        // The second cause of "not determined" arrived with a fix and inherited the first one's
        // explanation: a column with no values was reported as holding more than the profile tracks.
        TargetSchema schema = new() { Fields = [ImportField.Text("id").Unique()] };

        PrecheckFinding finding = Assert.Single(
            MappingPrecheck.Check(Plan(field: "id"), schema, Profile("id\n")).Findings);

        Assert.Equal(PrecheckSeverity.Undetermined, finding.Severity);
        Assert.Contains("no values at all", finding.Detail);
    }

    private static MappingPlan Plan(IReadOnlyList<string>? treatAsEmpty = null, string field = "iso") =>
        new()
        {
            Bindings =
            [
                new ColumnBinding
                {
                    SourceColumnIndex = 0,
                    SourceHeader = string.Empty,
                    TargetFieldName = field,
                    TreatAsEmpty = treatAsEmpty ?? [],
                },
            ],
        };

    private static FileProfile Profile(string csv)
    {
        using MemoryStream stream = new(Utf8NoBom.GetBytes(csv), writable: false);
        using CsvCursor cursor = new(stream, "test.csv");

        return new TabularAnalyzer().Analyze(cursor);
    }
}
