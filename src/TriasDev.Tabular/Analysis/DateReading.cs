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
/// A month in words passes the shape test when the value ends in a four-digit year:
/// <c>15 Jan 2023</c> is a date, <c>15 Jan 23</c> is text. A genuine <c>0001-01-01</c> is refused,
/// and is recorded in the known-issues register rather than pretended away.
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
    /// <para>
    /// Two separators, because one is how a decimal and a day-and-month are written and the parser
    /// takes both. A colon admits a time of day so that the parse can happen and the year check can
    /// refuse it, which is more honest than never looking.
    /// </para>
    /// <para>
    /// A month in words — <c>17 SEP 1992</c>, <c>September 17, 1992</c> — is set apart by spaces, which
    /// the count above does not see. It cannot simply count them: <c>Mon 17 Sep</c> has two, and the
    /// parser completes it with the current year, which the year check cannot tell from a year the
    /// text stated. So a value in words must also end in a year of four digits.
    /// </para>
    /// </remarks>
    public static bool LooksLikeOne(string text)
    {
        if (text.Contains(':', StringComparison.Ordinal))
        {
            return true;
        }

        ReadOnlySpan<char> span = text.AsSpan();
        int separators = span.Count('-') + span.Count('/') + span.Count('.');

        return separators >= 2 || LooksLikeOneInWords(span, separators);
    }

    /// <summary>
    /// Whether the text could be a date with its month in words: a year of exactly four digits at its
    /// end, a letter before it, and at least two separators once spaces count among them.
    /// </summary>
    /// <remarks>
    /// The profile asks this of every value the separator count refused, which is most text, so a
    /// value that does not end in four digits is refused by its last characters, before any scan.
    /// The year must end the value because an address leads with its number: in a file of five million
    /// locations, a four-digit run anywhere sent 1.4 million street names on to three failing parses
    /// and cost the analysis 7%; at the end, thirty thousand.
    /// </remarks>
    private static bool LooksLikeOneInWords(ReadOnlySpan<char> text, int separators)
    {
        text = text.TrimEnd();

        if (text.Length is < 8 or > 40
            || !char.IsAsciiDigit(text[^1]) || !char.IsAsciiDigit(text[^2])
            || !char.IsAsciiDigit(text[^3]) || !char.IsAsciiDigit(text[^4])
            || char.IsAsciiDigit(text[^5]))
        {
            return false;
        }

        bool letter = false;

        foreach (char c in text[..^4])
        {
            if (c == ' ')
            {
                separators++;
            }
            else
            {
                letter |= char.IsLetter(c);
            }
        }

        return letter && separators >= 2;
    }
}
