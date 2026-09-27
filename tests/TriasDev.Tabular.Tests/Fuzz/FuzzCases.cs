using System.Globalization;

namespace TriasDev.Tabular.Tests.Fuzz;

/// <summary>
/// Seeded random cases: the same every run, so a failure reproduces, and more of them on request.
/// </summary>
/// <remarks>
/// <c>TABULAR_FUZZ_CASES</c> raises the count for a long local run; <c>TABULAR_FUZZ_SEED</c> replays one
/// case a failure named. Each case gets its own generator, seeded from the base seed and its number,
/// so a case reproduces on its own without the ones before it.
/// </remarks>
internal static class FuzzCases
{
    private const int BaseSeed = 20260927;

    public static IEnumerable<(int Seed, Random Random)> Generate(int defaultCount)
    {
        if (Environment.GetEnvironmentVariable("TABULAR_FUZZ_SEED") is { } one
            && int.TryParse(one, NumberStyles.Integer, CultureInfo.InvariantCulture, out int seed))
        {
            yield return (seed, new Random(seed));
            yield break;
        }

        int count = int.TryParse(Environment.GetEnvironmentVariable("TABULAR_FUZZ_CASES"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int requested)
            ? requested
            : defaultCount;

        for (int i = 0; i < count; i++)
        {
            int caseSeed = unchecked(BaseSeed + (i * 7919));
            yield return (caseSeed, new Random(caseSeed));
        }
    }

    /// <summary>Keeps a failing case's input where <c>TABULAR_FUZZ_DUMP</c> points, to look at by hand.</summary>
    public static void Keep(int seed, string extension, byte[] content)
    {
        if (Environment.GetEnvironmentVariable("TABULAR_FUZZ_DUMP") is { Length: > 0 } folder)
        {
            Directory.CreateDirectory(folder);
            File.WriteAllBytes(Path.Combine(folder, $"{seed}{extension}"), content);
        }
    }

    public static T Pick<T>(this Random random, params T[] items) => items[random.Next(items.Length)];

    public static bool Chance(this Random random, double probability) => random.NextDouble() < probability;
}
