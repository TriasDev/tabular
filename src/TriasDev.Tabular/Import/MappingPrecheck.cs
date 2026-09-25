using System.Globalization;

using TriasDev.Tabular.Abstractions;
using TriasDev.Tabular.Analysis;
using TriasDev.Tabular.Mapping;

namespace TriasDev.Tabular.Import;

/// <summary>How much weight a finding carries.</summary>
public enum PrecheckSeverity
{
    /// <summary>Some rows will fail; the rest can still be imported.</summary>
    Warning,

    /// <summary>The file cannot be imported through this mapping at all.</summary>
    Blocking,

    /// <summary>The facts cannot settle it, and only reading the file will.</summary>
    Undetermined,
}

/// <summary>Something the measurements say about a mapping, before the file is read again.</summary>
public sealed record PrecheckFinding
{
    /// <summary>What is wrong, from the same catalog a row error uses where one applies.</summary>
    public required string Code { get; init; }

    /// <summary>How much it matters.</summary>
    public required PrecheckSeverity Severity { get; init; }

    /// <summary>The field it concerns.</summary>
    public required string TargetFieldName { get; init; }

    /// <summary>The column feeding that field.</summary>
    public required int SourceColumnIndex { get; init; }

    /// <summary>How many rows it affects, where the measurements can say.</summary>
    public int? AffectedRows { get; init; }

    /// <summary>What the measurements showed, in words.</summary>
    public required string Detail { get; init; }
}

/// <summary>What a precheck concluded.</summary>
/// <param name="CanImport">Whether the import may proceed at all.</param>
/// <param name="Findings">What the measurements showed, worst first.</param>
public sealed record PrecheckResult(bool CanImport, IReadOnlyList<PrecheckFinding> Findings);

/// <summary>
/// Judges a mapping against what analysis already measured, without reading the file again.
/// </summary>
/// <remarks>
/// <para>
/// The file was profiled once, over every row. Throwing that away and discovering the same facts a
/// row at a time — after eight thousand records have already been written — is the expensive way to
/// learn what was known before the import began.
/// </para>
/// <para>
/// It answers what a row cannot. Whether a column's values are all different is a property of the
/// column, not of any row in it, so no per-row rule can decide it; the profile counted the distinct
/// values exactly and can.
/// </para>
/// <para>
/// What it will not do is promise success. An allowed-value set is measured against a bounded sample,
/// and a rule spanning two fields is invisible to facts about one. So a finding says "these will
/// fail" or "this cannot be judged from here" — never "the rest is fine".
/// </para>
/// </remarks>
public static class MappingPrecheck
{
    /// <summary>Four findings answer the one uniqueness question, each for a different reason.</summary>
    private const string NotUnique = "value.not-unique";

    /// <summary>Checks a mapping against a profile.</summary>
    /// <param name="plan">What a person decided in a mapping screen.</param>
    /// <param name="schema">The fields to fill.</param>
    /// <param name="profile">What analysis measured about the file.</param>
    public static PrecheckResult Check(MappingPlan plan, TargetSchema schema, FileProfile profile)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(profile);

        SheetProfile? sheet = profile.Sheets.FirstOrDefault(s => s.Index == plan.SheetIndex);

        if (sheet is null)
        {
            return new PrecheckResult(
                false,
                [
                    new PrecheckFinding
                    {
                        Code = "mapping.invalid-sheet",
                        Severity = PrecheckSeverity.Blocking,
                        TargetFieldName = string.Empty,
                        SourceColumnIndex = -1,
                        Detail = $"The file has no sheet at index {plan.SheetIndex}.",
                    },
                ]);
        }

        Dictionary<string, TargetField> fields = SchemaFields.ByName(schema);
        List<PrecheckFinding> findings = [];

        // A profile measured against another header row describes another file: the real header and
        // everything above it were counted as data. Nothing measured can be trusted, so nothing
        // measured is reported — and this is undetermined rather than blocking, because the file is
        // very likely fine and it is the profile that is stale. Analysing it again under the chosen
        // row is what settles it.
        bool stale = sheet.HeaderRowIndex != plan.HeaderRowIndex;

        if (stale)
        {
            findings.Add(new PrecheckFinding
            {
                Code = "mapping.stale-profile",
                Severity = PrecheckSeverity.Undetermined,
                TargetFieldName = string.Empty,
                SourceColumnIndex = -1,
                Detail = $"The file was analysed with row {sheet.HeaderRowIndex} as the header and the "
                    + $"mapping names row {plan.HeaderRowIndex}, so nothing measured about the values "
                    + "applies. Analyse it again to have those checked.",
            });
        }

        foreach (ColumnBinding binding in plan.Bindings)
        {
            if (!fields.TryGetValue(binding.TargetFieldName, out TargetField? field))
            {
                continue;                       // the plan validator owns this fault
            }

            ColumnProfile? column = sheet.Columns.FirstOrDefault(c => c.Facts.Index == binding.SourceColumnIndex);

            if (column is null)
            {
                findings.Add(new PrecheckFinding
                {
                    Code = "mapping.invalid-column",
                    Severity = PrecheckSeverity.Blocking,
                    TargetFieldName = field.Name,
                    SourceColumnIndex = binding.SourceColumnIndex,
                    Detail = $"The sheet has no column at index {binding.SourceColumnIndex}.",
                });

                continue;
            }

            // A column absent from a stale profile is absent from a fresh one too — moving the header
            // down can only remove rows from consideration — so this verdict survives what the rest
            // does not, and throwing it away let a genuinely unimportable mapping pass in silence.
            if (stale)
            {
                continue;
            }

            Inspect(field, binding, column.Facts, sheet, plan, findings);
        }

        if (!stale)
        {
            CheckRequiredGroups(plan, schema, sheet, findings);
        }

        bool blocked = findings.Any(f => f.Severity == PrecheckSeverity.Blocking)
            || (schema.Policy == ImportPolicy.AllOrNothing
                && findings.Any(f => f.Severity != PrecheckSeverity.Undetermined));

        return new PrecheckResult(
            !blocked,
            [.. findings.OrderByDescending(f => f.Severity == PrecheckSeverity.Blocking)
                       .ThenByDescending(f => f.AffectedRows ?? 0)]);
    }

    /// <summary>
    /// Judges what a group asks of a row, as far as facts about single columns can reach.
    /// </summary>
    /// <remarks>
    /// They reach one conclusion and not the other. If every column bound to the group is empty from
    /// top to bottom, no row can carry any of them, and that is certain. Whether some particular row
    /// leaves all of them empty is not: two columns can be empty in complementary halves of a file
    /// and cover every row between them. That one is settled per row while importing.
    /// </remarks>
    private static void CheckRequiredGroups(
        MappingPlan plan,
        TargetSchema schema,
        SheetProfile sheet,
        List<PrecheckFinding> findings)
    {
        foreach (IGrouping<string, TargetField> group in schema.Fields
            .Where(f => f.Required && f.Group is not null)
            .GroupBy(f => f.Group!, StringComparer.Ordinal))
        {
            HashSet<string> members = [.. group.Select(f => f.Name)];

            List<ColumnFacts> bound =
            [
                .. plan.Bindings
                    .Where(b => members.Contains(b.TargetFieldName))
                    .Select(b => sheet.Columns.FirstOrDefault(c => c.Facts.Index == b.SourceColumnIndex))
                    .Where(c => c is not null)
                    .Select(c => c!.Facts),
            ];

            if (bound.Count == 0 || bound.Any(f => f.NonEmptyCount > 0))
            {
                continue;
            }

            findings.Add(new PrecheckFinding
            {
                Code = "group.required",
                Severity = PrecheckSeverity.Blocking,
                TargetFieldName = group.Key,
                SourceColumnIndex = bound[0].Index,
                AffectedRows = sheet.RowCount,
                Detail = bound.Count == 1
                    ? "The column mapped to this field is empty from top to bottom, and a row needs one of its languages."
                    : $"All {bound.Count} columns mapped to this field are empty from top to bottom, "
                      + "and a row needs one of its languages.",
            });
        }
    }

    private static void Inspect(
        TargetField field,
        ColumnBinding binding,
        ColumnFacts facts,
        SheetProfile sheet,
        MappingPlan plan,
        List<PrecheckFinding> findings)
    {
        string? culture = plan.Culture;
        void Add(string code, PrecheckSeverity severity, string detail, int? rows = null) =>
            findings.Add(new PrecheckFinding
            {
                Code = code,
                Severity = severity,
                TargetFieldName = field.Name,
                SourceColumnIndex = binding.SourceColumnIndex,
                AffectedRows = rows,
                Detail = detail,
            });

        CheckRequired(field, binding, facts, sheet, plan, Add);
        CheckUnique(field, binding, facts, sheet, plan, Add);
        CheckHeader(binding, facts, Add);
        CheckRules(field, binding, facts, culture, Add);
        CheckType(field, facts, culture, Add);
    }

    /// <summary>Whether a required field's column can supply a value for every row.</summary>
    /// <remarks>
    /// A grouped field is answered for by its group, which is judged across all its columns at
    /// once. Warning per member would say "1256 rows leave this column empty" about a German
    /// column in an English file, which is not a fault at all.
    /// </remarks>
    private static void CheckRequired(
        TargetField field,
        ColumnBinding binding,
        ColumnFacts facts,
        SheetProfile sheet,
        MappingPlan plan,
        Action<string, PrecheckSeverity, string, int?> add)
    {
        if (!field.Required || field.Group is not null)
        {
            return;
        }

        HashSet<string> nothing = new(binding.TreatAsEmpty.Select(v => v.Trim()), StringComparer.OrdinalIgnoreCase);

        // The case that was answered by nobody. A column whose every value is one of the
        // binding's spellings of nothing has no empty cells to count — the profiler counted those
        // values as values — so the check above never ran, while the allowed-value check found
        // nothing left to disallow and returned. Between them they held the proof and said
        // nothing: every row of this column reads as absent.
        bool everyValueIsNothing = facts.NonEmptyCount > 0
            && facts.DistinctValuesAreComplete
            && facts.DistinctValues.Count > 0
            && facts.DistinctValues.All(nothing.Contains);

        if (everyValueIsNothing)
        {
            add(
                "value.required",
                PrecheckSeverity.Blocking,
                "Every value in this column is one of the spellings of nothing the mapping "
                + "declares, so no row carries a value for a field that requires one.",
                sheet.RowCount);
        }
        else if (facts.EmptyCount > 0)
        {
            add(
                "value.required",
                PrecheckSeverity.Warning,
                $"{Describe(facts.EmptyCount, binding, sheet, plan)} leave this column empty and "
                + "the field is required.",
                facts.EmptyCount);
        }
    }

    /// <summary>Whether a field that identifies a record can do so from this column.</summary>
    /// <remarks>
    /// Whether every value differs is a property of the column, so no per-row rule can decide it.
    /// The profile counted them exactly, unless its budget ran out.
    /// </remarks>
    private static void CheckUnique(
        TargetField field,
        ColumnBinding binding,
        ColumnFacts facts,
        SheetProfile sheet,
        MappingPlan plan,
        Action<string, PrecheckSeverity, string, int?> add)
    {
        if (!field.MustBeUnique)
        {
            return;
        }

        // The spellings of nothing are not repeated values: the import reads them as absent, so a
        // column saying "k.A." twice repeats nothing — and equally, it carries rows with no value
        // at all, which is the other way a column fails to identify its rows. Its three siblings
        // learned about these spellings; this one had not, and reported them as repeats.
        //
        // Certain, unlike an empty cell: a row whose mapped column says "k.A." is not blank, so
        // the import does read it and does find nothing there.
        bool readsAsNothing = binding.TreatAsEmpty.Count > 0
            && facts.DistinctValuesAreComplete
            && facts.DistinctValues.Any(v =>
                binding.TreatAsEmpty.Any(e => string.Equals(e.Trim(), v, StringComparison.OrdinalIgnoreCase)));

        if (readsAsNothing)
        {
            add(
                NotUnique,
                PrecheckSeverity.Blocking,
                "Some rows spell this column's value as nothing, so they carry no value at all, and "
                + "a field that identifies a record must do so for every row.",
                null);
        }
        else if (facts.IsUnique == false)
        {
            // Two different faults wore one message. A column can fail to identify its rows by
            // repeating a value, or by leaving one without any value at all, and reporting the
            // second as "0 rows repeat a value already used" is a sentence that refutes itself.
            int repeats = facts.NonEmptyCount - facts.DistinctCount;

            // Failing by an empty cell is not the same as failing by a repeat, and only the
            // repeat is certain. A row that is empty here may be a row the import never sees —
            // it skips a row whose mapped columns are all empty, while analysis keeps any row
            // with a value anywhere — so where the plan leaves columns unbound, this is a warning
            // about rows that may not exist rather than a refusal.
            bool certain = repeats > 0 || AllColumnsBound(sheet, plan);

            add(
                NotUnique,
                certain ? PrecheckSeverity.Blocking : PrecheckSeverity.Warning,
                repeats > 0
                    ? $"{repeats} rows repeat a value already used, and this field identifies a record."
                    : $"{Describe(facts.EmptyCount, binding, sheet, plan)} leave this column empty, and a "
                      + "field that identifies a record must do so for every row.",
                repeats > 0 ? repeats : facts.EmptyCount);
        }
        else if (facts.IsUnique is null && facts.NonEmptyCount == 0 && facts.EmptyCount == 0)
        {
            add(
                NotUnique,
                PrecheckSeverity.Undetermined,
                "The column holds no values at all, so whether it identifies its rows was not "
                + "established.",
                null);
        }
        else if (facts.IsUnique is null)
        {
            add(
                NotUnique,
                PrecheckSeverity.Undetermined,
                "There were more distinct values than the profile tracks, so uniqueness was not "
                + "established.",
                null);
        }
    }

    /// <summary>
    /// Judges every rule the field declares, against the values the import would produce.
    /// </summary>
    /// <remarks>
    /// <para>
    /// One rule for all of them, and the rule is: read each of the column's distinct values the way
    /// the extractor will, then ask the constraint itself. Four separate judges stood here before,
    /// each with its own idea of what it was comparing — and three of them compared the file's
    /// characters while the import compared what it renders from them. A pattern of three digits
    /// passed <c>007</c> and failed every row, because the import sees <c>7</c>.
    /// </para>
    /// <para>
    /// Asking the constraint rather than reimplementing it is the other half. A rule knows whether it
    /// is satisfied; a precheck that re-derives that knowledge acquires its own bugs, which is how a
    /// range rule came to be judged against numbers read under a culture the import does not use.
    /// </para>
    /// <para>
    /// Affordable because it runs once per distinct value, not once per row — and only where the
    /// profile kept every distinct value. Where it did not, nothing is claimed: a column with more
    /// variety than was kept is not one these rules were written for, and the import's own per-row
    /// check stands behind it.
    /// </para>
    /// </remarks>
    private static void CheckRules(
        TargetField field,
        ColumnBinding binding,
        ColumnFacts facts,
        string? culture,
        Action<string, PrecheckSeverity, string, int?> add)
    {
        if (field.Constraints.Count == 0 || facts.NonEmptyCount == 0)
        {
            return;
        }

        if (!facts.DistinctValuesAreComplete)
        {
            ReportRulesUnchecked(field, facts, add);
            return;
        }

        List<(string Source, MappedValue Value, bool Readable)> read = ReadDistinctValues(field, binding, facts, culture);

        if (read.Count == 0)
        {
            return;             // every value reads as absent; the required check owns that
        }

        foreach (FieldConstraint constraint in field.Constraints)
        {
            JudgeConstraint(constraint, read, field, binding, facts, add);
        }
    }

    private static void ReportRulesUnchecked(
        TargetField field,
        ColumnFacts facts,
        Action<string, PrecheckSeverity, string, int?> add)
    {
        foreach (FieldConstraint constraint in field.Constraints)
        {
            add(
                constraint.Code,
                PrecheckSeverity.Undetermined,
                $"The column holds {facts.DistinctCount} distinct values, more than the profile "
                + "keeps, so this rule was not checked here.",
                null);
        }
    }

    /// <summary>
    /// Every distinct value that does not read as absent, read once the way the extractor reads it.
    /// </summary>
    /// <remarks>
    /// Kept rather than re-read per constraint, because every constraint asks about the same
    /// rendering.
    /// </remarks>
    private static List<(string Source, MappedValue Value, bool Readable)> ReadDistinctValues(
        TargetField field,
        ColumnBinding binding,
        ColumnFacts facts,
        string? culture)
    {
        CultureInfo reading = culture is { Length: > 0 } name
            ? CultureInfo.GetCultureInfo(name)
            : CultureInfo.InvariantCulture;

        HashSet<string> nothing = new(binding.TreatAsEmpty.Select(v => v.Trim()), StringComparer.OrdinalIgnoreCase);
        RawCellKind declared = DeclaredKind(facts);

        return
        [
            .. facts.DistinctValues
                .Where(v => !nothing.Contains(v))
                .Select(v =>
                {
                    bool ok = ValueReading.TryRead(Rebuild(v, declared), v, field.Type, reading, out MappedValue value);
                    return (v, value, ok);
                }),
        ];
    }

    private static void JudgeConstraint(
        FieldConstraint constraint,
        List<(string Source, MappedValue Value, bool Readable)> read,
        TargetField field,
        ColumnBinding binding,
        ColumnFacts facts,
        Action<string, PrecheckSeverity, string, int?> add)
    {
        // A value that does not read as the field's type never reaches the constraint: extraction
        // fails it as a type mismatch first, and CheckType reports that.
        List<string> failing =
        [
            .. read.Where(r => r.Readable && !constraint.IsSatisfiedBy(r.Value)).Select(r => r.Source),
        ];

        if (failing.Count == 0)
        {
            return;
        }

        int readable = read.Count(r => r.Readable);
        bool none = failing.Count == readable && CannotBeSatisfiedByAnyRow(field, binding, facts);

        add(
            constraint.Code,
            none ? PrecheckSeverity.Blocking : PrecheckSeverity.Warning,
            $"{failing.Count} of {readable} distinct values fail this rule: "
            + string.Join(", ", failing.Take(5).Order(StringComparer.Ordinal))
            + (failing.Count > 5 ? $" and {failing.Count - 5} more" : string.Empty) + "."
            + (none ? " No row can satisfy it." : string.Empty),
            null);
    }

    /// <summary>
    /// The kind the file itself declared for this column's cells, or text where it declared none.
    /// </summary>
    private static RawCellKind DeclaredKind(ColumnFacts facts)
    {
        RawCellKind best = RawCellKind.Text;
        int most = 0;

        foreach (RawCellKind kind in (RawCellKind[])[RawCellKind.Number, RawCellKind.Date, RawCellKind.Boolean])
        {
            int count = facts.NativeKinds.GetValueOrDefault(kind);

            if (count > most)
            {
                most = count;
                best = kind;
            }
        }

        return best;
    }

    /// <summary>
    /// Puts a distinct value back into the cell it came out of.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The profile keeps a column's distinct values as text, because that is what a distinct value
    /// is. But a workbook types its own cells, and the extractor takes such a cell at its word rather
    /// than parsing anything — so reading the text back under the mapping's culture answers a
    /// question the import never asks.
    /// </para>
    /// <para>
    /// Measured before this: a native <c>1234.5</c> is rendered invariantly, re-read under de-DE
    /// where the point is a group separator, and becomes 12345 — so the precheck refused a German
    /// workbook that imports perfectly, which is the very defect it had just been rewritten to close,
    /// closed for csv and reopened for xlsx.
    /// </para>
    /// <para>
    /// The rendering is <see cref="RawCell.AsText"/>'s, so reversing it is that method read
    /// backwards: invariant round-trip for a number, ISO for a date, <c>true</c>/<c>false</c> for a
    /// boolean. A value that will not reverse is text after all — a mixed column has some.
    /// </para>
    /// </remarks>
    private static RawCell Rebuild(string value, RawCellKind declared)
    {
        switch (declared)
        {
            case RawCellKind.Number
                when double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double number):
                return RawCell.FromNumber(number);

            case RawCellKind.Date
                when DateTime.TryParse(
                    value,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind,
                    out DateTime date):
                return RawCell.FromDate(date);

            case RawCellKind.Boolean when bool.TryParse(value, out bool flag):
                return RawCell.FromBoolean(flag);

            default:
                return RawCell.FromText(value);
        }
    }

    /// <summary>
    /// Says how far a count of empty cells can be trusted, in the direction it is wrong.
    /// </summary>
    /// <remarks>
    /// Two things pull it apart. The binding's spellings of nothing were counted as values, so the
    /// true number is higher. And the import skips a row whose <em>mapped</em> columns are all empty
    /// while analysis keeps any row with a value anywhere, so where the sheet has columns the plan
    /// does not bind, the true number is lower. When both apply, neither direction is known and the
    /// number is not offered as one.
    /// </remarks>
    private static string Describe(int count, ColumnBinding binding, SheetProfile sheet, MappingPlan plan)
    {
        bool understated = binding.TreatAsEmpty.Count > 0;
        bool overstated = !AllColumnsBound(sheet, plan);

        return (understated, overstated) switch
        {
            (true, true) => "Some rows",
            (true, false) => $"At least {count} rows",
            (false, true) => $"Up to {count} rows",
            _ => $"{count} rows",
        };
    }

    /// <summary>
    /// Whether the plan binds every column the sheet has.
    /// </summary>
    /// <remarks>
    /// Counted against the sheet's own columns, not against the bindings' indices: a binding naming a
    /// column that does not exist used to make the count reach the sheet's width while a real column
    /// went unbound, and every hedge that depends on this then dropped silently.
    /// </remarks>
    private static bool AllColumnsBound(SheetProfile sheet, MappingPlan plan)
    {
        HashSet<int> bound = [.. plan.Bindings.Select(b => b.SourceColumnIndex)];

        return sheet.Columns.All(c => bound.Contains(c.Facts.Index));
    }

    /// <summary>
    /// Compares the header a binding recorded against the one the column carries.
    /// </summary>
    /// <remarks>
    /// Extraction refuses the whole run over this — a file swapped between analysis and import would
    /// otherwise be read against the wrong columns and reported as a success — and the precheck was
    /// blind to it, so the one fault that kills a run outright was the one it never mentioned. Only
    /// compared when the binding recorded a header at all: a caller may leave it empty deliberately.
    /// </remarks>
    private static void CheckHeader(
        ColumnBinding binding,
        ColumnFacts facts,
        Action<string, PrecheckSeverity, string, int?> add)
    {
        if (binding.SourceHeader.Length == 0 || string.Equals(binding.SourceHeader, facts.Header, StringComparison.Ordinal))
        {
            return;
        }

        add(
            "mapping.header-changed",
            PrecheckSeverity.Blocking,
            $"This column was mapped as '{binding.SourceHeader}' and now reads '{facts.Header}'. "
            + "The import will refuse the file.",
            null);
    }

    private static bool CannotBeSatisfiedByAnyRow(TargetField field, ColumnBinding binding, ColumnFacts facts) =>
        (field.Required && field.Group is null)
        || (facts.EmptyCount == 0 && binding.TreatAsEmpty.Count == 0);

    private static void CheckType(
        TargetField field,
        ColumnFacts facts,
        string? culture,
        Action<string, PrecheckSeverity, string, int?> add)
    {
        if (field.Type == ColumnType.Text || facts.NonEmptyCount == 0)
        {
            return;                             // anything reads as text
        }

        // A workbook that declares its own types has already answered this.
        int declared = Native(facts, Abstractions.RawCellKind.Number)
            + Native(facts, Abstractions.RawCellKind.Date)
            + Native(facts, Abstractions.RawCellKind.Boolean);

        if (declared == facts.NonEmptyCount)
        {
            return;
        }

        string name = string.IsNullOrEmpty(culture) ? "invariant" : culture;
        CultureParseCounts? counts = facts.ParseCounts.FirstOrDefault(c => c.Culture == name);

        if (counts is null)
        {
            add(
                "value.type-mismatch",
                PrecheckSeverity.Undetermined,
                $"The file was not profiled under {name}, so this could not be judged.",
                null);

            return;
        }

        int readable = field.Type switch
        {
            ColumnType.Integer => counts.Integer,
            ColumnType.Decimal => counts.Integer + counts.Decimal,
            ColumnType.Date => counts.Date,
            ColumnType.Boolean => facts.BooleanCount,
            _ => facts.NonEmptyCount,
        };

        int failing = facts.NonEmptyCount - readable;

        if (failing <= 0)
        {
            return;
        }

        add(
            "value.type-mismatch",
            readable == 0 ? PrecheckSeverity.Blocking : PrecheckSeverity.Warning,
            $"{failing} of {facts.NonEmptyCount} values do not read as "
            + $"{field.Type.ToString().ToLowerInvariant()} under {name}."
            + (readable == 0 ? " None of them do." : string.Empty),
            failing);
    }

    private static int Native(ColumnFacts facts, Abstractions.RawCellKind kind) =>
        facts.NativeKinds.TryGetValue(kind, out int count) ? count : 0;
}
