namespace TriasDev.Tabular.Csv;

/// <summary>How a property of a csv dialect came to be what it is.</summary>
/// <remarks>
/// Reported alongside the value because the two carry different weight. A delimiter a caller stated
/// is a fact; one inferred from twenty lines is a good guess, and a user looking at a mapping screen
/// deserves to know which of the two they are being shown.
/// </remarks>
public enum DialectSource
{
    /// <summary>The caller supplied it, and detection was not consulted.</summary>
    Specified,

    /// <summary>A byte order mark declared it.</summary>
    ByteOrderMark,

    /// <summary>Inferred from the content.</summary>
    Detected,

    /// <summary>Nothing decided it, so the fallback applied.</summary>
    Fallback,
}
