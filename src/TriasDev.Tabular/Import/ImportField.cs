using TriasDev.Tabular.Analysis;
using TriasDev.Tabular.Mapping;

namespace TriasDev.Tabular.Import;

/// <summary>
/// Declares a field once, so the schema and the code that reads a value share one declaration.
/// </summary>
/// <remarks>
/// <para>
/// The alternative is a field named in the schema and named again, as a string, wherever a value is
/// read. Two spellings of one name is a rename waiting to go wrong: the schema says
/// <c>postalCode</c>, the mapper still says <c>postcode</c>, and the column arrives empty with
/// nothing to indicate why.
/// </para>
/// <para>
/// Declaring it once also lets the value come back typed. A date field yields a
/// <see cref="DateTime"/>, a text field a <see cref="string"/>, and asking a text field for a date
/// does not compile.
/// </para>
/// </remarks>
public static class ImportField
{
    /// <summary>A field holding text.</summary>
    public static TextField Text(string name) =>
        new() { Name = name, Type = ColumnType.Text };

    /// <summary>A field holding whole numbers.</summary>
    public static IntegerField Integer(string name) =>
        new() { Name = name, Type = ColumnType.Integer };

    /// <summary>A field holding numbers with a fractional part.</summary>
    public static DecimalField Decimal(string name) =>
        new() { Name = name, Type = ColumnType.Decimal };

    /// <summary>A field holding dates.</summary>
    public static DateField Date(string name) =>
        new() { Name = name, Type = ColumnType.Date };

    /// <summary>
    /// One field a file may say in several languages, one column per language.
    /// </summary>
    /// <param name="name">The group's name. Members are named <c>name.variant</c>.</param>
    /// <param name="variants">The variants, normally language codes.</param>
    /// <example>
    /// <code>
    /// public static readonly TranslatedField Title = ImportField.Translated("title", ["en", "de"]).Require();
    /// // declares title.en and title.de; a row needs one of them
    /// </code>
    /// </example>
    public static TranslatedField Translated(string name, IEnumerable<string> variants) =>
        new(name, variants);

    /// <summary>A field holding true or false.</summary>
    public static BooleanField Boolean(string name) =>
        new() { Name = name, Type = ColumnType.Boolean };
}

/// <summary>A field whose values are text.</summary>
public sealed record TextField : TargetField
{
    /// <summary>Declares that a row without this field is invalid.</summary>
    public TextField Require() => this with { Required = true };

    /// <summary>
    /// Declares that every row must carry a different value.
    /// </summary>
    /// <remarks>
    /// Checked against the profile before the import runs, not row by row: whether a value repeats
    /// is a property of the column and no single row can show it.
    /// </remarks>
    public TextField Unique() => this with { MustBeUnique = true };

    /// <summary>The value must have exactly this many characters.</summary>
    public TextField ExactLength(int length) => With(new FieldConstraint.ExactLength(length));

    /// <summary>The value must have at least this many characters.</summary>
    public TextField MinLength(int length) => With(new FieldConstraint.MinLength(length));

    /// <summary>The value must have at most this many characters.</summary>
    public TextField MaxLength(int length) => With(new FieldConstraint.MaxLength(length));

    /// <summary>The value must be one of these.</summary>
    public TextField AllowedValues(IEnumerable<string> values, bool ignoreCase = false) =>
        With(new FieldConstraint.AllowedValues(values, ignoreCase));

    /// <summary>The value must match this pattern.</summary>
    public TextField Matching(string pattern) => With(new FieldConstraint.Pattern(pattern));

    private TextField With(FieldConstraint constraint) =>
        this with { Constraints = [.. Constraints, constraint] };
}

/// <summary>A field whose values are whole numbers.</summary>
public sealed record IntegerField : TargetField
{
    /// <summary>Declares that a row without this field is invalid.</summary>
    public IntegerField Require() => this with { Required = true };

    /// <summary>
    /// Declares that every row must carry a different value.
    /// </summary>
    /// <remarks>
    /// Checked against the profile before the import runs, not row by row: whether a value repeats
    /// is a property of the column and no single row can show it.
    /// </remarks>
    public IntegerField Unique() => this with { MustBeUnique = true };

    /// <summary>The value must not be below this one.</summary>
    public IntegerField AtLeast(long value) => With(new FieldConstraint.MinValue(value));

    /// <summary>The value must not be above this one.</summary>
    public IntegerField AtMost(long value) => With(new FieldConstraint.MaxValue(value));

    private IntegerField With(FieldConstraint constraint) =>
        this with { Constraints = [.. Constraints, constraint] };
}

/// <summary>A field whose values are numbers with a fractional part.</summary>
public sealed record DecimalField : TargetField
{
    /// <summary>Declares that a row without this field is invalid.</summary>
    public DecimalField Require() => this with { Required = true };

    /// <summary>
    /// Declares that every row must carry a different value.
    /// </summary>
    /// <remarks>
    /// Checked against the profile before the import runs, not row by row: whether a value repeats
    /// is a property of the column and no single row can show it.
    /// </remarks>
    public DecimalField Unique() => this with { MustBeUnique = true };

    /// <summary>The value must not be below this one.</summary>
    public DecimalField AtLeast(decimal value) => With(new FieldConstraint.MinValue(value));

    /// <summary>The value must not be above this one.</summary>
    public DecimalField AtMost(decimal value) => With(new FieldConstraint.MaxValue(value));

    private DecimalField With(FieldConstraint constraint) =>
        this with { Constraints = [.. Constraints, constraint] };
}

/// <summary>A field whose values are dates.</summary>
public sealed record DateField : TargetField
{
    /// <summary>Declares that a row without this field is invalid.</summary>
    public DateField Require() => this with { Required = true };

    /// <summary>
    /// Declares that every row must carry a different value.
    /// </summary>
    /// <remarks>
    /// Checked against the profile before the import runs, not row by row: whether a value repeats
    /// is a property of the column and no single row can show it.
    /// </remarks>
    public DateField Unique() => this with { MustBeUnique = true };
}

/// <summary>A field whose values are true or false.</summary>
public sealed record BooleanField : TargetField
{
    /// <summary>Declares that a row without this field is invalid.</summary>
    public BooleanField Require() => this with { Required = true };
}
