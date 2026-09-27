using System.Globalization;
using System.Text;

namespace TriasDev.Tabular.Xlsx;

/// <summary>
/// ECMA-376's escape for a character in a string: <c>_xHHHH_</c>, the character's UTF-16 code in hex,
/// with <c>_x005F_</c> for an underscore that would otherwise start one.
/// </summary>
/// <remarks>
/// Writers use it for what XML cannot hold — a carriage return, a control character — and some for
/// much more: MiniExcel spells an emoji as its two surrogates. Read raw, a typed <c>_x000D_</c>, which
/// most writers escape as <c>_x005F_x000D_</c>, comes back as text nobody wrote.
/// </remarks>
internal static class OoxmlEscapes
{
    /// <summary>The value with its escapes resolved; the same instance when it holds none.</summary>
    public static string Decode(string value)
    {
        int at = value.IndexOf("_x", StringComparison.Ordinal);

        if (at < 0)
        {
            return value;
        }

        StringBuilder? decoded = null;
        int copied = 0;

        while (at >= 0)
        {
            if (at + 7 <= value.Length
                && value[at + 6] == '_'
                && int.TryParse(value.AsSpan(at + 2, 4), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out int code))
            {
                decoded ??= new StringBuilder(value.Length);
                decoded.Append(value, copied, at - copied).Append((char)code);
                copied = at + 7;
                at = value.IndexOf("_x", copied, StringComparison.Ordinal);
            }
            else
            {
                at = value.IndexOf("_x", at + 1, StringComparison.Ordinal);
            }
        }

        return decoded is null ? value : decoded.Append(value, copied, value.Length - copied).ToString();
    }
}
