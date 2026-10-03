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
internal interface ISheetWriter
{
    /// <summary>The most rows a sheet holds, the header included.</summary>
    long MaxRows { get; }

    /// <summary>Whether a file holds more than one sheet.</summary>
    bool AllowsSeveralSheets { get; }

    void BeginSheet(string name, ReadOnlySpan<WriteColumn> columns);

    void BeginRow();

    /// <summary>Writes text already checked by <c>TextRules</c>; returns a code if the format cannot hold it.</summary>
    string? WriteText(string value);

    void WriteBoolean(bool value);

    void WriteEmpty();

    void EndRow();

    /// <summary>Writes whatever ends the file. Called once, after the last row.</summary>
    void Complete();
}
