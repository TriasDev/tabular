using TriasDev.Tabular.Csv;

namespace TriasDev.Tabular;

/// <summary>
/// Reads a file forward, one row at a time.
/// </summary>
/// <remarks>
/// <para>
/// The narrowest thing both the analyzer and the extractor need, and the only place in the library
/// that knows how a format is encoded. Everything above it is written against this interface, which
/// is what makes the parsing decision in ADR-0001 reversible at the cost of one implementation.
/// </para>
/// <para>
/// <see cref="CurrentRow"/> is a view over a buffer the cursor reuses. It is valid until the next
/// <see cref="ReadRow"/> and must be copied by anything that keeps it. Handing out a fresh array per
/// row would cost an allocation on a path that runs to eighty million cells.
/// </para>
/// <para>
/// Reading is synchronous on purpose. Parsing is processor work over a buffered stream, so an
/// asynchronous row would add a state machine per row and buy nothing; and a row cannot be a
/// <see cref="ReadOnlySpan{T}"/> and be awaited in the same expression. The asynchrony that matters
/// to a caller belongs at the extraction boundary, not here.
/// </para>
/// </remarks>
public interface ITabularCursor : IDisposable
{
    /// <summary>The format this cursor reads.</summary>
    TabularFormat Format { get; }

    /// <summary>Every sheet in the file, in file order.</summary>
    IReadOnlyList<SheetInfo> Sheets { get; }

    /// <summary>The sheet being read.</summary>
    int CurrentSheetIndex { get; }

    /// <summary>
    /// Moves to a sheet and positions before its first row. Returns false when no such sheet exists.
    /// </summary>
    bool MoveToSheet(int index);

    /// <summary>Advances to the next row. Returns false at the end of the sheet.</summary>
    /// <param name="cancellationToken">
    /// Stops a read in progress. Optional so that existing callers keep compiling, and honoured
    /// rather than accepted: a single call can spend seconds on a hostile file — a shared string
    /// table to load, a row to reassemble — and a timeout at the request level cannot reach a thread
    /// that is inside one.
    /// </param>
    bool ReadRow(CancellationToken cancellationToken = default);

    /// <summary>
    /// The current row. Valid only until the next <see cref="ReadRow"/>.
    /// </summary>
    ReadOnlySpan<RawCell> CurrentRow { get; }

    /// <summary>
    /// The current row's number as a person reading the file in a spreadsheet would count it: the
    /// first row is 1, and the count includes the header.
    /// </summary>
    int CurrentRowNumber { get; }

    /// <summary>What had to be repaired so far, across every sheet read.</summary>
    /// <remarks>
    /// Cumulative for the cursor's life and never reset by <see cref="MoveToSheet"/>: analysis takes a
    /// sheet's share as the difference between two readings, and the file's total as the last one.
    /// A cursor that returned only its current sheet's counts would make that total wrong.
    /// </remarks>
    CursorDiagnostics Diagnostics { get; }

    /// <summary>
    /// Roughly how much of the file has been read, from 0 to 1, or null where it cannot be known.
    /// </summary>
    /// <remarks>
    /// Measured from what the reader has consumed — a csv's stream position against its length, a
    /// workbook's worksheet bytes against their total — so it moves in steps of a read buffer rather
    /// than per row. A cursor that cannot say leaves the default.
    /// </remarks>
    double? ReadFraction => null;

    /// <summary>
    /// How the current sheet's file is encoded and punctuated, or null where it has no dialect — a
    /// workbook sheet.
    /// </summary>
    /// <remarks>
    /// On the interface so that a cursor wrapping another — to log, to count — can pass it on, and
    /// the profile still says how the file was read.
    /// </remarks>
    CsvDialect? Dialect => null;

    /// <summary>The files of an archive that were not read as tables, and why; empty for any other file.</summary>
    IReadOnlyList<SkippedEntry> SkippedEntries => [];
}
