using System.Buffers;

namespace TriasDev.Tabular;

/// <summary>The characters no format may carry: the ones XML 1.0 forbids.</summary>
/// <remarks>
/// csv could carry them, but one rule for every format means an export that a workbook refuses is
/// not quietly accepted as csv: the same data fails the same way whichever format is chosen.
/// </remarks>
internal static class TextRules
{
    private static readonly SearchValues<char> Forbidden = SearchValues.Create(
        [.. Enumerable.Range(0, 0x20).Where(c => c is not (0x09 or 0x0A or 0x0D)).Select(c => (char)c), '￾', '￿']);

    /// <summary>Null when every character may be written, otherwise <see cref="ErrorCodes.Write.InvalidCharacter"/>.</summary>
    public static string? Check(string text)
    {
        ReadOnlySpan<char> span = text;

        if (span.IndexOfAny(Forbidden) >= 0)
        {
            return ErrorCodes.Write.InvalidCharacter;
        }

        int surrogate = span.IndexOfAnyInRange('\uD800', '\uDFFF');

        return surrogate < 0 || PairsAreWhole(span[surrogate..]) ? null : ErrorCodes.Write.InvalidCharacter;
    }

    /// <summary>Whether every surrogate is a high one directly followed by a low one.</summary>
    private static bool PairsAreWhole(ReadOnlySpan<char> span)
    {
        int i = 0;
        while (i < span.Length)
        {
            if (char.IsHighSurrogate(span[i]))
            {
                if (i + 1 == span.Length || !char.IsLowSurrogate(span[i + 1]))
                {
                    return false;
                }

                i += 2;
            }
            else if (char.IsLowSurrogate(span[i]))
            {
                return false;
            }
            else
            {
                i++;
            }
        }

        return true;
    }
}
