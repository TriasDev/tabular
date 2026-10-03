using System.Buffers;

namespace TriasDev.Tabular;

/// <summary>The rules a sheet name meets in a workbook — Excel's, which LibreOffice also keeps.</summary>
internal static class SheetNames
{
    /// <summary>Excel's limit on a sheet name's length.</summary>
    public const int MaxLength = 31;

    private static readonly SearchValues<char> Forbidden = SearchValues.Create("[]:*?/\\");

    /// <summary>Why the name cannot be used, or null when it can. <paramref name="taken"/> compares ignoring case.</summary>
    public static string? Problem(string name, ISet<string> taken)
    {
        if (name.Length is 0 or > MaxLength)
        {
            return $"A sheet name has 1 to {MaxLength} characters.";
        }

        if (name.AsSpan().IndexOfAny(Forbidden) >= 0)
        {
            return "A sheet name holds none of [ ] : * ? / \\.";
        }

        if (name[0] == '\'' || name[^1] == '\'')
        {
            return "A sheet name neither starts nor ends with an apostrophe.";
        }

        if (string.Equals(name, "History", StringComparison.OrdinalIgnoreCase))
        {
            return "\"History\" is reserved by Excel.";
        }

        if (TextRules.Check(name) is not null)
        {
            return "A sheet name holds a character XML cannot carry.";
        }

        return taken.Contains(name) ? $"The workbook already has a sheet named \"{name}\", ignoring case." : null;
    }
}
