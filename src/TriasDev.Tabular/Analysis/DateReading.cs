using System.Globalization;

namespace TriasDev.Tabular;

/// <summary>
/// The one rule for reading a date out of text, shared by the profiler and the extractor.
/// </summary>
/// <remarks>
/// <para>
/// Shared because they disagreed. The profiler applied a shape test and the extractor did not, so a
/// value the profile called text imported as a date — and the precheck blocked files the import
/// accepted. One rule in one place is the only way that stays fixed.
/// </para>
/// <para>
/// <c>DateTime.TryParse</c> fills in whatever the text leaves out, and not always from the same
/// place: a bare time takes today's date, while a day and month take the current year. Measured,
/// <c>3/15</c> under en-US becomes March 2026, and <c>1.5</c> becomes the fifth of January. So
/// neither the parser's success nor the year it returns can be trusted to mean the text said one.
/// </para>
/// <para>
/// Hence the shape test first: a date has at least two separators of its own, or is a time of day
/// with a colon. Then the parse, and then a check that a year survived it. Together those refuse a
/// bare number, a decimal, a time, a day-and-month, and a month-and-year, and accept a value that
/// names all three parts.
/// </para>
/// <para>
/// The cost is real and worth stating: <c>15 Jan 2023</c> has no separators and is refused, as is a
/// genuine <c>0001-01-01</c>. Both are recorded in the known-issues register rather than pretended
/// away.
/// </para>
/// </remarks>
internal static class DateReading
{
    /// <summary>Reads a value that names a date completely, or refuses it.</summary>
    public static bool TryRead(string text, CultureInfo culture, out DateTime date)
    {
        date = default;

        return LooksLikeOne(text) && TryReadShaped(text, culture, out date);
    }

    /// <summary>
    /// <see cref="TryRead"/> for a value already known to pass <see cref="LooksLikeOne"/> — so that a
    /// caller trying several cultures asks that culture-free question once, not once per culture.
    /// </summary>
    public static bool TryReadShaped(string text, CultureInfo culture, out DateTime date) =>
        TryParseAsWritten(text, culture, DateTimeStyles.NoCurrentDateDefault, out date)
        && date.Year != 1;

    /// <summary>
    /// Parses a date and time as the clock time it states, setting aside any zone it carries.
    /// </summary>
    /// <remarks>
    /// Dates in this library are wall-clock values. <c>DateTime.TryParse</c> converts a zoned text —
    /// <c>…Z</c>, <c>+05:00</c> — to the host's local time, so one file imported different dates on
    /// different servers, and a timestamp near midnight moved to another day. The clock time as
    /// written is what a person reading the cell sees, and is the same everywhere.
    /// </remarks>
    public static bool TryParseAsWritten(
        ReadOnlySpan<char> text,
        IFormatProvider culture,
        DateTimeStyles styles,
        out DateTime date)
    {
        if (!HasZone(text))
        {
            return DateTime.TryParse(text, culture, styles, out date);
        }

        if (DateTimeOffset.TryParse(text, culture, styles & ~DateTimeStyles.NoCurrentDateDefault, out DateTimeOffset zoned))
        {
            date = DateTime.SpecifyKind(zoned.DateTime, DateTimeKind.Unspecified);
            return true;
        }

        date = default;
        return false;
    }

    /// <summary>Whether a timestamp ends in a zone designator: <c>Z</c>, or an offset after its time.</summary>
    private static bool HasZone(ReadOnlySpan<char> text)
    {
        ReadOnlySpan<char> trimmed = text.TrimEnd();

        if (trimmed.Length > 0 && trimmed[^1] is 'Z' or 'z')
        {
            return true;
        }

        int time = trimmed.IndexOf(':');

        return time >= 0 && trimmed[time..].IndexOfAny('+', '-') >= 0;
    }

    /// <summary>
    /// Whether the text is shaped like a date at all.
    /// </summary>
    /// <remarks>
    /// Two separators, because one is how a decimal and a day-and-month are written and the parser
    /// takes both. A colon admits a time of day so that the parse can happen and the year check can
    /// refuse it, which is more honest than never looking.
    /// </remarks>
    public static bool LooksLikeOne(string text)
    {
        if (text.Contains(':', StringComparison.Ordinal))
        {
            return true;
        }

        ReadOnlySpan<char> span = text.AsSpan();
        int separators = span.Count('-') + span.Count('/') + span.Count('.');

        return separators >= 2;
    }
}
