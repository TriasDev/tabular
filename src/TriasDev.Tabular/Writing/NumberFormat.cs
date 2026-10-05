using System.Globalization;

namespace TriasDev.Tabular;

/// <summary>
/// How a numeric cell shows its value, as an Excel format code from a subset both workbook formats
/// can state: <c>0</c>, <c>0.00</c>, <c>#,##0</c>, <c>#,##0.00</c>, <c>0.0#</c>, <c>0%</c>, and
/// quoted text before or after, as in <c>"€ "#,##0.00</c>.
/// </summary>
/// <remarks>
/// The value stays the number written; only its display changes. The decimal point and the
/// thousands separator show as the reader's locale has them.
/// </remarks>
public sealed record NumberFormat
{
    private const int MaxDecimals = 30;

    private NumberFormat(string code, bool grouping, int minIntegerDigits, int decimalPlaces, int minDecimalPlaces, bool percent, string prefix, string suffix)
    {
        Code = code;
        Grouping = grouping;
        MinIntegerDigits = minIntegerDigits;
        DecimalPlaces = decimalPlaces;
        MinDecimalPlaces = minDecimalPlaces;
        Percent = percent;
        Prefix = prefix;
        Suffix = suffix;
    }

    /// <summary>The format code as written into a workbook: the canonical form of what was parsed.</summary>
    public string Code { get; }

    internal bool Grouping { get; }

    internal int MinIntegerDigits { get; }

    internal int DecimalPlaces { get; }

    internal int MinDecimalPlaces { get; }

    internal bool Percent { get; }

    internal string Prefix { get; }

    internal string Suffix { get; }

    /// <summary>Parses a format code.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="code"/> is null.</exception>
    /// <exception cref="FormatException">The code is empty or outside the supported subset; the message names the part.</exception>
    public static NumberFormat Parse(string code)
    {
        ArgumentNullException.ThrowIfNull(code);

        if (code.Length == 0)
        {
            throw Refuse(code, "the code is empty");
        }

        int i = 0;
        string prefix = FormatCodes.ReadQuoted(code, ref i);

        int integerStart = i;
        (int zeros, bool grouping) = ReadInteger(code, ref i);
        string integer = code[integerStart..i];
        (string decimalsText, int decimals, int minDecimals) = ReadDecimals(code, ref i);

        bool percent = i < code.Length && code[i] == '%';
        i += percent ? 1 : 0;

        string suffix = FormatCodes.ReadQuoted(code, ref i);

        if (i < code.Length)
        {
            throw Refuse(code, FormatCodes.At(code, i));
        }

        string canonical = FormatCodes.Quote(prefix) + integer + (decimals > 0 ? "." + decimalsText : string.Empty) + (percent ? "%" : string.Empty) + FormatCodes.Quote(suffix);
        return new NumberFormat(canonical, grouping, zeros, decimals, minDecimals, percent, prefix, suffix);
    }

    /// <inheritdoc/>
    public override string ToString() => Code;

    private static (int Zeros, bool Grouping) ReadInteger(string code, ref int i)
    {
        int digits = 0;
        int zeros = 0;
        bool grouping = false;

        for (; i < code.Length && code[i] is '#' or '0' or ','; i++)
        {
            switch (code[i])
            {
                case '#' when zeros > 0:
                    throw Refuse(code, "a # after a 0 in the integer part");
                case '#':
                    digits++;
                    break;
                case '0':
                    zeros++;
                    digits++;
                    break;
                default:
                    if (digits == 0 || i + 1 >= code.Length || code[i + 1] is not ('#' or '0'))
                    {
                        throw Refuse(code, "a comma that is not a thousands separator between digit placeholders");
                    }

                    grouping = true;
                    break;
            }
        }

        if (digits == 0)
        {
            throw Refuse(code, i < code.Length ? FormatCodes.At(code, i) : "no digit placeholder (0 or #) in the integer part");
        }

        return (zeros, grouping);
    }

    private static (string Text, int Places, int MinPlaces) ReadDecimals(string code, ref int i)
    {
        if (i >= code.Length || code[i] != '.')
        {
            return (string.Empty, 0, 0);
        }

        i++;
        int start = i;
        int decimals = 0;
        int minDecimals = 0;
        bool optional = false;

        for (; i < code.Length && code[i] is '0' or '#'; i++)
        {
            if (code[i] == '0' && optional)
            {
                throw Refuse(code, "a 0 after a # in the decimals");
            }

            optional |= code[i] == '#';
            minDecimals += code[i] == '0' ? 1 : 0;
            decimals++;
        }

        if (decimals == 0)
        {
            throw Refuse(code, "a decimal point without digit placeholders after it");
        }

        if (decimals > MaxDecimals)
        {
            throw Refuse(code, string.Create(CultureInfo.InvariantCulture, $"more than {MaxDecimals} decimal places"));
        }

        return (code[start..i], decimals, minDecimals);
    }

    private static FormatException Refuse(string code, string what) =>
        new($"The number format \"{code}\" is not supported: {what}.");
}
