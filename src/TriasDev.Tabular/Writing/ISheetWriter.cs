namespace TriasDev.Tabular;

/// <summary>
/// One format's encoding of a sheet: the only part of writing that knows a format, as
/// <see cref="ITabularCursor"/> is for reading.
/// </summary>
/// <remarks>
/// <see cref="TabularWriter"/> keeps the order of calls, the row and column count and the checks
/// every format shares; an implementation only encodes. A method that can meet a value its format
/// cannot hold returns the <see cref="ErrorCodes.Write"/> code instead of throwing, so that the
/// writer — which knows the sheet, row and column — raises the exception.
/// </remarks>
internal interface ISheetWriter : IDisposable
{
    /// <summary>The most rows a sheet holds, the header included.</summary>
    long MaxRows { get; }

    /// <summary>Whether a file holds more than one sheet.</summary>
    bool AllowsSeveralSheets { get; }

    /// <summary>Whether the format stores sheet names, which must then meet <see cref="SheetNames"/>' rules.</summary>
    bool NamesSheets { get; }

    void BeginSheet(string name, ReadOnlySpan<WriteColumn> columns);

    void BeginRow();

    /// <summary>
    /// Writes a header cell, checked by <see cref="TextRules"/>: as text, but never altered — the
    /// import compares headers as written — and never judged against a record's width, which the
    /// reader learns from the header. Returns a code if the format cannot hold it.
    /// </summary>
    string? WriteHeader(string value);

    /// <summary>
    /// Writes text already checked by <see cref="TextRules"/> into the zero-based
    /// <paramref name="column"/>; returns a code if the format cannot hold it.
    /// </summary>
    string? WriteText(string value, int column);

    /// <summary>Writes an integer; returns a code if the format cannot hold it exactly.</summary>
    string? WriteLong(long value);

    /// <summary>Writes a decimal; returns a code if the format cannot hold it exactly.</summary>
    string? WriteDecimal(decimal value);

    /// <summary>Writes a double already checked by <see cref="ValueChecks.Double"/>.</summary>
    string? WriteDouble(double value);

    /// <summary>Writes a date already truncated to the millisecond; <paramref name="hasTime"/> says whether it has a time of day.</summary>
    string? WriteDate(DateTime value, bool hasTime);

    void WriteBoolean(bool value);

    void WriteEmpty();

    void EndRow();

    /// <summary>Writes whatever ends the file. Called once, after the last row.</summary>
    void Complete();

    // Dispose (from IDisposable): releases what the writer still holds when the file is abandoned —
    // writes nothing meaningful. A no-op once Complete has run.
}
