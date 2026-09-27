
namespace TriasDev.Tabular;

/// <summary>A field whose values are text.</summary>
public sealed record TextImportField : ImportField
{
    /// <summary>Declares that a row without this field is invalid.</summary>
    public TextImportField Require() => this with { Required = true };

    /// <summary>
    /// Declares that every row must carry a different value.
    /// </summary>
    /// <remarks>
    /// Checked against the profile before the import runs, not row by row: whether a value repeats
    /// is a property of the column and no single row can show it.
    /// </remarks>
    public TextImportField Unique() => this with { MustBeUnique = true };

    /// <summary>The value must have exactly this many characters.</summary>
    public TextImportField ExactLength(int length) => With(new FieldConstraint.ExactLength(length));

    /// <summary>The value must have at least this many characters.</summary>
    public TextImportField MinLength(int length) => With(new FieldConstraint.MinLength(length));

    /// <summary>The value must have at most this many characters.</summary>
    public TextImportField MaxLength(int length) => With(new FieldConstraint.MaxLength(length));

    /// <summary>The value must be one of these.</summary>
    public TextImportField AllowedValues(IEnumerable<string> values, bool ignoreCase = false) =>
        With(new FieldConstraint.AllowedValues(values, ignoreCase));

    /// <summary>The whole value must match this pattern; see <see cref="FieldConstraint.Pattern"/>.</summary>
    public TextImportField Matching(string pattern) => With(new FieldConstraint.Pattern(pattern));

    /// <summary>Adds a rule of the caller's own, reported under its code when a value fails it.</summary>
    /// <param name="code">The caller's error code; see <see cref="FieldConstraint.Rule"/>.</param>
    /// <param name="rule">Whether a present value satisfies the rule, e.g. <see cref="CheckDigits.Luhn"/>.</param>
    public TextImportField Must(string code, Func<string, bool> rule)
    {
        ArgumentNullException.ThrowIfNull(rule);
        return With(new FieldConstraint.Rule(code, v => v.Text is { } text && rule(text)));
    }

    private TextImportField With(FieldConstraint constraint) =>
        this with { Constraints = [.. Constraints, constraint] };
}

/// <summary>A field whose values are whole numbers.</summary>
public sealed record IntegerImportField : ImportField
{
    /// <summary>Declares that a row without this field is invalid.</summary>
    public IntegerImportField Require() => this with { Required = true };

    /// <summary>
    /// Declares that every row must carry a different value.
    /// </summary>
    /// <remarks>
    /// Checked against the profile before the import runs, not row by row: whether a value repeats
    /// is a property of the column and no single row can show it.
    /// </remarks>
    public IntegerImportField Unique() => this with { MustBeUnique = true };

    /// <summary>The value must not be below this one.</summary>
    public IntegerImportField AtLeast(long value) => With(new FieldConstraint.MinValue(value));

    /// <summary>The value must not be above this one.</summary>
    public IntegerImportField AtMost(long value) => With(new FieldConstraint.MaxValue(value));

    /// <summary>Adds a rule of the caller's own, reported under its code when a value fails it.</summary>
    public IntegerImportField Must(string code, Func<long, bool> rule)
    {
        ArgumentNullException.ThrowIfNull(rule);
        return With(new FieldConstraint.Rule(code, v => rule(v.Integer)));
    }

    private IntegerImportField With(FieldConstraint constraint) =>
        this with { Constraints = [.. Constraints, constraint] };
}

/// <summary>A field whose values are numbers with a fractional part.</summary>
public sealed record DecimalImportField : ImportField
{
    /// <summary>Declares that a row without this field is invalid.</summary>
    public DecimalImportField Require() => this with { Required = true };

    /// <summary>
    /// Declares that every row must carry a different value.
    /// </summary>
    /// <remarks>
    /// Checked against the profile before the import runs, not row by row: whether a value repeats
    /// is a property of the column and no single row can show it.
    /// </remarks>
    public DecimalImportField Unique() => this with { MustBeUnique = true };

    /// <summary>The value must not be below this one.</summary>
    public DecimalImportField AtLeast(decimal value) => With(new FieldConstraint.MinValue(value));

    /// <summary>The value must not be above this one.</summary>
    public DecimalImportField AtMost(decimal value) => With(new FieldConstraint.MaxValue(value));

    /// <summary>Adds a rule of the caller's own, reported under its code when a value fails it.</summary>
    public DecimalImportField Must(string code, Func<decimal, bool> rule)
    {
        ArgumentNullException.ThrowIfNull(rule);
        return With(new FieldConstraint.Rule(code, v => rule(v.Number)));
    }

    private DecimalImportField With(FieldConstraint constraint) =>
        this with { Constraints = [.. Constraints, constraint] };
}

/// <summary>A field whose values are dates.</summary>
public sealed record DateImportField : ImportField
{
    /// <summary>Declares that a row without this field is invalid.</summary>
    public DateImportField Require() => this with { Required = true };

    /// <summary>
    /// Declares that every row must carry a different value.
    /// </summary>
    /// <remarks>
    /// Checked against the profile before the import runs, not row by row: whether a value repeats
    /// is a property of the column and no single row can show it.
    /// </remarks>
    public DateImportField Unique() => this with { MustBeUnique = true };

    /// <summary>Adds a rule of the caller's own, reported under its code when a value fails it.</summary>
    public DateImportField Must(string code, Func<DateTime, bool> rule)
    {
        ArgumentNullException.ThrowIfNull(rule);
        return this with { Constraints = [.. Constraints, new FieldConstraint.Rule(code, v => rule(v.Date))] };
    }
}

/// <summary>A field whose values are true or false.</summary>
public sealed record BooleanImportField : ImportField
{
    /// <summary>Declares that a row without this field is invalid.</summary>
    public BooleanImportField Require() => this with { Required = true };
}
