using System.Globalization;

namespace TriasDev.Tabular.Analysis;

/// <summary>Which cultures this runtime actually has, asked the same way everywhere.</summary>
/// <remarks>
/// Under invariant globalization — common in slim container images — only the invariant culture
/// exists, and asking for de-DE throws. The analyzer, the profiler and the precheck all ask here, so
/// the library degrades the same way in each rather than failing in one and not another.
/// </remarks>
internal static class CultureCatalog
{
    /// <summary>Whether the runtime runs with invariant globalization, where no named culture exists.</summary>
    public static bool InvariantGlobalization { get; } =
        CultureInfo.GetCultures(CultureTypes.SpecificCultures).Length <= 1;

    /// <summary>Finds a culture by name; the empty name is the invariant culture.</summary>
    /// <remarks>
    /// Predefined cultures only: otherwise ICU invents a culture for any well-formed name, and a typo
    /// would read numbers by rules nobody chose.
    /// </remarks>
    public static bool TryGet(string? name, out CultureInfo culture)
    {
        if (string.IsNullOrEmpty(name))
        {
            culture = CultureInfo.InvariantCulture;
            return true;
        }

        try
        {
            culture = CultureInfo.GetCultureInfo(name, predefinedOnly: true);
            return true;
        }
        catch (CultureNotFoundException)
        {
            culture = CultureInfo.InvariantCulture;
            return false;
        }
    }

    /// <summary>The names this runtime has, in order; the invariant culture when it has none of them.</summary>
    public static IReadOnlyList<string> Available(IReadOnlyList<string> names)
    {
        List<string> available = [.. names.Where(name => TryGet(name, out _))];

        return available.Count == 0 ? [string.Empty] : available;
    }
}
