using System.Globalization;

namespace TriasDev.Tabular;

/// <summary>How much weight a finding carries.</summary>
/// <remarks>Declared in rising order, so severities compare and sort as they read.</remarks>
public enum PrecheckSeverity
{
    /// <summary>The facts cannot settle it, and only reading the file will.</summary>
    Undetermined = 0,

    /// <summary>Some rows will fail; the rest can still be imported.</summary>
    Warning = 1,

    /// <summary>The file cannot be imported through this mapping at all.</summary>
    Blocking = 2,
}

/// <summary>Something the measurements say about a mapping, before the file is read again.</summary>
public sealed record PrecheckFinding : ITabularProblem
{
    /// <summary>What is wrong, from the same catalog a row error uses where one applies.</summary>
    public required string Code { get; init; }

    /// <summary>How much it matters.</summary>
    public required PrecheckSeverity Severity { get; init; }

    /// <summary>The field it concerns, or null when it concerns the plan as a whole.</summary>
    public string? FieldName { get; init; }

    /// <summary>The column feeding that field, or null when it concerns no column.</summary>
    public int? ColumnIndex { get; init; }

    /// <summary>How many rows it affects, where the measurements can say.</summary>
    public int? AffectedRows { get; init; }

    /// <summary>
    /// How far <see cref="AffectedRows"/> can be trusted, and in which direction; meaningless when
    /// <see cref="AffectedRows"/> is null.
    /// </summary>
    public RowCountBound AffectedRowsBound { get; init; }

    /// <summary>Up to five of the values that fail, in ordinal order, as the profile kept them.</summary>
    public IReadOnlyList<string> Examples { get; init => field = Equatable.List(value); } = Equatable.Empty<string>();

    /// <summary>
    /// The values a message for this code needs, by the names in <see cref="PrecheckArguments"/>;
    /// numbers written invariantly.
    /// </summary>
    /// <remarks>
    /// Data rather than a sentence: a UI fills its own template for the code, in its own language.
    /// Which names each code carries is listed on the documentation's error-code page.
    /// </remarks>
    public IReadOnlyDictionary<string, string> Arguments { get; init => field = Equatable.Dictionary(value); } = Equatable.Dictionary(NoArguments);

    internal static IReadOnlyDictionary<string, string> NoArguments { get; } = new Dictionary<string, string>(0);
}

/// <summary>How far a count of rows can be trusted.</summary>
/// <remarks>Open to new members, as <see cref="TabularFormat"/> is: a switch over it needs a default arm.</remarks>
public enum RowCountBound
{
    /// <summary>The count is exact.</summary>
    Exact = 0,

    /// <summary>At least this many; the true number may be higher.</summary>
    AtLeast = 1,

    /// <summary>At most this many; the true number may be lower.</summary>
    AtMost = 2,

    /// <summary>Neither direction is known; the count is only indicative.</summary>
    Unknown = 3,
}

/// <summary>What a precheck concluded.</summary>
/// <param name="CanImport">Whether the import may proceed at all.</param>
/// <param name="Findings">What the measurements showed, worst first.</param>
public sealed record PrecheckResult(bool CanImport, IReadOnlyList<PrecheckFinding> Findings)
{
    /// <summary>What the measurements showed, worst first.</summary>
    public IReadOnlyList<PrecheckFinding> Findings { get; init => field = Equatable.List(value); } = Equatable.List(Findings);
}

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
    private const string NotUnique = ErrorCodes.Value.NotUnique;

    /// <summary>Checks a mapping against a profile.</summary>
    /// <param name="plan">What a person decided in a mapping screen.</param>
    /// <param name="schema">The fields to fill.</param>
    /// <param name="profile">What analysis measured about the file.</param>
    public static PrecheckResult Check(MappingPlan plan, ImportSchema schema, FileProfile profile)
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
                        Code = ErrorCodes.Mapping.InvalidSheet,
                        Severity = PrecheckSeverity.Blocking,
                        Arguments = Args((PrecheckArguments.SheetIndex, N(plan.SheetIndex))),
                    },
                ]);
        }

        // Another sheet stands where the plan was built: the import is certain to refuse it, and
        // analysing again would find the same sheet there, so this blocks rather than being left
        // undetermined — and nothing is judged against columns that belong to another sheet.
        if (IsAnotherSheet(plan, sheet))
        {
            return new PrecheckResult(
                false,
                [
                    new PrecheckFinding
                    {
                        Code = ErrorCodes.Structure.SheetChanged,
                        Severity = PrecheckSeverity.Blocking,
                        Arguments = Args((PrecheckArguments.SheetIndex, N(plan.SheetIndex))),
                    },
                ]);
        }

        // A culture the runtime does not have — any named one, under invariant globalization — cannot
        // read a value, so nothing about the values can be judged; the import would refuse the plan
        // on the same grounds.
        if (!CultureCatalog.TryGet(plan.Culture, out _))
        {
            return new PrecheckResult(
                false,
                [
                    new PrecheckFinding
                    {
                        Code = ErrorCodes.Mapping.UnknownCulture,
                        Severity = PrecheckSeverity.Blocking,
                        Arguments = Args((PrecheckArguments.Culture, plan.Culture ?? string.Empty)),
                    },
                ]);
        }

        Dictionary<string, ImportField> fields = SchemaFields.ByName(schema);
        List<PrecheckFinding> findings = [];
        ResolvedAlternatives[] alternatives = SchemaAlternatives.Resolve(schema);

        // Fields of a later group are needed only in rows an earlier group does not locate, which the
        // profile cannot tell apart; so nothing about them blocks here — the review settles it per row.
        HashSet<string> lenient =
        [
            .. alternatives.SelectMany(a => a.Groups.Skip(1)).SelectMany(g => g.Members).Select(p => schema.Fields[p].Name),
        ];

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
                Code = ErrorCodes.Mapping.StaleProfile,
                Severity = PrecheckSeverity.Undetermined,
                Arguments = Args((PrecheckArguments.ProfileHeaderRowIndex, N(sheet.HeaderRowIndex)), (PrecheckArguments.PlanHeaderRowIndex, N(plan.HeaderRowIndex))),
            });
        }

        foreach (ColumnBinding binding in plan.Bindings)
        {
            if (!fields.TryGetValue(binding.FieldName, out ImportField? field))
            {
                continue;                       // the plan validator owns this fault
            }

            ColumnProfile? column = sheet.Columns.FirstOrDefault(c => c.Facts.Index == binding.ColumnIndex);

            if (column is null)
            {
                findings.Add(new PrecheckFinding
                {
                    Code = ErrorCodes.Mapping.InvalidColumn,
                    Severity = PrecheckSeverity.Blocking,
                    FieldName = field.Name,
                    ColumnIndex = binding.ColumnIndex,
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

            Inspect(field, binding, column.Facts, sheet, plan, findings, lenient.Contains(field.Name));
        }

        if (!stale)
        {
            CheckRequiredGroups(plan, schema, sheet, findings);
            CheckAlternatives(plan, schema, alternatives, sheet, findings);
        }

        bool blocked = findings.Any(f => f.Severity == PrecheckSeverity.Blocking)
            || (schema.Policy == ImportPolicy.AllOrNothing
                && findings.Any(f => f.Severity != PrecheckSeverity.Undetermined));

        return new PrecheckResult(
            !blocked,
            [.. findings.OrderByDescending(f => f.Severity)
                       .ThenByDescending(f => f.AffectedRows ?? 0)]);
    }

    /// <summary>
    /// Whether the sheet at the plan's index is not the one the plan recorded, judged as extraction
    /// judges it.
    /// </summary>
    private static bool IsAnotherSheet(MappingPlan plan, SheetProfile sheet) =>
        (plan.SheetName is not null && !string.Equals(plan.SheetName, sheet.Name, StringComparison.Ordinal))
        || (plan.SheetSource is not null && !string.Equals(plan.SheetSource, sheet.Source, StringComparison.Ordinal));

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
        ImportSchema schema,
        SheetProfile sheet,
        List<PrecheckFinding> findings)
    {
        foreach (IGrouping<string, ImportField> group in schema.Fields
            .Where(f => f.Required && f.Group is not null)
            .GroupBy(f => f.Group!, StringComparer.Ordinal))
        {
            HashSet<string> members = [.. group.Select(f => f.Name)];

            List<ColumnFacts> bound =
            [
                .. plan.Bindings
                    .Where(b => members.Contains(b.FieldName))
                    .Select(b => sheet.Columns.FirstOrDefault(c => c.Facts.Index == b.ColumnIndex))
                    .Where(c => c is not null)
                    .Select(c => c!.Facts),
            ];

            if (bound.Count == 0 || bound.Any(f => f.NonEmptyCount > 0))
            {
                continue;
            }

            findings.Add(new PrecheckFinding
            {
                Code = ErrorCodes.Group.Required,
                Severity = PrecheckSeverity.Blocking,
                FieldName = group.Key,
                ColumnIndex = bound[0].Index,
                AffectedRows = sheet.RowCount,
                Arguments = Args((PrecheckArguments.BoundColumnCount, N(bound.Count))),
            });
        }
    }

    /// <summary>What the mapping alone decides about each set of alternatives.</summary>
    /// <remarks>
    /// Both findings are certain, unlike a count of empty cells: a level with no bound field is empty
    /// in every row, whatever the file holds.
    /// </remarks>
    private static void CheckAlternatives(
        MappingPlan plan,
        ImportSchema schema,
        ResolvedAlternatives[] alternatives,
        SheetProfile sheet,
        List<PrecheckFinding> findings)
    {
        HashSet<string> names = [.. plan.Bindings.Select(b => b.FieldName)];
        HashSet<int> bound = [.. Enumerable.Range(0, schema.Fields.Count).Where(i => names.Contains(schema.Fields[i].Name))];

        foreach (ResolvedAlternatives set in alternatives)
        {
            bool anyUsable = false;

            foreach (ResolvedGroup group in set.Groups)
            {
                int reachable = Reachable(group, bound);
                anyUsable |= reachable >= group.RequiredLevels;

                if (reachable < group.Levels.Length)
                {
                    findings.Add(new PrecheckFinding
                    {
                        Code = ErrorCodes.Group.LevelUnmapped,
                        Severity = PrecheckSeverity.Warning,
                        FieldName = group.Name,
                        Arguments = Args(
                            (PrecheckArguments.Alternatives, set.Declared.Name),
                            (PrecheckArguments.Group, group.Name),
                            (PrecheckArguments.Level, group.LevelNames[reachable]),
                            (PrecheckArguments.ReachableLevel, N(reachable))),
                    });
                }
            }

            if (!anyUsable)
            {
                findings.Add(new PrecheckFinding
                {
                    Code = ErrorCodes.Group.Unresolved,
                    Severity = set.Declared.UnresolvedRowFails ? PrecheckSeverity.Blocking : PrecheckSeverity.Warning,
                    FieldName = set.Declared.Name,
                    AffectedRows = sheet.RowCount,
                    Arguments = Args((PrecheckArguments.Alternatives, set.Declared.Name)),
                });
            }
        }
    }

    /// <summary>How many levels of a group the bound fields let a row reach.</summary>
    private static int Reachable(ResolvedGroup group, HashSet<int> bound)
    {
        int reachable = 0;

        while (reachable < group.Levels.Length && group.Levels[reachable].Any(bound.Contains))
        {
            reachable++;
        }

        return reachable;
    }

    private static void Inspect(
        ImportField field,
        ColumnBinding binding,
        ColumnFacts facts,
        SheetProfile sheet,
        MappingPlan plan,
        List<PrecheckFinding> findings,
        bool lenient)
    {
        string? culture = plan.Culture;
        void Add(string code, PrecheckSeverity severity, Evidence evidence) =>
            findings.Add(new PrecheckFinding
            {
                Code = code,
                Severity = lenient && severity == PrecheckSeverity.Blocking ? PrecheckSeverity.Warning : severity,
                FieldName = field.Name,
                ColumnIndex = binding.ColumnIndex,
                AffectedRows = evidence.Rows,
                AffectedRowsBound = evidence.Bound,
                Examples = evidence.Examples ?? [],
                Arguments = evidence.Arguments ?? PrecheckFinding.NoArguments,
            });

        CheckRequired(field, binding, facts, sheet, plan, Add);
        CheckUnique(field, binding, facts, sheet, plan, Add);
        CheckHeader(binding, facts, Add);
        CheckRules(field, binding, facts, culture, Add);
        CheckType(field, binding, facts, culture, Add);
    }

    /// <summary>Whether a required field's column can supply a value for every row.</summary>
    /// <remarks>
    /// A grouped field is answered for by its group, which is judged across all its columns at
    /// once. Warning per member would say "1256 rows leave this column empty" about a German
    /// column in an English file, which is not a fault at all.
    /// </remarks>
    private static void CheckRequired(
        ImportField field,
        ColumnBinding binding,
        ColumnFacts facts,
        SheetProfile sheet,
        MappingPlan plan,
        AddFinding add)
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
                ErrorCodes.Value.Required,
                PrecheckSeverity.Blocking,
                new Evidence(sheet.RowCount, Arguments: Args((PrecheckArguments.Reason, PrecheckReasons.EveryValueIsNothing))));
        }
        else if (facts.EmptyCount > 0)
        {
            add(
                ErrorCodes.Value.Required,
                PrecheckSeverity.Warning,
                new Evidence(facts.EmptyCount, EmptyCountBound(binding, sheet, plan), Arguments: Args((PrecheckArguments.Reason, PrecheckReasons.EmptyCells))));
        }
    }

    /// <summary>Whether a field that identifies a record can do so from this column.</summary>
    /// <remarks>
    /// Whether every value differs is a property of the column, so no per-row rule can decide it.
    /// The profile counted them exactly, unless its budget ran out.
    /// </remarks>
    private static void CheckUnique(
        ImportField field,
        ColumnBinding binding,
        ColumnFacts facts,
        SheetProfile sheet,
        MappingPlan plan,
        AddFinding add)
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
                new Evidence(Arguments: Args((PrecheckArguments.Reason, PrecheckReasons.SpelledAsNothing))));
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
                    ? new Evidence(repeats, RepeatsBound(facts), Arguments: Args((PrecheckArguments.Reason, PrecheckReasons.Repeats)))
                    : new Evidence(facts.EmptyCount, EmptyCountBound(binding, sheet, plan), Arguments: Args((PrecheckArguments.Reason, PrecheckReasons.EmptyCells))));
        }
        else if (facts.IsUnique is null && facts.NonEmptyCount == 0 && facts.EmptyCount == 0)
        {
            add(
                NotUnique,
                PrecheckSeverity.Undetermined,
                new Evidence(Arguments: Args((PrecheckArguments.Reason, PrecheckReasons.NoValues))));
        }
        else if (facts.IsUnique is null)
        {
            add(
                NotUnique,
                PrecheckSeverity.Undetermined,
                new Evidence(Arguments: Args((PrecheckArguments.Reason, PrecheckReasons.TooManyDistinct), (PrecheckArguments.DistinctCount, N(facts.DistinctCount)))));
        }
    }

    /// <summary>
    /// Exact while the distinct count is; an upper bound once the column gave up counting, as its count
    /// is then a lower bound and the repeats derived from it can only be fewer.
    /// </summary>
    private static RowCountBound RepeatsBound(ColumnFacts facts) =>
        facts.DistinctCountIsExact ? RowCountBound.Exact : RowCountBound.AtMost;

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
        ImportField field,
        ColumnBinding binding,
        ColumnFacts facts,
        string? culture,
        AddFinding add)
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
        ImportField field,
        ColumnFacts facts,
        AddFinding add)
    {
        foreach (FieldConstraint constraint in field.Constraints)
        {
            add(
                constraint.Code,
                PrecheckSeverity.Undetermined,
                new Evidence(Arguments: Args((PrecheckArguments.Reason, PrecheckReasons.TooManyDistinct), (PrecheckArguments.DistinctCount, N(facts.DistinctCount)))));
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
        ImportField field,
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
                    bool ok = ValueReading.TryRead(Rebuild(v, declared), v, field.Type, reading, binding.AcceptOtherDecimalSeparator, out MappedValue value, out _);
                    return (v, value, ok);
                }),
        ];
    }

    private static void JudgeConstraint(
        FieldConstraint constraint,
        List<(string Source, MappedValue Value, bool Readable)> read,
        ImportField field,
        ColumnBinding binding,
        ColumnFacts facts,
        AddFinding add)
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

        // The rows, not only the values: each failing value's tally, where the facts hold one (#63).
        Dictionary<string, int> tally = facts.DistinctValueCounts.ToDictionary(v => v.Value, v => v.Count, StringComparer.Ordinal);
        int? rows = tally.Count > 0 ? failing.Sum(v => tally.GetValueOrDefault(v)) : null;

        add(
            constraint.Code,
            none ? PrecheckSeverity.Blocking : PrecheckSeverity.Warning,
            new Evidence(
                rows,
                Examples: [.. failing.Order(StringComparer.Ordinal).Take(5)],
                Arguments: Args(
                    (PrecheckArguments.Reason, none ? PrecheckReasons.NoRowCanSatisfy : PrecheckReasons.ValuesFail),
                    (PrecheckArguments.FailingCount, N(failing.Count)),
                    (PrecheckArguments.JudgedCount, N(readable)))));
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
    /// does not bind, the true number is lower. When both apply, neither direction is known.
    /// </remarks>
    private static RowCountBound EmptyCountBound(ColumnBinding binding, SheetProfile sheet, MappingPlan plan)
    {
        bool understated = binding.TreatAsEmpty.Count > 0;
        bool overstated = !AllColumnsBound(sheet, plan);

        return (understated, overstated) switch
        {
            (true, true) => RowCountBound.Unknown,
            (true, false) => RowCountBound.AtLeast,
            (false, true) => RowCountBound.AtMost,
            _ => RowCountBound.Exact,
        };
    }

    /// <summary>Records a finding about the field under inspection.</summary>
    private delegate void AddFinding(string code, PrecheckSeverity severity, Evidence evidence);

    /// <summary>What a finding carries beyond its code: the rows, the failing values, the named values.</summary>
    private readonly record struct Evidence(
        int? Rows = null,
        RowCountBound Bound = RowCountBound.Exact,
        IReadOnlyList<string>? Examples = null,
        IReadOnlyDictionary<string, string>? Arguments = null);

    private static IReadOnlyDictionary<string, string> Args(params (string Key, string Value)[] values) =>
        values.ToDictionary(v => v.Key, v => v.Value, StringComparer.Ordinal);

    /// <summary>A number as an argument: invariant, so every UI reads it alike.</summary>
    private static string N(int value) => value.ToString(CultureInfo.InvariantCulture);

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
        HashSet<int> bound = [.. plan.Bindings.Select(b => b.ColumnIndex)];

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
        AddFinding add)
    {
        if (binding.Header.Length == 0 || string.Equals(binding.Header, facts.Header, StringComparison.Ordinal))
        {
            return;
        }

        add(
            ErrorCodes.Mapping.HeaderChanged,
            PrecheckSeverity.Blocking,
            new Evidence(Arguments: Args((PrecheckArguments.ExpectedHeader, binding.Header), (PrecheckArguments.ActualHeader, facts.Header))));
    }

    private static bool CannotBeSatisfiedByAnyRow(ImportField field, ColumnBinding binding, ColumnFacts facts) =>
        (field.Required && field.Group is null)
        || (facts.EmptyCount == 0 && binding.TreatAsEmpty.Count == 0);

    private static void CheckType(
        ImportField field,
        ColumnBinding binding,
        ColumnFacts facts,
        string? culture,
        AddFinding add)
    {
        if (field.Type == ColumnType.Text || facts.NonEmptyCount == 0)
        {
            return;                             // anything reads as text
        }

        // A workbook's own types are counted by kind in every culture's figures — a native number as
        // an integer or a decimal, a native date as a date — so they are judged by the same arithmetic
        // as text. Returning early for them, on the view that the file "had already answered", let a
        // column of numbers mapped to a date field pass here and then fail every row of the import.
        int declared = Native(facts, RawCellKind.Number)
            + Native(facts, RawCellKind.Date)
            + Native(facts, RawCellKind.Boolean);

        // One spelling of the invariant culture everywhere: the empty name, as .NET itself names it.
        string name = culture ?? string.Empty;
        string type = field.Type.ToString().ToLowerInvariant();
        CultureParseCounts? counts = facts.ParseCounts.FirstOrDefault(c => c.Culture == name)
            // Native values read the same under every culture, so any culture's figures answer for a
            // column made of nothing else.
            ?? (declared == facts.NonEmptyCount ? facts.ParseCounts.FirstOrDefault() : null);

        if (counts is null)
        {
            add(
                ErrorCodes.Value.TypeMismatch,
                PrecheckSeverity.Undetermined,
                new Evidence(Arguments: Args((PrecheckArguments.Reason, PrecheckReasons.NotProfiled), (PrecheckArguments.Culture, name), (PrecheckArguments.Type, type))));

            return;
        }

        int readable = field.Type switch
        {
            ColumnType.Integer => counts.Integer,
            // As the import will run it: with the values written the other way, when the binding takes them.
            ColumnType.Decimal => counts.Integer + counts.Decimal + (binding.AcceptOtherDecimalSeparator ? counts.OtherSeparatorDecimals : 0),
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
            ErrorCodes.Value.TypeMismatch,
            readable == 0 ? PrecheckSeverity.Blocking : PrecheckSeverity.Warning,
            new Evidence(
                failing,
                Arguments: Args(
                    (PrecheckArguments.Reason, readable == 0 ? PrecheckReasons.NoRowCanSatisfy : PrecheckReasons.ValuesFail),
                    (PrecheckArguments.Culture, name),
                    (PrecheckArguments.Type, type),
                    (PrecheckArguments.FailingCount, N(failing)),
                    (PrecheckArguments.JudgedCount, N(facts.NonEmptyCount)))));
    }

    private static int Native(ColumnFacts facts, RawCellKind kind) =>
        facts.NativeKinds.TryGetValue(kind, out int count) ? count : 0;
}
