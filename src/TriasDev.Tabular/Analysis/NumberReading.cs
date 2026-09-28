using System.Globalization;

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
