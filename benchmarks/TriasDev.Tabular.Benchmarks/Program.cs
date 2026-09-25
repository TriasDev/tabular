using System.Diagnostics;
using System.Globalization;

using TriasDev.Tabular.Benchmarks.Shared;
using TriasDev.Tabular.Csv;
using TriasDev.Tabular.Xlsx;
using TriasDev.Tabular.Tests.Spike;

namespace TriasDev.Tabular.Benchmarks;

/// <summary>
/// Measures how each surviving candidate reads the large fixtures.
/// </summary>
/// <remarks>
/// <para>
/// Each measurement runs in its own child process, and that is the whole point of the design.
/// <see cref="Process.PeakWorkingSet64"/> only ever rises, so running several candidates in one
/// process would credit every later candidate with the peak of the greediest earlier one. Peak
/// memory is the number that decides this comparison — a reader that consumes a gigabyte quickly but
/// holds four gigabytes while doing it is not usable — so it has to be measured honestly.
/// </para>
/// <para>
/// The rows are consumed and counted rather than collected. A list of five million rows would
/// measure the harness, and it would measure it identically for everyone.
/// </para>
/// </remarks>
public static class Program
{
    /// <summary>Names the folder holding the large fixtures, which are not in the repository.</summary>
    public const string FixtureDirectoryVariable = "TABULAR_FIXTURES";

    public static int Main(string[] args)
    {
        if (args.Length == 3 && args[0] == "measure")
        {
            return Measure(args[1], args[2]);
        }

        return Drive();
    }

    private static IReadOnlyList<IParserCandidate> Candidates =>
    [
        new BclXlsxCandidate(),
        new BclCsvCandidate(),
        new LibraryCsvCursor(),
        new LibraryXlsxCursor(),
        new LibraryXlsxCursorCellsOnly(),
        new FullAnalysis(TabularFormat.Xlsx),
        new FullAnalysis(TabularFormat.Csv),
    ];

    /// <summary>
    /// The whole analysis pass, not just the reading underneath it.
    /// </summary>
    /// <remarks>
    /// Measured separately because the two are not the same work and were being quoted as though
    /// they were. Reading a cell hands over a value; profiling one counts it, lengths it, hashes it
    /// for the distinct count and tries to parse it under every culture in the options. The reader's
    /// number is a floor for the analyzer's, never a substitute for it.
    /// </remarks>
    private sealed class FullAnalysis(TabularFormat format) : IParserCandidate
    {
        /// <summary>
        /// How many distinct values per column analysis keeps, so the cost of keeping them can be
        /// measured against not keeping them.
        /// </summary>
        /// <remarks>
        /// A knob rather than a second candidate: the two runs must differ in exactly one thing, and
        /// a separate candidate would also differ in which code path the driver took to reach it.
        /// </remarks>
        private static AnalysisOptions AnalysisOptions
        {
            get
            {
                string? retain = Environment.GetEnvironmentVariable("TABULAR_RETAIN");

                return retain is null || !int.TryParse(retain, out int cap)
                    ? AnalysisOptions.Default
                    : new AnalysisOptions { RetainedDistinctValues = cap };
            }
        }

        public string Name => $"Full analysis ({format.ToString().ToLowerInvariant()})";

        public CandidateFormats Formats =>
            format == TabularFormat.Xlsx ? CandidateFormats.Xlsx : CandidateFormats.Csv;

        public IEnumerable<IReadOnlyList<string?>> Rows(Stream stream)
        {
            using ITabularCursor cursor = format == TabularFormat.Xlsx
                ? new XlsxCursor(stream)
                : new CsvCursor(stream, "benchmark.csv");

            FileProfile profile = new TabularAnalyzer(AnalysisOptions).Analyze(cursor);

            // Reported through the row channel so the driver's counters mean something comparable:
            // one entry per data row, of the width the sheet had.
            foreach (SheetProfile sheet in profile.Sheets)
            {
                string?[] shape = new string?[sheet.Columns.Count];

                for (int i = 0; i < sheet.RowCount; i++)
                {
                    yield return shape;
                }
            }
        }
    }

    /// <summary>
    /// The xlsx cursor consuming cells without turning any of them into text.
    /// </summary>
    /// <remarks>
    /// Measured beside the text-producing reading to separate what the cursor itself costs from what
    /// the caller's demand for text costs. An analyzer does want text for most cells, so the other
    /// number is the honest one for that use; this one says how much of it the cursor could ever
    /// avoid.
    /// </remarks>
    private sealed class LibraryXlsxCursorCellsOnly : IParserCandidate
    {
        public string Name => "TriasDev.Tabular.XlsxCursor (cells only)";

        public CandidateFormats Formats => CandidateFormats.Xlsx;

        public IEnumerable<IReadOnlyList<string?>> Rows(Stream stream)
        {
            using XlsxCursor cursor = new(stream);

            string?[] shape = [];

            while (cursor.ReadRow())
            {
                int present = 0;

                for (int i = 0; i < cursor.CurrentRow.Length; i++)
                {
                    if (!cursor.CurrentRow[i].IsEmpty)
                    {
                        present++;
                    }
                }

                if (shape.Length != cursor.CurrentRow.Length)
                {
                    shape = new string?[cursor.CurrentRow.Length];
                }

                _ = present;
                yield return shape;
            }
        }
    }

    /// <summary>The library's own xlsx cursor, measured beside the prototype it grew out of.</summary>
    private sealed class LibraryXlsxCursor : IParserCandidate
    {
        public string Name => "TriasDev.Tabular.XlsxCursor";

        public CandidateFormats Formats => CandidateFormats.Xlsx;

        public IEnumerable<IReadOnlyList<string?>> Rows(Stream stream)
        {
            using XlsxCursor cursor = new(stream);

            List<string?> row = [];

            while (cursor.ReadRow())
            {
                row.Clear();

                for (int i = 0; i < cursor.CurrentRow.Length; i++)
                {
                    row.Add(cursor.CurrentRow[i].AsText());
                }

                yield return row;
            }
        }
    }

    /// <summary>
    /// The library's own csv cursor, measured beside the prototype it grew out of so the cost of the
    /// repairs it added is visible rather than assumed.
    /// </summary>
    private sealed class LibraryCsvCursor : IParserCandidate
    {
        public string Name => "TriasDev.Tabular.CsvCursor";

        /// <summary>Unterminated quotes the last run had to recover from.</summary>
        public static int Repairs { get; private set; }

        public CandidateFormats Formats => CandidateFormats.Csv;

        public IEnumerable<IReadOnlyList<string?>> Rows(Stream stream)
        {
            using CsvCursor cursor = new(stream, "benchmark.csv");

            List<string?> row = [];

            while (cursor.ReadRow())
            {
                row.Clear();

                for (int i = 0; i < cursor.CurrentRow.Length; i++)
                {
                    row.Add(cursor.CurrentRow[i].AsText());
                }

                yield return row;
            }

            Repairs = cursor.Diagnostics.RecoveredUnterminatedQuotes;
        }
    }

    /// <summary>Runs one candidate over one file and prints a single result line.</summary>
    private static int Measure(string candidateName, string path)
    {
        IParserCandidate? candidate = Candidates.FirstOrDefault(
            c => string.Equals(c.Name, candidateName, StringComparison.Ordinal));

        if (candidate is null)
        {
            Console.Error.WriteLine($"Unknown candidate {candidateName}.");
            return 1;
        }

        long rows = 0;
        long cells = 0;
        long nonEmpty = 0;

        Stopwatch clock = Stopwatch.StartNew();

        try
        {
            using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 64 * 1024);

            foreach (IReadOnlyList<string?> row in candidate.Rows(stream))
            {
                rows++;
                cells += row.Count;

                for (int i = 0; i < row.Count; i++)
                {
                    if (row[i] is not null)
                    {
                        nonEmpty++;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            clock.Stop();
            Console.WriteLine($"FAILED\t{ex.GetType().Name}\t{ex.Message.ReplaceLineEndings(" ")}");
            return 0;
        }

        clock.Stop();

        Console.WriteLine(string.Join(
            '\t',
            "OK",
            LibraryCsvCursor.Repairs.ToString(CultureInfo.InvariantCulture),
            clock.Elapsed.TotalMilliseconds.ToString("F0", CultureInfo.InvariantCulture),
            GC.GetTotalAllocatedBytes(precise: true).ToString(CultureInfo.InvariantCulture),
            PeakMemory.ResidentBytes().ToString(CultureInfo.InvariantCulture),
            rows.ToString(CultureInfo.InvariantCulture),
            cells.ToString(CultureInfo.InvariantCulture),
            nonEmpty.ToString(CultureInfo.InvariantCulture),
            GC.CollectionCount(0).ToString(CultureInfo.InvariantCulture),
            GC.CollectionCount(2).ToString(CultureInfo.InvariantCulture)));

        return 0;
    }

    private static string Extension(string path) =>
        Path.GetExtension(path).TrimStart('.').ToLowerInvariant();

    private static int Drive()
    {
        string? directory = Environment.GetEnvironmentVariable(FixtureDirectoryVariable);

        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            Console.WriteLine(
                $"Set {FixtureDirectoryVariable} to the folder holding the large fixtures before running the "
                + "benchmarks. Nothing was measured.");
            return 0;
        }

        string[] files = (Environment.GetEnvironmentVariable("TABULAR_FILES") ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (files.Length == 0)
        {
            Console.WriteLine("Set TABULAR_FILES to a comma-separated list of file names to measure.");
            return 0;
        }

        // Bytes per cell is the column that matters over time. A total says how big the file was;
        // per cell says how the reader behaves, and it is comparable between fixtures. ADR-0001 sets
        // the budget at 60 bytes per cell, against roughly 670 for the prototype it was written from.
        Console.WriteLine("| file | reader | ms | allocated | B/cell | peak RSS | rows | cells | repaired |");
        Console.WriteLine("|---|---|--:|--:|--:|--:|--:|--:|--:|");

        foreach (string file in files)
        {
            string path = Path.Combine(directory, file);

            if (!File.Exists(path))
            {
                Console.WriteLine($"| {file} | — | missing | | | | | | |");
                continue;
            }

            CandidateFormats format = Extension(path) == "xlsx" ? CandidateFormats.Xlsx : CandidateFormats.Csv;

            foreach (IParserCandidate candidate in Candidates.Where(c => c.Formats.HasFlag(format)))
            {
                RunChild(file, path, candidate.Name);
            }
        }

        return 0;
    }

    private static void RunChild(string file, string path, string candidateName)
    {
        ProcessStartInfo start = new()
        {
            FileName = Environment.ProcessPath ?? "dotnet",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        // Environment.ProcessPath is the apphost when one was produced and the dotnet muxer when the
        // build only produced a dll; the argument list has to match whichever it is.
        if (Path.GetFileNameWithoutExtension(start.FileName) == "dotnet")
        {
            start.ArgumentList.Add(Environment.GetCommandLineArgs()[0]);
        }

        start.ArgumentList.Add("measure");
        start.ArgumentList.Add(candidateName);
        start.ArgumentList.Add(path);

        using Process child = Process.Start(start)
            ?? throw new InvalidOperationException("Could not start the measurement process.");

        string output = child.StandardOutput.ReadToEnd().Trim();
        child.WaitForExit();

        string[] parts = output.Split('\t');

        if (parts.Length < 2 || parts[0] != "OK")
        {
            string reason = parts.Length > 1 ? string.Join(' ', parts.Skip(1)) : "no output";
            Console.WriteLine($"| {file} | {candidateName} | failed | {Truncate(reason, 90)} | | | | | |");
            return;
        }

        // parts: OK, repairs, ms, allocated, peak, rows, cells, nonEmpty, gen0, gen2
        long allocated = long.Parse(parts[3], CultureInfo.InvariantCulture);
        long cells = long.Parse(parts[6], CultureInfo.InvariantCulture);

        Console.WriteLine(
            $"| {file} | {candidateName} | {long.Parse(parts[2], CultureInfo.InvariantCulture):N0} "
            + $"| {Bytes(parts[3])} | {(cells == 0 ? "—" : (allocated / (double)cells).ToString("N0", CultureInfo.InvariantCulture))} "
            + $"| {Bytes(parts[4])} "
            + $"| {long.Parse(parts[5], CultureInfo.InvariantCulture):N0} "
            + $"| {cells:N0} "
            + $"| {long.Parse(parts[1], CultureInfo.InvariantCulture):N0} |");
    }

    private static string Bytes(string raw) =>
        $"{long.Parse(raw, CultureInfo.InvariantCulture) / 1024d / 1024d:N0} MB";

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max] + "…";
}
