using System.Reflection;
using System.Text.RegularExpressions;

using Xunit;

namespace TriasDev.Tabular.Tests;

/// <summary>
/// The error codes are a frontend's translation contract, and the guide's table is where a frontend
/// reads them. Nothing kept the two in step, and the register that said so watched the drift happen
/// twice in the branch that recorded it — a code added, asserted, given a requirement, described in
/// the guide's own prose, and left out of the guide's table.
/// </summary>
public sealed class ErrorCodeCatalogTests
{
    private static readonly Regex CodeInSource = new(
        """"(?<code>(?:value|mapping|group|structure|format|limit)\.[a-z-]+)"""",
        RegexOptions.Compiled);

    private static readonly Regex CodeInMarkdown = new(
        @"`(?<code>(?:value|mapping|group|structure|format|limit)\.[a-z-]+)`",
        RegexOptions.Compiled);

    [Fact]
    public void TheReadmeListsEveryCodeTheLibraryEmits()
    {
        HashSet<string> emitted = Emitted();
        HashSet<string> documented = Documented();

        string[] missing = [.. emitted.Except(documented).Order(StringComparer.Ordinal)];

        Assert.True(
            missing.Length == 0,
            "The guide's error-code table is missing: " + string.Join(", ", missing));
    }

    [Fact]
    public void TheReadmeListsNoCodeTheLibraryDoesNotEmit()
    {
        string[] invented = [.. Documented().Except(Emitted()).Order(StringComparer.Ordinal)];

        Assert.True(
            invented.Length == 0,
            "The guide's error-code table names codes nothing emits: " + string.Join(", ", invented));
    }

    /// <summary>Every code the library's own sources contain as a literal.</summary>
    private static HashSet<string> Emitted()
    {
        HashSet<string> codes = new(StringComparer.Ordinal);

        foreach (string file in Directory.EnumerateFiles(Path.Combine(RepositoryRoot(), "src", "TriasDev.Tabular"), "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            {
                continue;
            }

            foreach (Match match in CodeInSource.Matches(File.ReadAllText(file)))
            {
                codes.Add(match.Groups["code"].Value);
            }
        }

        Assert.NotEmpty(codes);

        return codes;
    }

    /// <summary>Every code named in the error-code table of docs/guide.md.</summary>
    private static HashSet<string> Documented()
    {
        string readme = File.ReadAllText(Path.Combine(RepositoryRoot(), "docs", "guide.md"));

        int start = readme.IndexOf("## Error codes", StringComparison.Ordinal);

        Assert.True(start >= 0, "The guide has no error-code section.");

        int end = readme.IndexOf("\n## ", start + 1, StringComparison.Ordinal);
        string section = end < 0 ? readme[start..] : readme[start..end];

        HashSet<string> codes = new(StringComparer.Ordinal);

        foreach (Match match in CodeInMarkdown.Matches(section))
        {
            codes.Add(match.Groups["code"].Value);
        }

        return codes;
    }

    /// <summary>
    /// The repository root, walked up to from the test assembly.
    /// </summary>
    /// <remarks>
    /// The only test here that reads the source tree, and it has to: the point is to compare what is
    /// written against what is compiled, which a compiled assembly alone cannot answer.
    /// </remarks>
    private static string RepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);

        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "TriasDev.Tabular.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Could not find the repository root above the test assembly.");
    }
}
