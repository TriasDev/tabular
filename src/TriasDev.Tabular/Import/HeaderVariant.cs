namespace TriasDev.Tabular.Import;

/// <summary>
/// Reads a language out of a column header, where the file marks one.
/// </summary>
/// <remarks>
/// <para>
/// A proposal, never a decision. A header is written by whoever exported the file and is wrong often
/// enough that acting on it silently would be the worst kind of mistake here: the text arrives, it is
/// simply filed under the wrong language, and nobody finds out until a reader of that language does.
/// Whatever this returns belongs in front of a person before it becomes a binding.
/// </para>
/// <para>
/// Splitting only on a variant that was actually declared is what keeps it from inventing marks.
/// <c>Order_id</c> is not an order in Indonesian, and a rule that splits on any trailing token after
/// an underscore would say it was.
/// </para>
/// </remarks>
public static class HeaderVariant
{
    /// <summary>
    /// The separators seen in the wild, in the order they are tried.
    /// </summary>
    /// <remarks>
    /// The hash comes first because it is the one already in production here — the TOM catalogue
    /// writes <c>Title#en</c> — and the rest are what the next customer will write instead.
    /// </remarks>
    private static readonly string[] Separators = ["#", "_", "-", ".", " ", "@"];

    /// <summary>
    /// Splits a header into what it names and which variant of it, against the variants a group
    /// declares.
    /// </summary>
    /// <param name="header">The column header as the file holds it.</param>
    /// <param name="variants">The variants that exist. Nothing else is treated as a mark.</param>
    /// <param name="name">What the header names, with the mark removed.</param>
    /// <param name="variant">The variant, spelled as it was declared rather than as the file spelled it.</param>
    /// <returns>Whether the header carried a mark at all.</returns>
    public static bool TryParse(
        string? header,
        IEnumerable<string> variants,
        out string name,
        out string variant)
    {
        ArgumentNullException.ThrowIfNull(variants);

        name = header?.Trim() ?? string.Empty;
        variant = string.Empty;

        if (name.Length == 0)
        {
            return false;
        }

        foreach (string declared in variants)
        {
            if (string.IsNullOrWhiteSpace(declared))
            {
                continue;
            }

            foreach (string separator in Separators)
            {
                // Bracketed forms first: "Title (de)" is common enough from spreadsheet exporters
                // that leaving it to the plain-suffix rule would miss it, since the mark is not at
                // the end of the string.
                foreach (string candidate in (string[])[$"{separator}{declared}", $"({declared})", $"[{declared}]"])
                {
                    if (!name.EndsWith(candidate, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    string stripped = name[..^candidate.Length].TrimEnd(' ', '_', '-', '#', '.');

                    if (stripped.Length == 0)
                    {
                        // The header is nothing but the mark. "de" alone names no field, and
                        // proposing an empty one would be worse than proposing nothing.
                        continue;
                    }

                    name = stripped;
                    variant = declared;
                    return true;
                }
            }
        }

        return false;
    }
}
