using System.Globalization;
using System.Text;

namespace TriasDev.Tabular;

/// <summary>What a part of a date format shows.</summary>
internal enum DatePartKind
{
    Literal,
    Year,
    Month,
    Day,
    Hour,
    Minute,
    Second,
}

/// <summary>One part of a date format: a field (long = two digits, or four for the year) or literal text.</summary>
internal readonly record struct DatePart(DatePartKind Kind, bool Long, string Text);

/// <summary>
/// How a date or date-time cell shows its value, as an Excel format code from a subset both workbook
/// formats can state: <c>yyyy</c> <c>yy</c> <c>m</c> <c>mm</c> <c>d</c> <c>dd</c> <c>h</c> <c>hh</c>
/// <c>s</c> <c>ss</c>, separators <c>/ - . : , space</c>, quoted or backslash-escaped text — as in
/// <c>dd/mm/yyyy</c> or <c>yyyy-mm-dd hh:mm</c>.
/// </summary>
/// <remarks>
/// <para>
/// <c>m</c> and <c>mm</c> are minutes right after an hour or right before a second, months
/// otherwise — Excel's rule. The separators are written as literal text, so <c>/</c> shows as a
/// slash whatever the reader's locale.
/// </para>
/// <para>The value stays the date written; only its display changes. Compares by <see cref="Code"/>.</para>
/// </remarks>
public sealed record DateFormat
{
    private DateFormat(string code, DatePart[] parts)
    {
        Code = code;
        Parts = parts;
    }

    /// <summary>The format code as written into a workbook: lower-case fields, separators escaped.</summary>
    public string Code { get; }

    internal IReadOnlyList<DatePart> Parts { get; }

    /// <summary>Parses a format code.</summary>
    /// <exception cref="ArgumentException">The code is outside the supported subset; the message names the part.</exception>
    public static DateFormat Parse(string code)
    {
        ArgumentNullException.ThrowIfNull(code);

        if (code.Length == 0)
        {
            throw Refuse(code, "the code is empty");
        }

        List<DatePart> parts = [];
        StringBuilder literal = new();
        int i = 0;

        while (i < code.Length)
        {
            if (TryReadLiteral(code, ref i, literal))
            {
                continue;
            }

            char c = code[i];
            char letter = char.ToLowerInvariant(c);

            if (letter is not ('y' or 'm' or 'd' or 'h' or 's'))
            {
                throw Refuse(code, FormatCodes.At(code, i));
            }

            int run = 1;

            while (i + run < code.Length && char.ToLowerInvariant(code[i + run]) == letter)
            {
                run++;
            }

            DatePart field = Field(code, letter, run);
            Flush(code, parts, literal);
            parts.Add(field);
            i += run;
        }

        Flush(code, parts, literal);

        if (parts.TrueForAll(p => p.Kind == DatePartKind.Literal))
        {
            throw Refuse(code, "no date or time part");
        }

        ResolveMinutes(parts);
        return new DateFormat(Canonical(parts), [.. parts]);
    }

    /// <inheritdoc/>
    public bool Equals(DateFormat? other) => other is not null && string.Equals(Code, other.Code, StringComparison.Ordinal);

    /// <inheritdoc/>
    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Code);

    /// <inheritdoc/>
    public override string ToString() => Code;

    /// <summary>Reads a quoted, escaped or separator literal at <paramref name="i"/> into <paramref name="literal"/>, if one starts there.</summary>
    private static bool TryReadLiteral(string code, ref int i, StringBuilder literal)
    {
        char c = code[i];

        if (c == '"')
        {
            literal.Append(FormatCodes.ReadQuoted(code, ref i));
            return true;
        }

        if (c == '\\')
        {
            if (i + 1 >= code.Length)
            {
                throw Refuse(code, "a backslash at the end, escaping nothing");
            }

            literal.Append(code[i + 1]);
            i += 2;
            return true;
        }

        if (c is '/' or '-' or '.' or ':' or ',' or ' ')
        {
            literal.Append(c);
            i++;
            return true;
        }

        return false;
    }

    private static DatePart Field(string code, char letter, int run)
    {
        string token = new(letter, run);

        return (letter, run) switch
        {
            ('y', 2) => new DatePart(DatePartKind.Year, false, token),
            ('y', 4) => new DatePart(DatePartKind.Year, true, token),
            ('m', <= 2) => new DatePart(DatePartKind.Month, run == 2, token),
            ('d', <= 2) => new DatePart(DatePartKind.Day, run == 2, token),
            ('h', <= 2) => new DatePart(DatePartKind.Hour, run == 2, token),
            ('s', <= 2) => new DatePart(DatePartKind.Second, run == 2, token),
            _ => throw Refuse(code, $"\"{token}\" (month and weekday names, and years other than yy or yyyy, are not supported)"),
        };
    }

    private static void Flush(string code, List<DatePart> parts, StringBuilder literal)
    {
        if (literal.Length > 0)
        {
            if (TextRules.Check(literal.ToString()) is { } problem)
            {
                throw Refuse(code, $"its literal text cannot be written ({problem})");
            }

            parts.Add(new DatePart(DatePartKind.Literal, false, literal.ToString()));
            literal.Clear();
        }
    }

    /// <summary>Excel's rule: m right after an hour, or right before a second, is minutes.</summary>
    private static void ResolveMinutes(List<DatePart> parts)
    {
        for (int i = 0; i < parts.Count; i++)
        {
            if (parts[i].Kind != DatePartKind.Month)
            {
                continue;
            }

            DatePartKind before = Neighbour(parts, i, -1);
            DatePartKind after = Neighbour(parts, i, +1);

            if (before == DatePartKind.Hour || after == DatePartKind.Second)
            {
                parts[i] = parts[i] with { Kind = DatePartKind.Minute };
            }
        }
    }

    private static DatePartKind Neighbour(List<DatePart> parts, int i, int step)
    {
        for (int j = i + step; j >= 0 && j < parts.Count; j += step)
        {
            if (parts[j].Kind != DatePartKind.Literal)
            {
                return parts[j].Kind;
            }
        }

        return DatePartKind.Literal;
    }

    /// <summary>Fields in lower case; literal characters escaped with a backslash, except ':', which Excel's own date-time formats leave bare.</summary>
    private static string Canonical(List<DatePart> parts)
    {
        StringBuilder code = new();

        foreach (DatePart part in parts)
        {
            if (part.Kind != DatePartKind.Literal)
            {
                code.Append(part.Text);
                continue;
            }

            string text = part.Text;

            int i = 0;

            while (i < text.Length)
            {
                char c = text[i++];

                if (char.IsHighSurrogate(c) && i < text.Length && char.IsLowSurrogate(text[i]))
                {
                    // one unit: a backslash would escape only the high half, so the pair goes in quotes
                    code.Append('"').Append(c).Append(text[i++]).Append('"');
                    continue;
                }

                if (c != ':')
                {
                    code.Append('\\');
                }

                code.Append(c);
            }
        }

        return code.ToString();
    }

    private static ArgumentException Refuse(string code, string what) =>
        new($"The date format \"{code}\" is not supported: {what}.", nameof(code));
}

/// <summary>The pieces of format-code parsing both parsers share.</summary>
internal static class FormatCodes
{
    /// <summary>Reads a double-quoted literal at <paramref name="i"/>, if one starts there; "" otherwise.</summary>
    public static string ReadQuoted(string code, ref int i)
    {
        if (i >= code.Length || code[i] != '"')
        {
            return string.Empty;
        }

        int end = code.IndexOf('"', i + 1);

        if (end < 0)
        {
            throw new ArgumentException($"The format \"{code}\" is not supported: a quote at position {i + 1} that never closes.", nameof(code));
        }

        string text = code[(i + 1)..end];

        if (TextRules.Check(text) is { } problem)
        {
            throw new ArgumentException($"The format \"{code}\" is not supported: its quoted text cannot be written ({problem}).", nameof(code));
        }

        i = end + 1;
        return text;
    }

    /// <summary>The literal as a quoted run of a format code, or "" for none.</summary>
    public static string Quote(string text) => text.Length == 0 ? string.Empty : "\"" + text + "\"";

    /// <summary>Names the character at a position, for a refusal.</summary>
    public static string At(string code, int i) => string.Create(CultureInfo.InvariantCulture, $"'{code[i]}' at position {i + 1}");
}
