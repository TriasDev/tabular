using System.Text;

namespace TriasDev.Tabular;

/// <summary>What a user decided about how to read a file.</summary>
public sealed record MappingPlan
{
    /// <summary>Which sheet to read.</summary>
    public int SheetIndex { get; init; }

    /// <summary>
    /// The name of the sheet the plan was built for, checked against the sheet found at
    /// <see cref="SheetIndex"/>; null leaves it unchecked.
    /// </summary>
    /// <remarks>
    /// A checksum, as <see cref="ColumnBinding.SourceHeader"/> is for a column. An index says where,
    /// not what: a workbook whose tabs were reordered, or an archive whose entries were written in
    /// another order, puts a different sheet there, and importing it would load the wrong data under
    /// the right headers.
    /// </remarks>
    public string? SheetName { get; init; }

    /// <summary>
    /// The source of the sheet the plan was built for — its path inside an archive — checked like
    /// <see cref="SheetName"/>; null leaves it unchecked.
    /// </summary>
    /// <remarks>Two workbooks in one archive may both hold a <c>Sheet1</c>; this tells them apart.</remarks>
    public string? SheetSource { get; init; }

    /// <summary>
    /// Which spreadsheet row carries the headers, zero-based: 2 is the row a person calls row 3.
    /// Counted as <see cref="AnalysisOptions.HeaderRowIndex"/> counts it.
    /// </summary>
    /// <remarks>
    /// Analysis always treats the first row as the header, because guessing otherwise produces a
    /// wrong answer nobody checks. This is where a user says the guess was wrong — a file with a
    /// title line above its table being the ordinary case.
    /// </remarks>
    public int HeaderRowIndex { get; init; }

    /// <summary>
    /// The culture numbers and dates are read under, or null for the invariant one.
    /// </summary>
    /// <remarks>
    /// Explicit here, where analysis only ever proposed. A frontend fills in what analysis suggested
    /// and a user may overrule it, which is the whole point of separating the two.
    /// </remarks>
    public string? Culture { get; init; }

    /// <summary>What each mapped column feeds.</summary>
    public required IReadOnlyList<ColumnBinding> Bindings { get; init; }

    /// <summary>
    /// A plan binding every column whose header names a field, ignoring case, spaces and the
    /// separators <c>_ - .</c> — so <c>Postal Code</c>, <c>postal_code</c> and <c>POSTALCODE</c> all
    /// feed <c>postalCode</c>.
    /// </summary>
    /// <param name="sheet">The analysed sheet; its index and header row go into the plan.</param>
    /// <param name="schema">The fields to fill.</param>
    /// <param name="culture">
    /// The culture to read numbers and dates under — typically the one the top hypotheses name. The
    /// empty string or null is the invariant culture.
    /// </param>
    /// <inheritdoc cref="ByHeader(SheetProfile, TargetSchema, Func{string, TargetField, bool}, string?)" path="/remarks"/>
    public static MappingPlan ByHeader(SheetProfile sheet, TargetSchema schema, string? culture = null) =>
        ByHeader(sheet, schema, NamesField, culture);

    /// <summary>
    /// A plan binding every column whose header <paramref name="matches"/> a field.
    /// </summary>
    /// <param name="sheet">The analysed sheet; its index and header row go into the plan.</param>
    /// <param name="schema">The fields to fill.</param>
    /// <param name="matches">Whether a header, as the file writes it, names a field.</param>
    /// <param name="culture">
    /// The culture to read numbers and dates under. The empty string or null is the invariant culture.
    /// </param>
    /// <remarks>
    /// A starting point, not a decision: the plan is an ordinary value to show, adjust and validate.
    /// A field no header names stays unbound, so <see cref="MappingPlanValidator"/> reports it if it
    /// is required. A field named by several columns is bound to the first of them, a column to at most
    /// one field, and a column without a header to none.
    /// </remarks>
    public static MappingPlan ByHeader(
        SheetProfile sheet,
        TargetSchema schema,
        Func<string, TargetField, bool> matches,
        string? culture = null)
    {
        ArgumentNullException.ThrowIfNull(sheet);
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(matches);

        List<ColumnBinding> bindings = [];
        HashSet<string> bound = new(StringComparer.Ordinal);

        foreach (ColumnFacts column in sheet.Columns.Select(c => c.Facts).Where(c => c.Header.Length > 0))
        {
            string header = column.Header;
            TargetField? field = schema.Fields.FirstOrDefault(f => !bound.Contains(f.Name) && matches(header, f));

            if (field is null)
            {
                continue;
            }

            bound.Add(field.Name);
            bindings.Add(new ColumnBinding
            {
                SourceColumnIndex = column.Index,
                SourceHeader = header,
                TargetFieldName = field.Name,
            });
        }

        return new MappingPlan
        {
            SheetIndex = sheet.Index,
            HeaderRowIndex = sheet.HeaderRowIndex,
            Culture = culture,
            SheetName = sheet.Name,
            SheetSource = sheet.Source,
            Bindings = bindings,
        };
    }

    private static bool NamesField(string header, TargetField field) =>
        Normalised(header).Equals(Normalised(field.Name), StringComparison.Ordinal);

    private static string Normalised(string name)
    {
        StringBuilder normalised = new(name.Length);

        foreach (char c in name.Where(c => !char.IsWhiteSpace(c) && c is not ('_' or '-' or '.')))
        {
            normalised.Append(char.ToLowerInvariant(c));
        }

        return normalised.ToString();
    }
}
