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

    /// <summary>Converts the file's first sheet to csv with LibreOffice and returns its lines, values as shown.</summary>
    public static string[] ConvertToCsv(byte[] file, string extension) =>
        Encoding.UTF8.GetString(Convert(file, extension, "csv:Text - txt - csv (StarCalc):44,34,76,1,,0,true", "csv"))
            .Split('\n')
            .Select(line => line.TrimEnd('\r'))
            .Reverse()
            .SkipWhile((line, i) => i == 0 && line.Length == 0)
            .Reverse()
            .ToArray();

    /// <summary>
    /// Opens a file in LibreOffice through a macro, so the document has a view, and stores it again as ods.
    /// A headless conversion drops view state such as frozen panes; a document opened by a macro keeps it,
    /// and the resaved file states what LibreOffice understood of the original.
    /// </summary>
    public static byte[] Resave(byte[] file, string extension)
    {
        string soffice = Require();
        string folder = Path.Combine(Path.GetTempPath(), "tabular-soffice-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);

        try
        {
            string input = Path.Combine(folder, "file." + extension);
            string output = Path.Combine(folder, "resaved.ods");
            string profile = Path.Combine(folder, "profile");
            File.WriteAllBytes(input, file);

            string[] profileArguments = ["--headless", "--invisible", $"-env:UserInstallation={new Uri(profile).AbsoluteUri}"];

            // The first start creates the profile and overwrites its Standard library, so it runs before the macro is written.
            Run(soffice, [.. profileArguments, "--terminate_after_init"]);

            string library = Path.Combine(profile, "user", "basic");
            string standard = Path.Combine(library, "Standard");
            Directory.CreateDirectory(standard);
            const string Header = "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n";
            const string Namespace = "xmlns:script=\"http://openoffice.org/2000/script\"";
            const string Libraries = "<library:libraries xmlns:library=\"http://openoffice.org/2000/library\" xmlns:xlink=\"http://www.w3.org/1999/xlink\"><library:library library:name=\"Standard\" library:link=\"false\"/></library:libraries>";
            static string Library(string elements) =>
                "<library:library xmlns:library=\"http://openoffice.org/2000/library\" library:name=\"Standard\" library:readonly=\"false\" library:passwordprotected=\"false\">" + elements + "</library:library>";
            File.WriteAllText(Path.Combine(library, "script.xlc"), Header + "<!DOCTYPE library:libraries PUBLIC \"-//OpenOffice.org//DTD OfficeDocument 1.0//EN\" \"libraries.dtd\">\n" + Libraries);
            File.WriteAllText(Path.Combine(library, "dialog.xlc"), Header + "<!DOCTYPE library:libraries PUBLIC \"-//OpenOffice.org//DTD OfficeDocument 1.0//EN\" \"libraries.dtd\">\n" + Libraries);
            File.WriteAllText(Path.Combine(standard, "script.xlb"), Header + "<!DOCTYPE library:library PUBLIC \"-//OpenOffice.org//DTD OfficeDocument 1.0//EN\" \"library.dtd\">\n" + Library("<library:element library:name=\"Module1\"/>"));
            File.WriteAllText(Path.Combine(standard, "dialog.xlb"), Header + "<!DOCTYPE library:library PUBLIC \"-//OpenOffice.org//DTD OfficeDocument 1.0//EN\" \"library.dtd\">\n" + Library(string.Empty));

            string code = "Sub Main\n"
                + "  Dim noArgs()\n"
                + "  Dim doc As Object\n"
                + $"  doc = StarDesktop.loadComponentFromURL(\"{new Uri(input).AbsoluteUri}\", \"_blank\", 0, noArgs())\n"
                + "  Dim args(0) As New com.sun.star.beans.PropertyValue\n"
                + "  args(0).Name = \"FilterName\"\n"
                + "  args(0).Value = \"calc8\"\n"
                + $"  doc.storeToURL(\"{new Uri(output).AbsoluteUri}\", args())\n"
                + "  doc.close(True)\n"
                + "End Sub\n";
            File.WriteAllText(Path.Combine(standard, "Module1.xba"), Header + "<!DOCTYPE script:module PUBLIC \"-//OpenOffice.org//DTD OfficeDocument 1.0//EN\" \"module.dtd\">\n"
                + $"<script:module {Namespace} script:name=\"Module1\" script:language=\"StarBasic\">{System.Security.SecurityElement.Escape(code)}</script:module>");

            string log = Run(soffice, [.. profileArguments, "macro:///Standard.Module1.Main"]);
            Assert.True(File.Exists(output), $"LibreOffice could not open and resave the file: {log}");

            return File.ReadAllBytes(output);
        }
        finally
        {
            Cleanup(folder);
        }
    }

    /// <summary>Converts a file with LibreOffice, by an export filter, and returns the converted file.</summary>
    public static byte[] Convert(byte[] file, string extension, string filter, string outputExtension)
    {
        string soffice = Require();
        string folder = Path.Combine(Path.GetTempPath(), "tabular-soffice-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);

        try
        {
            string input = Path.Combine(folder, "file." + extension);
            File.WriteAllBytes(input, file);

            string output = Run(
                soffice,
                [
                    "--headless",
                    $"-env:UserInstallation={new Uri(Path.Combine(folder, "profile")).AbsoluteUri}",
                    "--convert-to",
                    filter,
                    "--outdir",
                    folder,
                    input,
                ]);

            string converted = Path.Combine(folder, "file." + outputExtension);
            Assert.True(File.Exists(converted), $"LibreOffice could not convert the file: {output}");

            return File.ReadAllBytes(converted);
        }
        finally
        {
            Cleanup(folder);
        }
    }

    private static string Require()
    {
        if (Soffice is null)
        {
            Assert.False(Environment.GetEnvironmentVariable("TABULAR_REQUIRE_SOFFICE") == "1", "LibreOffice is required here but soffice was not found.");
            Assert.Skip("LibreOffice is not installed.");
        }

        return Soffice;
    }

    /// <summary>Runs LibreOffice to its end, killed after two minutes, and returns what it wrote to its output.</summary>
    private static string Run(string soffice, IEnumerable<string> arguments)
    {
        ProcessStartInfo start = new(soffice)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        foreach (string argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using Process process = Process.Start(start)!;

        try
        {
            Task<string> standardOutput = process.StandardOutput.ReadToEndAsync();
            Task<string> standardError = process.StandardError.ReadToEndAsync();

            if (!process.WaitForExit(120_000))
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit();
                Assert.Fail("LibreOffice did not finish within two minutes and was killed.");
            }

            // The pipes close with the process; read after it exited so a full pipe cannot stall it.
            return standardOutput.GetAwaiter().GetResult() + standardError.GetAwaiter().GetResult();
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit();
            }
        }
    }

    private static void Cleanup(string folder)
    {
        try
        {
            Directory.Delete(folder, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temp folder must not mask the test's own outcome.
        }
        catch (UnauthorizedAccessException)
        {
            // Same.
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
