using System.Globalization;

namespace TriasDev.Tabular;

/// <summary>
/// The number-reading rules the profiler and the import must share, so the two cannot disagree.
/// </summary>
internal static class NumberReading
{
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
