namespace TriasDev.Tabular.Samples.Import;

/// <summary>The caller's own type — the library never sees it, only the mapper that builds it.</summary>
internal sealed record Customer(string Name, string? Country, DateTime? SignedOn, decimal Amount);
