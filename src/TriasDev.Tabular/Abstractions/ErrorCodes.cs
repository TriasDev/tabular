namespace TriasDev.Tabular;

/// <summary>
/// Every error code the library reports, as constants — the catalog in the guide, for code.
/// </summary>
/// <remarks>
/// The codes are a translation contract: a frontend maps them to its wording, a calling domain to its
/// own error envelope. The library never emits a code outside this class, and callers' own rules
/// (<c>Must</c>) may not use its prefixes; see <see cref="IsReserved"/>.
/// </remarks>
public static class ErrorCodes
{
    /// <summary>A value in a row that breaks a field's rule — reported per row, and by the precheck.</summary>
    public static class Value
    {
        /// <summary><c>value.exact-length</c></summary>
        public const string ExactLength = "value.exact-length";

        /// <summary><c>value.max-length</c></summary>
        public const string MaxLength = "value.max-length";

        /// <summary><c>value.min-length</c></summary>
        public const string MinLength = "value.min-length";

        /// <summary><c>value.not-allowed</c></summary>
        public const string NotAllowed = "value.not-allowed";

        /// <summary><c>value.not-unique</c></summary>
        public const string NotUnique = "value.not-unique";

        /// <summary><c>value.out-of-range</c></summary>
        public const string OutOfRange = "value.out-of-range";

        /// <summary><c>value.pattern</c></summary>
        public const string Pattern = "value.pattern";

        /// <summary><c>value.required</c></summary>
        public const string Required = "value.required";

        /// <summary><c>value.type-mismatch</c></summary>
        public const string TypeMismatch = "value.type-mismatch";
    }

    /// <summary>A group of fields — the variants of a translated field.</summary>
    public static class Group
    {
        /// <summary><c>group.required</c></summary>
        public const string Required = "group.required";
    }

    /// <summary>A mapping plan that does not fit its schema or its file — from the validator, the precheck and MappingPlanException.</summary>
    public static class Mapping
    {
        /// <summary><c>mapping.constraint-type-mismatch</c></summary>
        public const string ConstraintTypeMismatch = "mapping.constraint-type-mismatch";

        /// <summary><c>mapping.duplicate-binding</c></summary>
        public const string DuplicateBinding = "mapping.duplicate-binding";

        /// <summary><c>mapping.header-changed</c></summary>
        public const string HeaderChanged = "mapping.header-changed";

        /// <summary><c>mapping.invalid-column</c></summary>
        public const string InvalidColumn = "mapping.invalid-column";

        /// <summary><c>mapping.invalid-header-row</c></summary>
        public const string InvalidHeaderRow = "mapping.invalid-header-row";

        /// <summary><c>mapping.invalid-plan</c></summary>
        public const string InvalidPlan = "mapping.invalid-plan";

        /// <summary><c>mapping.invalid-sheet</c></summary>
        public const string InvalidSheet = "mapping.invalid-sheet";

        /// <summary><c>mapping.required-field-unmapped</c></summary>
        public const string RequiredFieldUnmapped = "mapping.required-field-unmapped";

        /// <summary><c>mapping.required-group-unmapped</c></summary>
        public const string RequiredGroupUnmapped = "mapping.required-group-unmapped";

        /// <summary><c>mapping.stale-profile</c></summary>
        public const string StaleProfile = "mapping.stale-profile";

        /// <summary><c>mapping.unknown-culture</c></summary>
        public const string UnknownCulture = "mapping.unknown-culture";

        /// <summary><c>mapping.unknown-field</c></summary>
        public const string UnknownField = "mapping.unknown-field";
    }

    /// <summary>A file that is not the one the plan was built for — TabularStructureException.</summary>
    public static class Structure
    {
        /// <summary><c>structure.header-changed</c></summary>
        public const string HeaderChanged = "structure.header-changed";

        /// <summary><c>structure.header-row-missing</c></summary>
        public const string HeaderRowMissing = "structure.header-row-missing";

        /// <summary><c>structure.sheet-missing</c></summary>
        public const string SheetMissing = "structure.sheet-missing";
    }

    /// <summary>A file this library does not read, or cannot — TabularFormatException.</summary>
    public static class Format
    {
        /// <summary><c>format.corrupt</c></summary>
        public const string Corrupt = "format.corrupt";

        /// <summary><c>format.truncated</c></summary>
        public const string Truncated = "format.truncated";

        /// <summary><c>format.unsupported</c></summary>
        public const string Unsupported = "format.unsupported";
    }

    /// <summary>A bound was exceeded — TabularLimitException.</summary>
    public static class Limit
    {
        /// <summary><c>limit.exceeded</c></summary>
        public const string Exceeded = "limit.exceeded";
    }

    /// <summary>The prefixes of the library's own codes; a caller's rule may not use them.</summary>
    public static IReadOnlyList<string> ReservedPrefixes { get; } = ["value.", "mapping.", "group.", "structure.", "format.", "limit."];

    /// <summary>Whether a code uses one of the library's prefixes.</summary>
    public static bool IsReserved(string code)
    {
        ArgumentNullException.ThrowIfNull(code);
        return ReservedPrefixes.Any(prefix => code.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    }
}
