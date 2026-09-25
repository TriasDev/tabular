using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;

using TriasDev.Tabular.Benchmarks.Shared;
using TriasDev.Tabular.Comparison.Readers;

namespace TriasDev.Tabular.Comparison;

/// <summary>
/// Reads the same large files with TriasDev.Tabular and with the libraries people would otherwise
/// reach for, on one machine, under one harness, and prints the results as Markdown.
/// </summary>
/// <remarks>
/// <para>
/// Every measurement runs in a process of its own, because peak memory only ever rises within a
/// process: measured together, each library would be credited with the peak of the greediest one
/// before it. Each is repeated and the median reported, so one noisy run does not decide a ranking.
/// </para>
/// <para>
/// Configured through the environment, like the benchmark project beside it:
/// <c>TABULAR_FIXTURES</c> (folder), <c>TABULAR_FILES</c> (comma-separated names in it),
/// <c>TABULAR_RUNS</c> (repetitions, default 3), <c>TABULAR_READERS</c> (optional comma-separated
/// library names to restrict to), <c>TABULAR_DELIMITER</c> (for the csv libraries that do not
/// detect one, default <c>;</c>) and <c>TABULAR_TIMEOUT</c> (seconds per run, default 900).
/// </para>
/// </remarks>
public static class Program
{
    private static readonly IReader[] Readers =
    [
        new TabularCsv(),
        new SylvanCsv(),
        new SepCsv(),
        new CsvHelperCsv(),
        new TabularXlsx(),
        new SylvanExcel(),
        new ExcelDataReaderXlsx(),
        new MiniExcelXlsx(),
        new OpenXmlSax(),
        new ClosedXmlXlsx(),
        new EpplusXlsx(),
        new NpoiXlsx(),
    ];

    public static int Main(string[] args)
    {
        if (args.Length == 4 && args[0] == "measure")
        {
            return Measure(args[1], args[2], args[3][0]);
        }

        return Drive();
    }

    private static string Key(IReader reader) => $"{reader.Name}|{reader.Kind}";

    /// <summary>Runs one reader over one file, in this process, and prints one result line.</summary>
    private static int Measure(string key, string path, char delimiter)
    {
        IReader reader = Readers.Single(r => Key(r) == key);

        Stopwatch clock = Stopwatch.StartNew();
        ReadCount count;

        try
        {
            count = reader.Read(path, delimiter);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"FAILED\t{ex.GetType().Name}: {ex.Message.ReplaceLineEndings(" ")}");
            return 0;
        }

        clock.Stop();

        Console.WriteLine(string.Join(
            '\t',
            "OK",
            clock.Elapsed.TotalMilliseconds.ToString("F0", CultureInfo.InvariantCulture),
            GC.GetTotalAllocatedBytes(precise: true).ToString(CultureInfo.InvariantCulture),
            PeakMemory.ResidentBytes().ToString(CultureInfo.InvariantCulture),
            count.Rows.ToString(CultureInfo.InvariantCulture),
            count.Values.ToString(CultureInfo.InvariantCulture)));

        return 0;
    }

    private static int Drive()
    {
        string? directory = Environment.GetEnvironmentVariable("TABULAR_FIXTURES");
        string[] files = List("TABULAR_FILES");

        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory) || files.Length == 0)
        {
            Console.WriteLine("Set TABULAR_FIXTURES to a folder and TABULAR_FILES to the files in it to compare.");
            return 0;
        }

        int runs = int.TryParse(Environment.GetEnvironmentVariable("TABULAR_RUNS"), out int r) && r > 0 ? r : 3;
        int timeout = int.TryParse(Environment.GetEnvironmentVariable("TABULAR_TIMEOUT"), out int t) && t > 0 ? t : 900;
        string delimiter = Environment.GetEnvironmentVariable("TABULAR_DELIMITER") is { Length: 1 } d ? d : ";";
        string[] only = List("TABULAR_READERS");

        PrintEnvironment(runs);

        foreach (string file in files)
        {
            CompareOn(Path.Combine(directory, file), only, delimiter, runs, timeout);
        }

        return 0;
    }

    private static void CompareOn(string path, string[] only, string delimiter, int runs, int timeout)
    {
        string file = Path.GetFileName(path);

        if (!File.Exists(path))
        {
            Console.WriteLine($"## {file}\n\nMissing.\n");
            return;
        }

        FileKind kind = Path.GetExtension(path).Equals(".xlsx", StringComparison.OrdinalIgnoreCase)
            ? FileKind.Xlsx
            : FileKind.Csv;

        List<Result> results =
        [
            .. Readers
                .Where(x => x.Kind == kind)
                .Where(x => only.Length == 0 || only.Contains(x.Name, StringComparer.OrdinalIgnoreCase))
                .Select(x => Repeat(x, path, delimiter, runs, timeout)),
        ];

        Print(file, new FileInfo(path).Length, results);
    }

    private static string[] List(string variable) =>
        (Environment.GetEnvironmentVariable(variable) ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private sealed record Result(IReader Reader, string? Failure, long Ms, long Allocated, long Peak, long Rows, long Values);

    private static Result Repeat(IReader reader, string path, string delimiter, int runs, int timeout)
    {
        List<string[]> ok = [];

        for (int i = 0; i < runs; i++)
        {
            string[] parts = RunChild(Key(reader), path, delimiter, timeout);

            if (parts[0] != "OK")
            {
                // A failure is a result, not noise: the same input fails the same way every time.
                return new Result(reader, parts.Length > 1 ? parts[1] : parts[0], 0, 0, 0, 0, 0);
            }

            ok.Add(parts);
        }

        long Median(int field) =>
            ok.Select(p => long.Parse(p[field], CultureInfo.InvariantCulture)).Order().ElementAt(ok.Count / 2);

        return new Result(reader, null, Median(1), Median(2), Median(3), Median(4), Median(5));
    }

    private static string[] RunChild(string key, string path, string delimiter, int timeoutSeconds)
    {
        ProcessStartInfo start = new()
        {
            FileName = Environment.ProcessPath ?? "dotnet",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        // The apphost when one was produced, the dotnet muxer when only a dll was.
        if (Path.GetFileNameWithoutExtension(start.FileName) == "dotnet")
        {
            start.ArgumentList.Add(Environment.GetCommandLineArgs()[0]);
        }

        foreach (string argument in (string[])["measure", key, path, delimiter])
        {
            start.ArgumentList.Add(argument);
        }

        using Process child = Process.Start(start)
            ?? throw new InvalidOperationException("Could not start the measurement process.");

        Task<string> output = child.StandardOutput.ReadToEndAsync();
        Task<string> errors = child.StandardError.ReadToEndAsync();

        if (!child.WaitForExit(TimeSpan.FromSeconds(timeoutSeconds)))
        {
            child.Kill(entireProcessTree: true);
            return ["FAILED", $"did not finish within {timeoutSeconds} s"];
        }

        string line = output.Result.Trim();

        if (line.Length == 0)
        {
            string reason = errors.Result.Trim().Split('\n').FirstOrDefault(l => l.Length > 0) ?? "no output";
            return ["FAILED", $"crashed (exit {child.ExitCode}): {reason}"];
        }

        return line.Split('\t');
    }

    private static void Print(string file, long size, List<Result> results)
    {
        long? reference = results.FirstOrDefault(x => x.Reader is TabularCsv or TabularXlsx && x.Failure is null)?.Rows;

        Console.WriteLine($"## {file} ({size / 1024d / 1024d:N1} MB)");
        Console.WriteLine();
        Console.WriteLine("| Library | Version | Time | Peak memory | Allocated | Rows | Values | Note |");
        Console.WriteLine("|---|---|--:|--:|--:|--:|--:|---|");

        foreach (Result x in results.OrderBy(x => x.Failure is null ? 0 : 1).ThenBy(x => x.Ms))
        {
            string version = Version(x.Reader.Anchor);

            if (x.Failure is not null)
            {
                Console.WriteLine($"| {x.Reader.Name} | {version} | — | — | — | — | — | failed: {Truncate(x.Failure, 110)} |");
                continue;
            }

            string note = reference is { } rows && x.Rows != rows
                ? $"read {x.Rows - rows:+#,0;-#,0} rows compared with TriasDev.Tabular"
                : string.Empty;

            Console.WriteLine(
                $"| {x.Reader.Name} | {version} | {x.Ms / 1000d:N2} s | {Megabytes(x.Peak)} | {Megabytes(x.Allocated)} "
                + $"| {x.Rows:N0} | {x.Values:N0} | {note} |");
        }

        Console.WriteLine();
    }

    private static void PrintEnvironment(int runs)
    {
        Console.WriteLine($"# Reader comparison, {DateTime.Now:yyyy-MM-dd}");
        Console.WriteLine();
        Console.WriteLine($"- Machine: {Processor()}, {Environment.ProcessorCount} logical cores, "
            + $"{GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / 1024d / 1024d / 1024d:N0} GB");
        Console.WriteLine($"- OS: {RuntimeInformation.OSDescription} ({RuntimeInformation.ProcessArchitecture})");
        Console.WriteLine($"- Runtime: {RuntimeInformation.FrameworkDescription}, "
            + $"{(System.Runtime.GCSettings.IsServerGC ? "server" : "workstation")} GC");
        Console.WriteLine($"- Each figure is the median of {runs} runs, each in a fresh process. Time includes "
            + "opening the file. Peak memory is the process's peak resident set; allocated is everything "
            + "the garbage collector handed out over the run.");
        Console.WriteLine();
    }

    private static string Processor()
    {
        try
        {
            if (OperatingSystem.IsMacOS())
            {
                using Process sysctl = Process.Start(new ProcessStartInfo("/usr/sbin/sysctl", "-n machdep.cpu.brand_string")
                {
                    RedirectStandardOutput = true,
                })!;

                return sysctl.StandardOutput.ReadToEnd().Trim();
            }

            if (OperatingSystem.IsLinux())
            {
                return File.ReadLines("/proc/cpuinfo")
                    .FirstOrDefault(l => l.StartsWith("model name", StringComparison.Ordinal))?
                    .Split(':', 2)[1].Trim() ?? "unknown CPU";
            }

            return Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER") ?? "unknown CPU";
        }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception)
        {
            return "unknown CPU";
        }
    }

    private static string Version(Type anchor)
    {
        string? informational = anchor.Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

        // Drop the source-revision suffix some packages append ("1.2.3+abc123").
        return informational?.Split('+')[0] ?? anchor.Assembly.GetName().Version?.ToString() ?? "?";
    }

    private static string Megabytes(long bytes) => $"{bytes / 1024d / 1024d:N0} MB";

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max] + "…";
}
