using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;

using TriasDev.Tabular.Benchmarks.Shared;
using TriasDev.Tabular.WriteComparison.Writers;

namespace TriasDev.Tabular.WriteComparison;

/// <summary>
/// Writes the same data with TriasDev.Tabular and with the libraries people would otherwise reach for,
/// on one machine, under one harness, and prints the results as Markdown.
/// </summary>
/// <remarks>
/// <para>
/// Every measurement runs in a process of its own, because peak memory only ever rises within a
/// process, and each is repeated and the median reported. After the timed part the process re-reads
/// the file with <see cref="TabularFile"/> and checks it against the dataset; a file that does not read
/// back is reported as failed, not timed.
/// </para>
/// <para>
/// Configured through the environment: <c>TABULAR_RUNS</c> (repetitions, default 3),
/// <c>TABULAR_WRITERS</c> and <c>TABULAR_SCENARIOS</c> (comma-separated names to restrict to),
/// <c>TABULAR_OUT</c> (folder for the files; default a temporary one; each file is deleted after its
/// measurement), <c>TABULAR_TIMEOUT</c> (seconds per run, default 900) and <c>TABULAR_ROWS_SCALE</c>
/// (a fraction below 1 of every scenario's rows, for trying the harness).
/// </para>
/// </remarks>
public static class Program
{
    private static readonly IWriter[] Writers =
    [
        new TabularCsvWriter(),
        new TabularXlsxWriter(),
        new TabularOdsWriter(),
        new TabularZipWriter(),
        new CsvHelperWriter(),
        new SepCsvWriter(),
        new SylvanCsvWriter(),
        new LargeXlsxWriter(),
        new SpreadCheetahWriter(),
        new MiniExcelWriter(),
    ];

    public static int Main(string[] args)
    {
        if (args.Length == 4 && args[0] == "measure")
        {
            return Measure(args[1], args[2], args[3]);
        }

        if (args.Length == 3 && args[0] == "generate")
        {
            return Generate(args[1], args[2]);
        }

        if (args.Length == 3 && args[0] == "verify")
        {
            // Checks a file already on disk against a scenario's data: for trying the verification itself.
            Console.WriteLine(Verifier.Check(Scenario.All.Single(s => s.Name == args[1]), args[2]) ?? "OK");
            return 0;
        }

        return Drive();
    }

    private static string Key(IWriter writer) => $"{writer.Name}|{writer.Kind}";

    /// <summary>Runs one writer over one scenario, in this process, and prints one result line.</summary>
    private static int Measure(string scenarioName, string key, string path)
    {
        Scenario scenario = Scenario.All.Single(s => s.Name == scenarioName);
        IWriter writer = Writers.Single(w => Key(w) == key);
        long bytes;
        long milliseconds;
        long peak;
        long allocated;

        try
        {
            Stopwatch clock = Stopwatch.StartNew();

            using (FileStream target = new(path, FileMode.Create, FileAccess.ReadWrite, FileShare.None, 64 * 1024))
            {
                writer.Write(scenario, target);
            }

            clock.Stop();
            milliseconds = (long)clock.Elapsed.TotalMilliseconds;

            // Taken before the verification, which reads the file and would raise both.
            peak = PeakMemory.ResidentBytes();
            allocated = GC.GetTotalAllocatedBytes(precise: true);
            bytes = new FileInfo(path).Length;

            if (Verifier.Check(scenario, path) is { } problem)
            {
                Console.WriteLine($"FAILED\tdid not read back: {problem.ReplaceLineEndings(" ")}");
                return 0;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"FAILED\t{ex.GetType().Name}: {ex.Message.ReplaceLineEndings(" ")}");
            return 0;
        }

        Console.WriteLine(string.Join(
            '\t',
            "OK",
            milliseconds.ToString(CultureInfo.InvariantCulture),
            peak.ToString(CultureInfo.InvariantCulture),
            allocated.ToString(CultureInfo.InvariantCulture),
            bytes.ToString(CultureInfo.InvariantCulture),
            scenario.Rows.ToString(CultureInfo.InvariantCulture)));

        return 0;
    }

    /// <summary>Generates one scenario's data and writes nothing, in this process, and prints one result line.</summary>
    private static int Generate(string scenarioName, string mode)
    {
        Scenario scenario = Scenario.All.Single(s => s.Name == scenarioName);
        Stopwatch clock = Stopwatch.StartNew();
        long values = mode == "columns" ? Generation.ByColumn(scenario, TabularWriters.WideChunkRows) : Generation.ByRow(scenario);
        clock.Stop();

        Console.WriteLine(string.Join(
            '\t',
            "OK",
            ((long)clock.Elapsed.TotalMilliseconds).ToString(CultureInfo.InvariantCulture),
            PeakMemory.ResidentBytes().ToString(CultureInfo.InvariantCulture),
            GC.GetTotalAllocatedBytes(precise: true).ToString(CultureInfo.InvariantCulture),
            values.ToString(CultureInfo.InvariantCulture),
            scenario.Rows.ToString(CultureInfo.InvariantCulture)));

        return 0;
    }

    private static int Drive()
    {
        int runs = int.TryParse(Environment.GetEnvironmentVariable("TABULAR_RUNS"), out int r) && r > 0 ? r : 3;
        int timeout = int.TryParse(Environment.GetEnvironmentVariable("TABULAR_TIMEOUT"), out int t) && t > 0 ? t : 900;
        string[] onlyWriters = List("TABULAR_WRITERS");
        string[] onlyScenarios = List("TABULAR_SCENARIOS");
        string folder = Environment.GetEnvironmentVariable("TABULAR_OUT") is { Length: > 0 } given
            ? given
            : Path.Combine(Path.GetTempPath(), "tabular-write-comparison");

        Directory.CreateDirectory(folder);
        PrintEnvironment(runs);

        foreach (Scenario scenario in Scenario.All.Where(s => onlyScenarios.Length == 0 || onlyScenarios.Contains(s.Name, StringComparer.OrdinalIgnoreCase)))
        {
            List<IWriter> candidates =
            [
                .. Writers
                    .Where(w => w.Kind == scenario.Kind)
                    .Where(w => !scenario.TabularOnly || w.Name == "TriasDev.Tabular")
                    .Where(w => onlyWriters.Length == 0 || onlyWriters.Contains(w.Name, StringComparer.OrdinalIgnoreCase)),
            ];

            List<Result> results = [];

            foreach (IWriter writer in candidates.Where(w => !scenario.Styled || w.Styled))
            {
                results.Add(Repeat(scenario, writer, folder, runs, timeout));
            }

            Print(scenario, results, [.. candidates.Where(w => scenario.Styled && !w.Styled)], Baselines(scenario, runs, timeout));
        }

        return 0;
    }

    private static string[] List(string variable) =>
        (Environment.GetEnvironmentVariable(variable) ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private sealed record Result(IWriter? Writer, string? Failure, long Ms, long Peak, long Allocated, long Bytes, long Rows);

    /// <summary>The data generation alone, as each way of consuming it pays for it: by row (every row writer) and, for wide data, by typed column.</summary>
    private static List<(string Label, Result Result)> Baselines(Scenario scenario, int runs, int timeout)
    {
        List<(string, Result)> baselines =
        [
            ("row by row, one cell struct per value (what a row-oriented writer consumes)", RepeatChild(null, ["generate", scenario.Name, "rows"], runs, timeout)),
        ];

        if (scenario.Dataset is WideDataset)
        {
            baselines.Add(("typed column arrays in batches of 500 rows (what `ColumnBatch` consumes)", RepeatChild(null, ["generate", scenario.Name, "columns"], runs, timeout)));
        }

        return baselines;
    }

    private static Result Repeat(Scenario scenario, IWriter writer, string folder, int runs, int timeout)
    {
        string path = Path.Combine(folder, $"{scenario.Name}-{Guid.NewGuid():N}{scenario.Extension}");

        try
        {
            return RepeatChild(writer, ["measure", scenario.Name, Key(writer), path], runs, timeout);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static Result RepeatChild(IWriter? writer, string[] arguments, int runs, int timeout)
    {
        List<string[]> ok = [];

        for (int i = 0; i < runs; i++)
        {
            string[] parts = RunChild(arguments, timeout);

            if (parts[0] != "OK")
            {
                // A failure is a result, not noise: the same input fails the same way every time.
                return new Result(writer, parts.Length > 1 ? parts[1] : parts[0], 0, 0, 0, 0, 0);
            }

            ok.Add(parts);
        }

        long Median(int field) =>
            ok.Select(p => long.Parse(p[field], CultureInfo.InvariantCulture)).Order().ElementAt(ok.Count / 2);

        return new Result(writer, null, Median(1), Median(2), Median(3), Median(4), Median(5));
    }

    private static string[] RunChild(string[] arguments, int timeoutSeconds)
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

        foreach (string argument in arguments)
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

    private static void Print(Scenario scenario, List<Result> results, List<IWriter> left, List<(string Label, Result Result)> baselines)
    {
        Console.WriteLine($"## {scenario.Name}: {scenario.Dataset.Name}, {scenario.Rows:N0} rows x {scenario.Dataset.Columns.Length:N0} columns, {scenario.Kind.ToString().ToLowerInvariant()}{(scenario.Styled ? ", styled" : string.Empty)}");
        Console.WriteLine();
        Console.WriteLine("| Library | Version | Time | Peak memory | Allocated | Size | Note |");
        Console.WriteLine("|---|---|--:|--:|--:|--:|---|");

        foreach (Result x in results.OrderBy(x => x.Failure is null ? 0 : 1).ThenBy(x => x.Ms))
        {
            string version = Version(x.Writer!.Anchor);

            if (x.Failure is not null)
            {
                Console.WriteLine($"| {x.Writer.Name} | {version} | — | — | — | — | failed: {Truncate(x.Failure, 160)} |");
                continue;
            }

            Console.WriteLine(
                $"| {x.Writer.Name} | {version} | {x.Ms / 1000d:N2} s | {Megabytes(x.Peak)} | {Megabytes(x.Allocated)} | {x.Bytes / 1024d / 1024d:N1} MB | |");
        }

        Console.WriteLine();

        foreach ((string label, Result generation) in baselines)
        {
            Console.WriteLine(generation.Failure is null
                ? $"Generating the data alone, {label}: {generation.Ms / 1000d:N2} s, {Megabytes(generation.Allocated)} allocated. Every library's time includes the matching share."
                : $"Generating the data alone, {label}: failed ({generation.Failure}).");
        }

        Console.WriteLine();

        if (left.Count > 0)
        {
            Console.WriteLine($"Not run, because they cannot apply the styles: {string.Join(", ", left.Select(w => w.Name))}.");
            Console.WriteLine();
        }
    }

    private static void PrintEnvironment(int runs)
    {
        Console.WriteLine($"# Writer comparison, {DateTime.Now:yyyy-MM-dd}");
        Console.WriteLine();
        Console.WriteLine($"- Machine: {Processor()}, {Environment.ProcessorCount} logical cores, "
            + $"{GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / 1024d / 1024d / 1024d:N0} GB");
        Console.WriteLine($"- OS: {RuntimeInformation.OSDescription} ({RuntimeInformation.ProcessArchitecture})");
        Console.WriteLine($"- Runtime: {RuntimeInformation.FrameworkDescription}, "
            + $"{(System.Runtime.GCSettings.IsServerGC ? "server" : "workstation")} GC");
        Console.WriteLine($"- Each figure is the median of {runs} runs, each in a fresh process. Time covers creating the "
            + "file, writing every row and closing it; the data is generated inside the timed part, from the same generators for every library, so each scenario also reports what generating it alone costs, in the form a row-oriented writer consumes it (one cell struct per value) and, for wide data, as typed column arrays; a library's time includes the share that matches how it takes its data. "
            + "Peak memory is the process's peak resident set; allocated is everything the garbage collector handed out over the run. "
            + "Every file is read back with TriasDev.Tabular after the timed part and checked against the data.");
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
