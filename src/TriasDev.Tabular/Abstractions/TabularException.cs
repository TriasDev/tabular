using System.Diagnostics.CodeAnalysis;

namespace TriasDev.Tabular;

/// <summary>
/// Everything the library throws about a file or a plan, as opposed to a mistake in the calling code.
/// </summary>
/// <remarks>
/// <para>
/// One base type so a host can write one <c>catch</c>, and a code on every instance so a frontend can
/// translate it — the same contract as a row's error codes. The derived types separate the answers a
/// host gives differently:
/// </para>
/// <list type="bullet">
/// <item><see cref="TabularFormatException"/> — not a file this library reads, or not a readable one
/// (a client error: 400/415).</item>
/// <item><see cref="TabularLimitException"/> — a readable file that exceeds a configured bound (413),
/// which is also how most hostile files end. Only readers throw it.</item>
/// <item><see cref="TabularWriteException"/> — a value the chosen format cannot hold exactly, or a
/// limit of the format the data reached, found while writing (500 for a server's own export: the
/// data or the format choice must change).</item>
/// <item><see cref="TabularStructureException"/> — a readable file that is not the one the plan was
/// built for (409/422: map it again).</item>
/// <item><see cref="MappingPlanException"/> — a plan that does not fit its schema (a defect in the
/// mapping, before any file is read).</item>
/// </list>
/// <para>
/// Mistakes in the calling code — a null argument, a negative option, a field the schema does not
/// declare — remain <see cref="ArgumentException"/> and <see cref="InvalidOperationException"/>; a
/// format code or colour the calling code passes to <see cref="NumberFormat.Parse"/>,
/// <see cref="DateFormat.Parse"/> or <see cref="CellColor.Parse"/> and that cannot be read is a
/// <see cref="FormatException"/>.
/// </para>
/// </remarks>
[SuppressMessage("Design", "RCS1194:Implement exception constructors", Justification = "Every instance carries a code a caller translates; a constructor without one would make an exception nobody can act on.")]
public abstract class TabularException : Exception
{
    /// <summary>Creates the exception.</summary>
    protected TabularException(string code, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        Code = code;
    }

    /// <summary>What went wrong, as a stable code a caller can translate; see the documentation's error-code page.</summary>
    public string Code { get; }
}

/// <summary>The file is not in a format this library reads, or cannot be read as the one it claims.</summary>
[SuppressMessage("Design", "RCS1194:Implement exception constructors", Justification = "Every instance carries a code a caller translates; a constructor without one would make an exception nobody can act on.")]
public sealed class TabularFormatException : TabularException
{
    /// <summary>A format this library does not read: .xls, .xlsb, .fods, another OpenDocument type, a binary file, an archive with nothing readable in it.</summary>
    public const string Unsupported = ErrorCodes.Format.Unsupported;

    /// <summary>A package or part that is damaged: a broken zip, malformed XML, a part that is referenced but missing.</summary>
    public const string Corrupt = ErrorCodes.Format.Corrupt;

    /// <summary>A part that ends before its markup does: a clipped upload, or sizes that lie.</summary>
    public const string Truncated = ErrorCodes.Format.Truncated;

    /// <summary>Creates the exception.</summary>
    /// <param name="code">One of <see cref="Unsupported"/>, <see cref="Corrupt"/>, <see cref="Truncated"/>.</param>
    /// <param name="message">What was found, in English, for logs.</param>
    /// <param name="innerException">The parser's own error, where there is one.</param>
    public TabularFormatException(string code, string message, Exception? innerException = null)
        : base(code, message, innerException)
    {
    }
}

/// <summary>A readable file exceeds one of the bounds its reader was given.</summary>
/// <remarks>
/// <para>
/// The bounds are the <c>Max…</c> properties of <see cref="CsvCursorOptions"/>,
/// <see cref="XlsxCursorOptions"/>, <see cref="OdsCursorOptions"/> and
/// <see cref="ArchiveCursorOptions"/>, plus the format's own limits; the
/// documentation's bounds page lists them with their defaults.
/// </para>
/// <para>
/// Only readers throw it, so a host may answer it as an upload that was too large (413). A limit
/// the writer reaches — rows, merged ranges or styles — is the data's doing and is reported as a
/// located <see cref="TabularWriteException"/> instead.
/// </para>
/// </remarks>
[SuppressMessage("Design", "RCS1194:Implement exception constructors", Justification = "Every instance carries a code a caller translates; a constructor without one would make an exception nobody can act on.")]
public sealed class TabularLimitException : TabularException
{
    /// <summary>The code every instance carries.</summary>
    public const string Exceeded = ErrorCodes.Limit.Exceeded;

    /// <summary>Creates the exception.</summary>
    /// <param name="limit">Which bound, by the name of the option that sets it, e.g. <c>MaxSharedStrings</c>.</param>
    /// <param name="maximum">The bound's value.</param>
    /// <param name="message">What was found, in English, for logs.</param>
    public TabularLimitException(string limit, long maximum, string message)
        : base(Exceeded, message)
    {
        Limit = limit;
        Maximum = maximum;
    }

    /// <summary>
    /// Which bound was exceeded, by the name of the option that sets it — or, for a ceiling the format
    /// fixes, the nearest one (<c>MaxRows</c> for a row number past <see cref="int.MaxValue"/>).
    /// </summary>
    public string Limit { get; }

    /// <summary>The bound's value.</summary>
    public long Maximum { get; }
}

/// <summary>A value cannot be written in the chosen format without changing it, or the sheet outgrew the format.</summary>
/// <remarks>
/// <para>
/// Raised while writing, so part of the file is already out: the writer is faulted, and the caller
/// discards what it wrote — aborts the response, deletes the file. The code says what the format
/// could not hold; the sheet, row and column say where.
/// </para>
/// <para>
/// A format's limits reached by the data are reported here too, never as the reader's
/// <see cref="TabularLimitException"/>: a row past the sheet's limit (<c>write.too-many-rows</c>),
/// a merged range past it (<c>write.too-many-merges</c>), a style rule's style past the file's
/// 4,096 (<c>write.too-many-styles</c>).
/// </para>
/// </remarks>
[SuppressMessage("Design", "RCS1194:Implement exception constructors", Justification = "Every instance carries a code a caller translates; a constructor without one would make an exception nobody can act on.")]
public sealed class TabularWriteException : TabularException
{
    /// <summary>Creates the exception.</summary>
    /// <param name="code">One of the <see cref="ErrorCodes.Write"/> codes.</param>
    /// <param name="sheetName">The sheet being written.</param>
    /// <param name="rowNumber">The row as a spreadsheet counts it: the header is row 1.</param>
    /// <param name="columnIndex">The column, zero-based.</param>
    /// <param name="header">The column's header.</param>
    /// <param name="message">What was found, in English, for logs.</param>
    public TabularWriteException(string code, string sheetName, int rowNumber, int columnIndex, string header, string message)
        : base(code, message)
    {
        SheetName = sheetName;
        RowNumber = rowNumber;
        ColumnIndex = columnIndex;
        Header = header;
    }

    /// <summary>The sheet being written.</summary>
    public string SheetName { get; }

    /// <summary>
    /// The row as a spreadsheet counts it: the header is row 1. For <c>write.too-many-rows</c>, the
    /// last row the sheet holds — the refused row is the one after it.
    /// </summary>
    public int RowNumber { get; }

    /// <summary>The column, zero-based; for <c>write.too-many-rows</c>, which concerns a whole row, the first.</summary>
    public int ColumnIndex { get; }

    /// <summary>The column's header.</summary>
    public string Header { get; }
}

/// <summary>
/// The file cannot be read the way the plan says it should be.
/// </summary>
/// <remarks>
/// A separate channel from a row's errors, deliberately. A bad value in row 812 and "this is not the
/// file you mapped" call for opposite responses — fix a cell, or start over — and a caller that
/// received both through one channel would have to sort them out itself.
/// </remarks>
[SuppressMessage("Design", "RCS1194:Implement exception constructors", Justification = "Every instance carries a code a caller translates; a constructor without one would make an exception nobody can act on.")]
public sealed class TabularStructureException : TabularException
{
    /// <summary>The plan names a sheet the file does not have.</summary>
    public const string SheetMissing = ErrorCodes.Structure.SheetMissing;

    /// <summary>The sheet at the plan's index is not the one the plan recorded.</summary>
    public const string SheetChanged = ErrorCodes.Structure.SheetChanged;

    /// <summary>The sheet ends before the row the plan names as the header.</summary>
    public const string HeaderRowMissing = ErrorCodes.Structure.HeaderRowMissing;

    /// <summary>A mapped column's header is not the one the mapping recorded.</summary>
    public const string HeaderChanged = ErrorCodes.Structure.HeaderChanged;

    /// <summary>Creates the exception.</summary>
    public TabularStructureException(string code, string message)
        : base(code, message)
    {
    }

    /// <summary>The sheet concerned, where there is one.</summary>
    public int? SheetIndex { get; init; }

    /// <summary>The column concerned, where there is one.</summary>
    public int? ColumnIndex { get; init; }

    /// <summary>The header the mapping recorded, for <see cref="HeaderChanged"/>.</summary>
    public string? ExpectedHeader { get; init; }

    /// <summary>The header the column carries now, for <see cref="HeaderChanged"/>.</summary>
    public string? ActualHeader { get; init; }
}

/// <summary>A mapping plan that does not fit its schema, refused before any row is read.</summary>
[SuppressMessage("Design", "RCS1194:Implement exception constructors", Justification = "Every instance carries a code a caller translates; a constructor without one would make an exception nobody can act on.")]
public sealed class MappingPlanException : TabularException
{
    /// <summary>The code every instance carries; the individual faults carry their own.</summary>
    public const string Invalid = ErrorCodes.Mapping.InvalidPlan;

    /// <summary>Creates the exception.</summary>
    public MappingPlanException(IReadOnlyList<MappingFault> faults)
        : base(
            Invalid,
            "The mapping does not fit the schema: "
            + string.Join(", ", (faults ?? throw new ArgumentNullException(nameof(faults))).Select(f => f.Code).Distinct(StringComparer.Ordinal))
            + ".")
    {
        Faults = faults;
    }

    /// <summary>Everything wrong with the plan, as <see cref="MappingPlanValidator"/> reports it.</summary>
    public IReadOnlyList<MappingFault> Faults { get; }
}
