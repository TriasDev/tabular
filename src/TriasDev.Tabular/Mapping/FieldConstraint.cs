using System.Globalization;
using System.Text.RegularExpressions;

namespace TriasDev.Tabular;

/// <summary>
/// A rule a mapped value must satisfy.
/// </summary>
/// <remarks>
/// A closed set. Constraints are data that crosses the boundary from a calling domain, so they have
/// to be expressible without code; and a closed set is what keeps the library from growing a rules
/// language nobody asked for.
/// </remarks>
public abstract record FieldConstraint
{
    /// <summary>
    /// Closes the hierarchy: only the rules nested below can exist.
    /// </summary>
    /// <remarks>
    /// Not an interface, deliberately. An interface would let a caller declare a rule of its own,
    /// and a closed set is the point — the error codes are a fixed catalog a frontend derives its
    /// wording from, and a rule nobody has a code for cannot be reported.
    /// </remarks>
    private protected FieldConstraint()
    {
    }

    /// <summary>The error code reported when a value fails this rule.</summary>
    public abstract string Code { get; }

    /// <summary>Whether a value satisfies this rule.</summary>
    public abstract bool IsSatisfiedBy(in MappedValue value);

    /// <summary>The value must have exactly this many characters.</summary>
    public sealed record ExactLength(int Length) : FieldConstraint
    {
        /// <inheritdoc />
        public override string Code => "value.exact-length";

        /// <inheritdoc />
        public override bool IsSatisfiedBy(in MappedValue value) => value.Text?.Length == Length;
    }

    /// <summary>The value must have at least this many characters.</summary>
    public sealed record MinLength(int Length) : FieldConstraint
    {
        /// <inheritdoc />
        public override string Code => "value.min-length";

        /// <inheritdoc />
        public override bool IsSatisfiedBy(in MappedValue value) => value.Text?.Length >= Length;
    }

    /// <summary>The value must have at most this many characters.</summary>
    public sealed record MaxLength(int Length) : FieldConstraint
    {
        /// <inheritdoc />
        public override string Code => "value.max-length";

        /// <inheritdoc />
        public override bool IsSatisfiedBy(in MappedValue value) => value.Text?.Length <= Length;
    }

    /// <summary>The value must not be below this one.</summary>
    /// <remarks>A value that is not a number does not satisfy a rule about numbers. See
    /// <see cref="IsNumber"/>.</remarks>
    public sealed record MinValue(decimal Value) : FieldConstraint
    {
        /// <inheritdoc />
        public override string Code => "value.out-of-range";

        /// <inheritdoc />
        public override bool IsSatisfiedBy(in MappedValue value) => IsNumber(value) && value.Number >= Value;
    }

    /// <summary>The value must not be above this one.</summary>
    /// <remarks>A value that is not a number does not satisfy a rule about numbers. See
    /// <see cref="IsNumber"/>.</remarks>
    public sealed record MaxValue(decimal Value) : FieldConstraint
    {
        /// <inheritdoc />
        public override string Code => "value.out-of-range";

        /// <inheritdoc />
        public override bool IsSatisfiedBy(in MappedValue value) => IsNumber(value) && value.Number <= Value;
    }

    /// <summary>
    /// Whether the value is a number at all, which a range rule has to ask before comparing.
    /// </summary>
    /// <remarks>
    /// <see cref="MappedValue.Number"/> reads anything that is not a number as zero, so without this
    /// a range rule silently compares against zero: a text value passes <c>MaxValue(100)</c> and any
    /// <c>MinValue</c> at or below zero, and fails a higher one for a reason that has nothing to do
    /// with its magnitude. Either way the verdict is about a comparison that was never meaningful.
    /// </remarks>
    private static bool IsNumber(in MappedValue value) =>
        value.IsPresent && value.Type is ColumnType.Integer or ColumnType.Decimal;

    /// <summary>The value must be one of these.</summary>
    public sealed record AllowedValues : FieldConstraint
    {
        private readonly HashSet<string> _allowed;

        /// <summary>Creates the rule from a set of values.</summary>
        /// <param name="values">What is allowed.</param>
        /// <param name="ignoreCase">Whether case is disregarded when comparing.</param>
        public AllowedValues(IEnumerable<string> values, bool ignoreCase = false)
        {
            ArgumentNullException.ThrowIfNull(values);

            _allowed = new HashSet<string>(
                values,
                ignoreCase ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        }

        /// <inheritdoc />
        public override string Code => "value.not-allowed";

        /// <summary>How many values are allowed.</summary>
        public int Count => _allowed.Count;

        /// <summary>Whether a value is one of them.</summary>
        /// <remarks>
        /// Asked of a column's distinct values rather than of a row's, which is what lets a mapping
        /// be judged against a reference set before the file is read a second time.
        /// </remarks>
        public bool Contains(string value) => _allowed.Contains(value);

        /// <inheritdoc />
        public override bool IsSatisfiedBy(in MappedValue value) => value.Text is not null && _allowed.Contains(value.Text);
    }

    /// <summary>The value must match this pattern.</summary>
    /// <remarks>
    /// Bounded in time, and compiled without backtracking. A pattern arrives as data from a calling
    /// domain and is applied to every row of a file a user chose; a pattern that can be made to run
    /// away would turn one upload into an outage.
    /// </remarks>
    public sealed record Pattern : FieldConstraint
    {
        private static readonly TimeSpan Budget = TimeSpan.FromMilliseconds(100);

        private readonly Regex _expression;

        /// <summary>Creates the rule from a regular expression.</summary>
        public Pattern(string expression)
        {
            ArgumentException.ThrowIfNullOrEmpty(expression);

            Expression = expression;
            _expression = Build(expression);
        }

        /// <summary>The pattern as it was given.</summary>
        public string Expression { get; }

        /// <inheritdoc />
        public override string Code => "value.pattern";

        /// <inheritdoc />
        public override bool IsSatisfiedBy(in MappedValue value)
        {
            if (value.Text is null)
            {
                return false;
            }

            try
            {
                return _expression.IsMatch(value.Text);
            }
            catch (RegexMatchTimeoutException)
            {
                // The value fails, the run continues. One pathological cell must not end an import.
                return false;
            }
        }

        private static Regex Build(string expression)
        {
            try
            {
                // The non-backtracking engine cannot run away by construction, which is worth more
                // than the constructs it refuses to compile.
                return new Regex(expression, RegexOptions.NonBacktracking | RegexOptions.CultureInvariant, Budget);
            }
            catch (NotSupportedException)
            {
                // Lookarounds and backreferences are not available without backtracking. Falling back
                // keeps them usable, and the timeout is then what bounds the run.
                return new Regex(expression, RegexOptions.CultureInvariant, Budget);
            }
        }

        /// <inheritdoc />
        public override string ToString() =>
            string.Create(CultureInfo.InvariantCulture, $"Pattern({Expression})");
    }

    /// <summary>A rule the caller declares, under a code of its own.</summary>
    /// <remarks>
    /// <para>
    /// For what a pattern cannot say — a check digit, a checksum, membership in something computed.
    /// It is applied wherever the built-in rules are: to every row's value at import, and to a
    /// column's distinct values in <see cref="MappingPrecheck"/>. Like them it is only asked
    /// about a value that is present; an empty cell is the required check's business.
    /// </para>
    /// <para>
    /// The code is the caller's to translate, and may not start with a prefix the library's own
    /// catalog uses — <c>value.</c>, <c>mapping.</c>, <c>group.</c>, <c>structure.</c> — so that a
    /// frontend never reads a caller's rule as one of the library's. A predicate that throws is a
    /// defect in the caller and ends the run, rather than passing or failing values silently.
    /// </para>
    /// </remarks>
    public sealed record Rule : FieldConstraint
    {
        private static readonly string[] ReservedPrefixes = ["value.", "mapping.", "group.", "structure."];

        private readonly Func<MappedValue, bool> _predicate;

        /// <summary>Creates a rule from a predicate over the field's value.</summary>
        /// <param name="code">What a failure is reported as; the caller's own, not the library's.</param>
        /// <param name="predicate">Whether a present value satisfies the rule.</param>
        public Rule(string code, Func<MappedValue, bool> predicate)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(code);
            ArgumentNullException.ThrowIfNull(predicate);

            if (ReservedPrefixes.Any(prefix => code.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
            {
                throw new ArgumentException(
                    $"The code '{code}' uses a prefix the library's own error codes use; choose another.",
                    nameof(code));
            }

            RuleCode = code;
            _predicate = predicate;
        }

        /// <summary>The code as the caller gave it.</summary>
        public string RuleCode { get; }

        /// <inheritdoc />
        public override string Code => RuleCode;

        /// <inheritdoc />
        public override bool IsSatisfiedBy(in MappedValue value) => value.IsPresent && _predicate(value);

        /// <inheritdoc />
        public override string ToString() => $"Rule({RuleCode})";
    }
}
