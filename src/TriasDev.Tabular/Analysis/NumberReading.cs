using System.Globalization;
using System.Numerics;

namespace TriasDev.Tabular;

/// <summary>
/// The number-reading rules the profiler and the import must share, so the two cannot disagree.
/// </summary>
internal static class NumberReading
{
    /// <summary>
    /// How a decimal written as text is parsed: grouped, signed, and in exponential notation too.
    /// </summary>
    /// <remarks>
    /// <c>Number</c> alone refused <c>-6.9345e-03</c>, which a naive <c>double.ToString()</c> writes, so
    /// a longitude column fell below full confidence and its rows failed on import (#44).
    /// <c>Float</c> would take the exponent but drop the thousands, and <c>1,234.56</c> with them.
    /// Integers are parsed without it: a value in exponents is a measurement, and counts as a decimal
    /// even when it is whole.
    /// </remarks>
    public const NumberStyles DecimalStyles = NumberStyles.Number | NumberStyles.AllowExponent;

    /// <summary>How a whole number written as text is parsed: grouped and signed, no exponent.</summary>
    public const NumberStyles IntegerStyles = NumberStyles.Integer | NumberStyles.AllowThousands;

    /// <summary>How long a value may be for its copy with the culture's own apostrophe to sit on the stack.</summary>
    public const int StackCopyLimit = 128;

    /// <summary>
    /// Parses a number under a culture as the profile and the import both do: its groups held to three
    /// digits, and either apostrophe taken for the culture's own.
    /// </summary>
    /// <remarks>
    /// Which apostrophe <c>de-CH</c> groups with depends on the runtime's ICU data — <c>'</c> on one
    /// system, <c>’</c> on another — and .NET matches the culture's separator only, so a file typed with
    /// the ASCII apostrophe read on one machine and not on the next (#126). The other apostrophe is
    /// replaced in a copy, made only for a culture that groups with one and a value that holds the other.
    /// The profiler does the same inline, with the apostrophe looked up once per culture and the groups
    /// checked once for both parses: it asks this of every numeric-looking value under every culture.
    /// </remarks>
    public static bool TryParse<T>(ReadOnlySpan<char> text, NumberStyles styles, CultureInfo culture, out T value)
        where T : INumberBase<T>
    {
        char other = OtherApostropheOf(culture.NumberFormat);

        if (other != '\0' && text.Contains(other))
        {
            Span<char> own = text.Length <= StackCopyLimit ? stackalloc char[text.Length] : new char[text.Length];
            ToOwnApostrophe(text, other, own);

            return TryParseAsWritten(own, styles, culture, out value);
        }

        return TryParseAsWritten(text, styles, culture, out value);
    }

    private static bool TryParseAsWritten<T>(ReadOnlySpan<char> text, NumberStyles styles, CultureInfo culture, out T value)
        where T : INumberBase<T>
    {
        if (HasWellFormedGroups(text, culture.NumberFormat))
        {
            return T.TryParse(text, styles, culture, out value!);
        }

        value = T.Zero;
        return false;
    }

    /// <summary>The apostrophe a culture that groups with one does not use — <c>’</c> for <c>'</c> and back — or <c>\0</c>.</summary>
    public static char OtherApostropheOf(NumberFormatInfo format) => format.NumberGroupSeparator switch
    {
        "'" => '’',
        "’" => '\'',
        _ => '\0',
    };

    /// <summary>Copies a value into <paramref name="own"/>, as long as it, with the other apostrophe made the culture's own.</summary>
    public static void ToOwnApostrophe(ReadOnlySpan<char> text, char other, Span<char> own)
    {
        text.CopyTo(own);
        own.Replace(other, other == '\'' ? '’' : '\'');
    }

    /// <summary>
    /// Reads a value that writes the decimal separator the other way from the culture, where that is
    /// the only way it can be read: digits, one <c>.</c> or <c>,</c> — whichever the culture does not
    /// use for decimals — and digits after it that are not exactly three, which would make it a group.
    /// </summary>
    /// <remarks>
    /// <c>34.020367</c> among German decimals cannot be a German grouped number, so its only sensible
    /// reading is 34.020367 (#47). <c>1.234</c> could be either and is left alone; so is anything
    /// holding a second separator, a space or an exponent.
    /// </remarks>
    public static bool TryReadOtherSeparator(ReadOnlySpan<char> text, NumberFormatInfo format, out decimal value)
    {
        value = 0;

        char other = OtherSeparatorOf(format);

        if (!IsOtherSeparatorDecimal(text, other))
        {
            return false;
        }

        ReadOnlySpan<char> trimmed = text.Trim();
        Span<char> invariant = stackalloc char[trimmed.Length];
        trimmed.CopyTo(invariant);
        invariant.Replace(other, '.');

        // Never false for a value the shape admits: at most 28 digits, one point, one sign.
        return decimal.TryParse(invariant, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out value);
    }

    /// <summary>The decimal separator a culture does not use — <c>.</c> for <c>,</c> and back — or <c>\0</c>.</summary>
    public static char OtherSeparatorOf(NumberFormatInfo format) => format.NumberDecimalSeparator switch
    {
        "," => '.',
        "." => ',',
        _ => '\0',
    };

    /// <summary>
    /// Whether the value has the shape <see cref="TryReadOtherSeparator"/> reads, found without
    /// allocating or parsing: the profile asks it of every value that failed as a number, in every
    /// culture, and only counts.
    /// </summary>
    public static bool IsOtherSeparatorDecimal(ReadOnlySpan<char> text, char other)
    {
        ReadOnlySpan<char> body = text.Trim();

        if (other == '\0' || body.Length is 0 or > 30)
        {
            return false;
        }

        if (body[0] is '-' or '+')
        {
            body = body[1..];
        }

        // One pass that gives up at the first character that is neither a digit nor the separator:
        // the profile asks this of every text value that failed as a number, so a street name must
        // cost one character, not a scan.
        int at = -1;

        for (int i = 0; i < body.Length; i++)
        {
            char c = body[i];

            if (char.IsAsciiDigit(c))
            {
                continue;
            }

            if (c != other || at >= 0)
            {
                return false;
            }

            at = i;
        }

        // Digits on both sides, the fraction not exactly three digits long — that would be a group —
        // and no more than decimal holds.
        return at > 0 && at < body.Length - 1 && body.Length - at - 1 != 3 && body.Length - 1 <= 28;
    }

    /// <summary>
    /// Whether every group separator in the value is followed by exactly three digits.
    /// </summary>
    /// <remarks>
    /// .NET does not check group sizes, so under a German reading <c>19.99</c> parses as the integer
    /// 1,999 and <c>31.12.2023</c> as 31,122,023. The profiler refused such values while the import
    /// accepted them, so a precheck blocked a mapping whose import would have written wrong amounts.
    /// </remarks>
    public static bool HasWellFormedGroups(ReadOnlySpan<char> text, NumberFormatInfo format)
    {
        string separator = format.NumberGroupSeparator;

        if (separator.Length == 0 || !text.Contains(separator, StringComparison.Ordinal))
        {
            return true;
        }

        int decimalAt = text.IndexOf(format.NumberDecimalSeparator, StringComparison.Ordinal);
        ReadOnlySpan<char> integerPart = decimalAt >= 0 ? text[..decimalAt] : text;

        // Everything after the first separator must be groups of exactly three.
        int first = integerPart.IndexOf(separator, StringComparison.Ordinal);
        ReadOnlySpan<char> rest = integerPart[(first + separator.Length)..];

        while (true)
        {
            int next = rest.IndexOf(separator, StringComparison.Ordinal);
            ReadOnlySpan<char> group = next >= 0 ? rest[..next] : rest;

            if (group.Length != 3)
            {
                return false;
            }

            if (next < 0)
            {
                return true;
            }

            rest = rest[(next + separator.Length)..];
        }
    }
}
