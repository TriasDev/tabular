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
/// which is also how most hostile files end.</item>
/// <item><see cref="TabularStructureException"/> — a readable file that is not the one the plan was
/// built for (409/422: map it again).</item>
/// <item><see cref="MappingPlanException"/> — a plan that does not fit its schema (a defect in the
/// mapping, before any file is read).</item>
/// </list>
/// <para>
/// Mistakes in the calling code — a null argument, a negative option, a field the schema does not
/// declare — remain <see cref="ArgumentException"/> and <see cref="InvalidOperationException"/>.
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

    /// <summary>What went wrong, as a stable code a caller can translate; see the guide's catalog.</summary>
    public string Code { get; }
}

/// <summary>The file is not in a format this library reads, or cannot be read as the one it claims.</summary>
[SuppressMessage("Design", "RCS1194:Implement exception constructors", Justification = "Every instance carries a code a caller translates; a constructor without one would make an exception nobody can act on.")]
public sealed class TabularFormatException : TabularException
{
    /// <summary>A format this library does not read: .xls, .xlsb, .ods, a binary file, a zip that is no workbook.</summary>
    public const string Unsupported = "format.unsupported";

    /// <summary>A package or part that is damaged: a broken zip, malformed XML, a part that is referenced but missing.</summary>
    public const string Corrupt = "format.corrupt";

    /// <summary>A part that ends before its markup does: a clipped upload, or sizes that lie.</summary>
    public const string Truncated = "format.truncated";

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
/// The bounds are the <c>Max…</c> properties of <c>XlsxCursorOptions</c> and <c>CsvCursorOptions</c>,
/// plus the format's own limits; the guide's Bounds table lists them with their defaults.
/// </remarks>
[SuppressMessage("Design", "RCS1194:Implement exception constructors", Justification = "Every instance carries a code a caller translates; a constructor without one would make an exception nobody can act on.")]
public sealed class TabularLimitException : TabularException
{
    /// <summary>The code every instance carries.</summary>
    public const string Exceeded = "limit.exceeded";

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

    /// <summary>Which bound was exceeded, by the name of the option that sets it.</summary>
    public string Limit { get; }

    /// <summary>The bound's value.</summary>
    public long Maximum { get; }
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
    public const string SheetMissing = "structure.sheet-missing";

    /// <summary>The sheet ends before the row the plan names as the header.</summary>
    public const string HeaderRowMissing = "structure.header-row-missing";

    /// <summary>A mapped column's header is not the one the mapping recorded.</summary>
    public const string HeaderChanged = "structure.header-changed";

    /// <summary>Creates the exception.</summary>
    public TabularStructureException(string code, string message)
        : base(code, message)
    {
    }

    /// <summary>The sheet concerned, where there is one.</summary>
    public int? SheetIndex { get; init; }

    /// <summary>The column concerned, where there is one.</summary>
    public int? SourceColumnIndex { get; init; }

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
    public const string Invalid = "mapping.invalid-plan";

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
