using System.Diagnostics;
using System.Text;

using Xunit;

namespace TriasDev.Tabular.Tests.Fixtures;

/// <summary>
/// LibreOffice headless, as the judge of whether a file we write is one a spreadsheet program opens.
/// </summary>
/// <remarks>
/// Skips where LibreOffice is not installed — unless <c>TABULAR_REQUIRE_SOFFICE</c> is set, as CI's
/// Linux job sets it, where a missing LibreOffice is a failure rather than a pass by absence.
/// </remarks>
public static class LibreOffice
{
    private static readonly string? Soffice = Find();

    /// <summary>Converts the file's first sheet to csv with LibreOffice and returns its lines.</summary>
    public static string[] ConvertToCsv(byte[] file, string extension)
    {
        if (Soffice is null)
        {
            Assert.False(Environment.GetEnvironmentVariable("TABULAR_REQUIRE_SOFFICE") == "1", "LibreOffice is required here but soffice was not found.");
            Assert.Skip("LibreOffice is not installed.");
        }

        string folder = Path.Combine(Path.GetTempPath(), "tabular-soffice-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);

        try
        {
            string input = Path.Combine(folder, "file." + extension);
            File.WriteAllBytes(input, file);

            ProcessStartInfo start = new(Soffice)
            {
                ArgumentList =
                {
                    "--headless",
                    $"-env:UserInstallation=file://{folder.Replace('\\', '/')}/profile",
                    "--convert-to",
                    "csv:Text - txt - csv (StarCalc):44,34,76",
                    "--outdir",
                    folder,
                    input,
                },
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };

            using Process process = Process.Start(start)!;
            string output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
            Assert.True(process.WaitForExit(120_000), "LibreOffice did not finish within two minutes.");

            string csv = Path.Combine(folder, "file.csv");
            Assert.True(File.Exists(csv), $"LibreOffice could not convert the file: {output}");

            return File.ReadAllLines(csv, Encoding.UTF8);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    private static string? Find()
    {
        string[] candidates =
        [
            "/Applications/LibreOffice.app/Contents/MacOS/soffice",
            @"C:\Program Files\LibreOffice\program\soffice.exe",
            .. (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
                .Select(directory => Path.Combine(directory, "soffice")),
        ];

        return candidates.FirstOrDefault(File.Exists);
    }
}
