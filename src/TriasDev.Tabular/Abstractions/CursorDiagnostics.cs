namespace TriasDev.Tabular;

/// <summary>
/// What the reader had to repair to get through the file.
/// </summary>
/// <remarks>
/// Every count here represents input that no specification allows and that real files contain
/// anyway. The reader recovers rather than refusing — a single malformed address must not fail an
/// import of five million rows — but a recovery nobody is told about is indistinguishable from
/// correct reading, and that is the actual defect. Hence the counts.
/// </remarks>
public sealed class CursorDiagnostics
{
    /// <summary>
    /// Quoted fields that were never closed and had to be abandoned at the configured bound, their
    /// opening quote then read as an ordinary character.
    /// </summary>
    /// <remarks>
    /// Not hypothetical: a 572 MB real-world export carries 302 of them, and left unbounded they
    /// swallow about 39,000 records without a word.
    /// </remarks>
    public int RecoveredUnterminatedQuotes { get; internal set; }

    /// <summary>True when nothing had to be repaired.</summary>
    /// <remarks>
    /// It reports on repairs, not on tidiness. A ragged row is not a repair — the row is reported at
    /// its own width and nothing is lost — so a file full of them is still clean. An earlier version
    /// counted them here and never assigned the counter, which made "clean" mean less than a caller
    /// would have taken it to mean.
    /// </remarks>
    public bool IsClean => RecoveredUnterminatedQuotes == 0;

    /// <summary>A copy that stops counting, for a result that must stay as it was read.</summary>
    internal CursorDiagnostics Snapshot() => new() { RecoveredUnterminatedQuotes = RecoveredUnterminatedQuotes };

    /// <summary>What was repaired since <paramref name="earlier"/> was taken — one sheet's share.</summary>
    internal CursorDiagnostics Since(CursorDiagnostics earlier) => new()
    {
        RecoveredUnterminatedQuotes = RecoveredUnterminatedQuotes - earlier.RecoveredUnterminatedQuotes,
    };
}
