namespace TriasDev.Tabular.Mapping;

/// <summary>Something wrong with a plan, found before the file is opened.</summary>
public sealed record MappingFault
{
    /// <summary>A code from the library's closed catalog.</summary>
    public required string Code { get; init; }

    /// <summary>The field the fault concerns, where it concerns one.</summary>
    public string? TargetFieldName { get; init; }

    /// <summary>The source column the fault concerns, where it concerns one.</summary>
    public int? SourceColumnIndex { get; init; }
}
