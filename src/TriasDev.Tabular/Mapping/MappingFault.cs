namespace TriasDev.Tabular;

/// <summary>Something wrong with a plan, found before the file is opened.</summary>
public sealed record MappingFault : ITabularProblem
{
    /// <summary>A code from the library's closed catalog.</summary>
    public required string Code { get; init; }

    /// <summary>The field the fault concerns, where it concerns one.</summary>
    public string? FieldName { get; init; }

    /// <summary>The source column the fault concerns, where it concerns one.</summary>
    public int? ColumnIndex { get; init; }
}
