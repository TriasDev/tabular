namespace TriasDev.Tabular.Mapping;

/// <summary>
/// The check-digit schemes identifiers commonly carry, for use as rules with <c>Must</c>.
/// </summary>
/// <remarks>
/// <para>
/// Both accept digits and upper-case letters, a letter counting as its two-digit value (A = 10 …
/// Z = 35) the way the identifiers that use them define it. Anything else — a lower-case letter, a
/// space, a hyphen — fails: normalise before checking if the source writes identifiers loosely.
/// </para>
/// <para>
/// They check the digits, not the shape. An ISIN is twelve characters starting with two letters; a
/// LEI twenty; that is a length rule and a pattern beside this one.
/// </para>
/// </remarks>
public static class CheckDigits
{
    /// <summary>
    /// Whether the value passes the Luhn (mod 10) check — card numbers, and ISINs with their letters
    /// expanded.
    /// </summary>
    public static bool Luhn(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        if (value.Length == 0)
        {
            return false;
        }

        // Walked from the right, doubling every second digit. A letter contributes two digits, so the
        // position is counted in digits, not characters.
        int sum = 0;
        bool doubled = false;

        for (int i = value.Length - 1; i >= 0; i--)
        {
            if (!TryValue(value[i], out int v))
            {
                return false;
            }

            if (v >= 10)
            {
                sum += Weigh(v % 10, ref doubled) + Weigh(v / 10, ref doubled);
            }
            else
            {
                sum += Weigh(v, ref doubled);
            }
        }

        return sum % 10 == 0;
    }

    /// <summary>
    /// Whether the value passes ISO 7064 MOD 97-10 — LEIs as written, IBANs with their first four
    /// characters moved to the end.
    /// </summary>
    public static bool Mod97(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        if (value.Length == 0)
        {
            return false;
        }

        // The number is far too long for any integer type, so the remainder is carried digit by digit.
        int remainder = 0;

        foreach (char c in value)
        {
            if (!TryValue(c, out int v))
            {
                return false;
            }

            remainder = v >= 10
                ? ((remainder * 100) + v) % 97
                : ((remainder * 10) + v) % 97;
        }

        return remainder == 1;
    }

    private static int Weigh(int digit, ref bool doubled)
    {
        int weighed = doubled ? digit * 2 : digit;
        doubled = !doubled;
        return weighed > 9 ? weighed - 9 : weighed;
    }

    private static bool TryValue(char c, out int value)
    {
        value = c switch
        {
            >= '0' and <= '9' => c - '0',
            >= 'A' and <= 'Z' => c - 'A' + 10,
            _ => -1,
        };

        return value >= 0;
    }
}
