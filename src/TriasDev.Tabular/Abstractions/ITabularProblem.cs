namespace TriasDev.Tabular;

/// <summary>
/// What every problem the library reports has in common: what it means, and where it is.
/// </summary>
/// <remarks>
/// Three kinds of problem implement it — a <see cref="RowError"/> met while importing a row, a
/// <see cref="MappingFault"/> in a plan that does not fit its schema, and a
/// <see cref="PrecheckFinding"/> predicted from a profile — and they stay three types, because they
/// are different things and the compiler should keep them apart. A UI that only shows a problem to
/// a person does it once, through this: the code is the meaning, and the words are the UI's own,
/// in whatever language it speaks.
/// </remarks>
public interface ITabularProblem
{
    /// <summary>What went wrong, as a stable code; see the documentation's error-code page.</summary>
    string Code { get; }

    /// <summary>The field it concerns, or null when it concerns none.</summary>
    string? FieldName { get; }

    /// <summary>The source column it concerns, zero-based, or null when it concerns none.</summary>
    int? ColumnIndex { get; }
}
