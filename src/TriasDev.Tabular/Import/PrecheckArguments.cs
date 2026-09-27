namespace TriasDev.Tabular;

/// <summary>
/// The names of the values a <see cref="PrecheckFinding"/> carries in its
/// <see cref="PrecheckFinding.Arguments"/>: what a message for its code needs.
/// </summary>
/// <remarks>
/// The finding states data, not a sentence, so that a UI in any language fills its own template for
/// the code. Numbers are written invariantly. Which code carries which names is listed on the
/// documentation's error-code page, and a test keeps that list and these constants in step.
/// </remarks>
public static class PrecheckArguments
{
    /// <summary>The sheet index the plan names.</summary>
    public const string SheetIndex = "sheetIndex";

    /// <summary>The culture the plan reads under, <c>""</c> for the invariant one.</summary>
    public const string Culture = "culture";

    /// <summary>The header row the profile was measured against, zero-based: <c>2</c> is the row a person calls row 3.</summary>
    public const string ProfileHeaderRowIndex = "profileHeaderRowIndex";

    /// <summary>The header row the plan names, zero-based, as <see cref="MappingPlan.HeaderRowIndex"/> is.</summary>
    public const string PlanHeaderRowIndex = "planHeaderRowIndex";

    /// <summary>How many columns are bound to a group of fields.</summary>
    public const string BoundColumnCount = "boundColumnCount";

    /// <summary>Which of a code's several causes this is; one of <see cref="PrecheckReasons"/>.</summary>
    public const string Reason = "reason";

    /// <summary>How many distinct values the column holds.</summary>
    public const string DistinctCount = "distinctCount";

    /// <summary>How many values fail: distinct values for a rule, values for a type.</summary>
    public const string FailingCount = "failingCount";

    /// <summary>
    /// How many values were judged, <see cref="FailingCount"/> of them failing: for a rule, the column's
    /// distinct values that read as the field's type; for a type, every value the column holds.
    /// </summary>
    public const string JudgedCount = "judgedCount";

    /// <summary>The header the plan recorded for the column.</summary>
    public const string ExpectedHeader = "expectedHeader";

    /// <summary>The header the column carries now.</summary>
    public const string ActualHeader = "actualHeader";

    /// <summary>The field's type, in lower case: <c>integer</c>, <c>decimal</c>, <c>date</c>, <c>boolean</c>.</summary>
    public const string Type = "type";
}

/// <summary>The causes a <see cref="PrecheckArguments.Reason"/> names, where one code has several.</summary>
public static class PrecheckReasons
{
    /// <summary>Every value is one of the binding's spellings of nothing.</summary>
    public const string EveryValueIsNothing = "every-value-is-nothing";

    /// <summary>Rows leave the column empty.</summary>
    public const string EmptyCells = "empty-cells";

    /// <summary>Some rows spell the value as nothing, so they carry none.</summary>
    public const string SpelledAsNothing = "spelled-as-nothing";

    /// <summary>Rows repeat a value already used.</summary>
    public const string Repeats = "repeats";

    /// <summary>The column holds no values at all.</summary>
    public const string NoValues = "no-values";

    /// <summary>The column holds more distinct values than the profile keeps, so it was not judged.</summary>
    public const string TooManyDistinct = "too-many-distinct";

    /// <summary>Some values fail; rows with others can still be imported.</summary>
    public const string ValuesFail = "values-fail";

    /// <summary>Every value fails, and no row can satisfy the rule.</summary>
    public const string NoRowCanSatisfy = "no-row-can-satisfy";

    /// <summary>The file was not profiled under the plan's culture, so the type was not judged.</summary>
    public const string NotProfiled = "not-profiled";
}
