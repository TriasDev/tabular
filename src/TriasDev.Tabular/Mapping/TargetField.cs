using TriasDev.Tabular.Analysis;

namespace TriasDev.Tabular.Mapping;

/// <summary>One field a caller wants filled.</summary>
/// <remarks>
/// A description, not a type. The library never reflects over a caller's classes and never learns
/// what entity a field belongs to, which is what lets it be reused by a solution that shares none of
/// this one's domain.
/// </remarks>
/// <remarks>
/// Open for the typed descriptions in <see cref="Import.ImportField"/> to derive from, so a caller
/// can declare a field once and use that same declaration both to build the schema and to read the
/// value out of a row. Writing the name twice is what makes a rename go wrong quietly.
/// </remarks>
public record TargetField
{
    /// <summary>How the field is addressed in a mapping.</summary>
    public required string Name { get; init; }

    /// <summary>How its values are read.</summary>
    public required ColumnType Type { get; init; }

    /// <summary>Whether a row without it is invalid.</summary>
    public bool Required { get; init; }

    /// <summary>Rules its values must satisfy.</summary>
    public IReadOnlyList<FieldConstraint> Constraints { get; init; } = [];

    /// <summary>
    /// Whether every row must carry a different value.
    /// </summary>
    /// <remarks>
    /// Not a <see cref="FieldConstraint"/>, because it is not a property of a value. Whether a value
    /// repeats can only be known from the whole column, which no row-by-row rule can see, so it is
    /// settled by <see cref="Import.MappingPrecheck"/> against the profile instead — where it costs
    /// nothing, because the distinct values were already counted.
    /// </remarks>
    public bool MustBeUnique { get; init; }

    /// <summary>
    /// The field group this belongs to, or null when it stands alone.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Several columns saying the same thing differently — a title in English beside the same title
    /// in German. Deliberately not a second dimension of the mapping: the members are ordinary
    /// fields with ordinary names (<c>title.en</c>, <c>title.de</c>), each fed by one column, so a
    /// binding, a plan and a row keep the shape they already had. The group is what lets a screen
    /// draw them together and what gives <see cref="Required"/> something wider to mean.
    /// </para>
    /// <para>
    /// On a grouped field <see cref="Required"/> is a rule about the group: at least one member must
    /// carry a value. Requiring a particular one would be wrong — a file translated into French
    /// alone is a complete file.
    /// </para>
    /// </remarks>
    public string? Group { get; init; }

    /// <summary>Which member of the group this is — a language code, where the group is a translation.</summary>
    public string? Variant { get; init; }
}
