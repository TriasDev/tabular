using System.Diagnostics.CodeAnalysis;


namespace TriasDev.Tabular;

/// <summary>One field a caller wants filled, and the factory for typed ones.</summary>
/// <remarks>
/// <para>
/// A description, not a type. The library never reflects over a caller's classes and never learns
/// what entity a field belongs to, which is what lets it be reused by a solution that shares none of
/// this one's domain.
/// </para>
/// <para>
/// Declare a field once, with <see cref="Text"/>, <see cref="Date"/> and the others, so the schema
/// and the code that reads a value share one declaration. The alternative is a field named in the
/// schema and named again, as a string, wherever a value is read — two spellings of one name is a
/// rename waiting to go wrong: the schema says <c>postalCode</c>, the mapper still says
/// <c>postcode</c>, and the column arrives empty with nothing to indicate why. Declared once, the
/// value also comes back typed: a date field yields a <see cref="DateTime"/>, and asking a text
/// field for a date does not compile.
/// </para>
/// <para>
/// Constructible itself for a schema whose field types are known only at run time — read from
/// configuration, say — where <c>new ImportField { Name = …, Type = … }</c> is the declaration.
/// </para>
/// </remarks>
public class ImportField
{
    /// <summary>Declares a field; <see cref="Name"/> and <see cref="Type"/> must be set.</summary>
    public ImportField()
    {
    }

    /// <summary>A copy of another field, which the typed fields' rules start from.</summary>
    /// <remarks>
    /// A class, not a record: a field carries rules that hold delegates and compiled patterns, for
    /// which equality by value means nothing. Two fields are the same field only if they are the same
    /// instance. The rules still never change a field — each returns a copy with one more rule.
    /// </remarks>
    /// <param name="original">The field to copy.</param>
    [SetsRequiredMembers]
    protected ImportField(ImportField original)
    {
        ArgumentNullException.ThrowIfNull(original);

        Name = original.Name;
        Type = original.Type;
        Required = original.Required;
        Constraints = original.Constraints;
        MustBeUnique = original.MustBeUnique;
        Group = original.Group;
        Variant = original.Variant;
    }

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
    /// settled by <see cref="MappingPrecheck"/> against the profile instead — where it costs
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

    // The factory: a typed field of each kind, declared once and read back typed.

    /// <summary>A field holding text.</summary>
    public static TextImportField Text(string name) =>
        new() { Name = Named(name), Type = ColumnType.Text };

    /// <summary>A field holding whole numbers.</summary>
    public static IntegerImportField Integer(string name) =>
        new() { Name = Named(name), Type = ColumnType.Integer };

    /// <summary>A field holding numbers with a fractional part.</summary>
    public static DecimalImportField Decimal(string name) =>
        new() { Name = Named(name), Type = ColumnType.Decimal };

    /// <summary>A field holding dates.</summary>
    public static DateImportField Date(string name) =>
        new() { Name = Named(name), Type = ColumnType.Date };

    /// <summary>
    /// One field a file may say in several languages, one column per language.
    /// </summary>
    /// <param name="name">The group's name. Members are named <c>name.variant</c>.</param>
    /// <param name="variants">The variants, normally language codes.</param>
    /// <example>
    /// <code>
    /// public static readonly TranslatedImportField Title = ImportField.Translated("title", ["en", "de"]).Require();
    /// // declares title.en and title.de; a row needs one of them
    /// </code>
    /// </example>
    public static TranslatedImportField Translated(string name, IEnumerable<string> variants) =>
        new(name, variants);

    /// <summary>A field holding true or false.</summary>
    public static BooleanImportField Boolean(string name) =>
        new() { Name = Named(name), Type = ColumnType.Boolean };

    /// <summary>A field's name, refused when it cannot address anything.</summary>
    /// <remarks>
    /// A binding and a row find a field by its name, so an empty one would be accepted here and then
    /// fail somewhere much further from the line that declared it.
    /// </remarks>
    private static string Named(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return name;
    }
}
