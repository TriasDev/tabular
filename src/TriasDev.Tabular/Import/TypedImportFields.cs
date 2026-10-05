using System.Diagnostics.CodeAnalysis;


namespace TriasDev.Tabular;

/// <summary>A field whose values are text.</summary>
public sealed class TextImportField : ImportField
{
    /// <summary>
    /// Declares the field. Not public: <see cref="ImportField.Text"/> is the only way in, so the field's
    /// <see cref="ImportField.Type"/> always matches its kind.
    /// </summary>
    internal TextImportField()
    {
    }

    [SetsRequiredMembers]
    private TextImportField(TextImportField original)
        : base(original)
    {
    }

    /// <summary>Declares that a row without this field is invalid.</summary>
    public TextImportField Require() => new(this) { Required = true };

    /// <summary>
    /// Declares that every row must carry a different value.
    /// </summary>
    /// <remarks>
    /// Checked against the profile before the import runs, not row by row: whether a value repeats
    /// is a property of the column and no single row can show it.
    /// </remarks>
    public TextImportField Unique() => new(this) { MustBeUnique = true };

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
        new(this) { Constraints = [.. Constraints, constraint] };
}

/// <summary>A field whose values are whole numbers.</summary>
public sealed class IntegerImportField : ImportField
{
    /// <summary>
    /// Declares the field. Not public: <see cref="ImportField.Integer"/> is the only way in, so the field's
    /// <see cref="ImportField.Type"/> always matches its kind.
    /// </summary>
    internal IntegerImportField()
    {
    }

    [SetsRequiredMembers]
    private IntegerImportField(IntegerImportField original)
        : base(original)
    {
    }

    /// <summary>Declares that a row without this field is invalid.</summary>
    public IntegerImportField Require() => new(this) { Required = true };

    /// <summary>
    /// Declares that every row must carry a different value.
    /// </summary>
    /// <remarks>
    /// Checked against the profile before the import runs, not row by row: whether a value repeats
    /// is a property of the column and no single row can show it.
    /// </remarks>
    public IntegerImportField Unique() => new(this) { MustBeUnique = true };

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
        new(this) { Constraints = [.. Constraints, constraint] };
}

/// <summary>A field whose values are numbers with a fractional part.</summary>
public sealed class DecimalImportField : ImportField
{
    /// <summary>
    /// Declares the field. Not public: <see cref="ImportField.Decimal"/> is the only way in, so the field's
    /// <see cref="ImportField.Type"/> always matches its kind.
    /// </summary>
    internal DecimalImportField()
    {
    }

    [SetsRequiredMembers]
    private DecimalImportField(DecimalImportField original)
        : base(original)
    {
    }

    /// <summary>Declares that a row without this field is invalid.</summary>
    public DecimalImportField Require() => new(this) { Required = true };

    /// <summary>
    /// Declares that every row must carry a different value.
    /// </summary>
    /// <remarks>
    /// Checked against the profile before the import runs, not row by row: whether a value repeats
    /// is a property of the column and no single row can show it.
    /// </remarks>
    public DecimalImportField Unique() => new(this) { MustBeUnique = true };

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
        new(this) { Constraints = [.. Constraints, constraint] };
}

/// <summary>A field whose values are dates.</summary>
public sealed class DateImportField : ImportField
{
    /// <summary>
    /// Declares the field. Not public: <see cref="ImportField.Date"/> is the only way in, so the field's
    /// <see cref="ImportField.Type"/> always matches its kind.
    /// </summary>
    internal DateImportField()
    {
    }

    [SetsRequiredMembers]
    private DateImportField(DateImportField original)
        : base(original)
    {
    }

    /// <summary>Declares that a row without this field is invalid.</summary>
    public DateImportField Require() => new(this) { Required = true };

    /// <summary>
    /// Declares that every row must carry a different value.
    /// </summary>
    /// <remarks>
    /// Checked against the profile before the import runs, not row by row: whether a value repeats
    /// is a property of the column and no single row can show it.
    /// </remarks>
    public DateImportField Unique() => new(this) { MustBeUnique = true };

    /// <summary>Adds a rule of the caller's own, reported under its code when a value fails it.</summary>
    public DateImportField Must(string code, Func<DateTime, bool> rule)
    {
        ArgumentNullException.ThrowIfNull(rule);
        return new(this) { Constraints = [.. Constraints, new FieldConstraint.Rule(code, v => rule(v.Date))] };
    }
}

/// <summary>A field whose values are true or false.</summary>
public sealed class BooleanImportField : ImportField
{
    /// <summary>
    /// Declares the field. Not public: <see cref="ImportField.Boolean"/> is the only way in, so the field's
    /// <see cref="ImportField.Type"/> always matches its kind.
    /// </summary>
    internal BooleanImportField()
    {
    }

    [SetsRequiredMembers]
    private BooleanImportField(BooleanImportField original)
        : base(original)
    {
    }

    /// <summary>Declares that a row without this field is invalid.</summary>
    public BooleanImportField Require() => new(this) { Required = true };
}
