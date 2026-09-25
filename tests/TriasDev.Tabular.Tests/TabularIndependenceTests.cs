using System.Text.Json;

using Xunit;

namespace TriasDev.Tabular.Tests;

/// <summary>
/// Holds the library to its one structural promise: nothing here parses through someone else's
/// library.
/// </summary>
/// <remarks>
/// A test rather than a convention, because this is the kind of property that decays silently. A
/// reference added for one convenient helper would not fail anything, would not be noticed in review,
/// and would be discovered by the first consumer who asked why a package that claims no parsing
/// dependency ships with one.
/// </remarks>
public sealed class TabularIndependenceTests
{
    private static readonly string[] ForbiddenPackages =
    [
        "Sylvan.Data.Excel",
        "Sylvan.Data.Csv",
        "ExcelDataReader",
        "MiniExcel",
        "NPOI",
        "DocumentFormat.OpenXml",
        "CsvHelper",
        "Sep",
        "EPPlus",
        "ClosedXML",
    ];

    private static DirectoryInfo ProjectDirectory()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "TriasDev.Tabular.slnx")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);

        return new DirectoryInfo(Path.Combine(directory.FullName, "src", "TriasDev.Tabular"));
    }

    [Fact]
    public void ParsesThroughNobodyElsesLibrary()
    {
        // ADR-0001 chose a cursor of our own over every alternative measured. A package reappearing
        // here would not be a mistake so much as a decision, and it should be made in the open.
        //
        // Checked against the resolved graph, not against the project file. Reading the file alone
        // is what made this test weaker than it looked: the project declares no package references at
        // all, so all of this iterated an empty list, and anything arriving through the shared props
        // it imports was invisible.
        foreach (string package in ResolvedPackages())
        {
            Assert.DoesNotContain(
                ForbiddenPackages,
                forbidden => string.Equals(package, forbidden, StringComparison.OrdinalIgnoreCase));
        }
    }

    /// <summary>
    /// Every package the library resolves to, including those inherited from the repository's shared
    /// build properties.
    /// </summary>
    private static IEnumerable<string> ResolvedPackages()
    {
        DirectoryInfo directory = ProjectDirectory();
        string assets = Path.Combine(directory.FullName, "obj", "project.assets.json");

        Assert.True(File.Exists(assets), $"expected a restored project at {assets}");

        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(assets));

        if (!document.RootElement.TryGetProperty("libraries", out JsonElement libraries))
        {
            yield break;
        }

        foreach (JsonProperty library in libraries.EnumerateObject())
        {
            // Entries read "Package.Name/1.2.3".
            int slash = library.Name.IndexOf('/', StringComparison.Ordinal);
            yield return slash > 0 ? library.Name[..slash] : library.Name;
        }
    }
}
