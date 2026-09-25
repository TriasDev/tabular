using System.Globalization;
using System.Text;

using TriasDev.Tabular;
using TriasDev.Tabular.Samples.Progress;

// Analyses a large file with a progress bar, and stops cleanly on Ctrl+C.
//
//   dotnet run -c Release --project samples/TriasDev.Tabular.Samples.Progress -- [path]
//
// Without a path it writes a synthetic csv of half a million rows to the temp folder first.

string path = args.Length > 0 ? args[0] : WriteSampleFile(500_000);

using CancellationTokenSource cancel = new();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;        // let the analysis stop itself rather than killing the process
    cancel.Cancel();
};

using FileStream file = File.OpenRead(path);
using ITabularCursor cursor = TabularFile.Open(file, Path.GetFileName(path), cancellationToken: cancel.Token);

try
{
    FileProfile profile = new TabularAnalyzer().Analyze(cursor, new ConsoleProgressBar(), cancel.Token);

    Console.WriteLine();
    Console.WriteLine($"{profile.Sheets.Sum(s => s.RowCount):N0} rows, {profile.Sheets[0].Columns.Count} columns");
}
catch (OperationCanceledException)
{
    Console.WriteLine();
    Console.WriteLine("cancelled");
}

static string WriteSampleFile(int rows)
{
    string path = Path.Combine(Path.GetTempPath(), "tabular-progress-sample.csv");

    using StreamWriter writer = new(path, append: false, new UTF8Encoding(false));
    writer.WriteLine("id;name;amount;signed_on");

    for (int i = 1; i <= rows; i++)
    {
        writer.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"{i};customer {i % 997};{i % 10_000},{i % 100:00};2024-{(i % 12) + 1:00}-{(i % 28) + 1:00}"));
    }

    return path;
}
