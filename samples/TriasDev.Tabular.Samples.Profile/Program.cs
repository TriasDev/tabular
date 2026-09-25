using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

using TriasDev.Tabular;

// Profiles a file and prints the headline of what analysis found, per column, as JSON.
//
//   dotnet run --project samples/TriasDev.Tabular.Samples.Profile -- samples/customers.csv
//
// The full FileProfile carries more — counts under every culture, samples, distinct values, every
// ranked reading — this prints what a mapping screen would show first.

string path = args.Length > 0 ? args[0] : Path.Combine("samples", "customers.csv");

using FileStream file = File.OpenRead(path);
using ITabularCursor cursor = TabularFile.Open(file, Path.GetFileName(path));

FileProfile profile = new TabularAnalyzer().Analyze(cursor);
SheetProfile sheet = profile.Sheets[0];

var summary = new
{
    format = profile.Format,
    delimiter = profile.Sheets[0].Dialect?.Delimiter.ToString(),
    encoding = profile.Sheets[0].Dialect?.Encoding.WebName,
    rows = sheet.RowCount,
    columns = sheet.Columns.Select(column =>
    {
        ColumnFacts facts = column.Facts;
        TypeHypothesis best = column.Hypotheses[0];

        return new
        {
            name = facts.Header,
            type = best.Type,
            // Named only where it decides the reading: 1001 or 2024-01-15 read the same under every
            // culture, 1.250,00 does not.
            culture = column.Hypotheses.Any(h => h.Type == best.Type && h.Culture != best.Culture && h.MatchedCount == best.MatchedCount)
                ? null
                : best.Culture,
            confidence = Math.Round(best.Confidence, 2),
            outliers = best.Outliers.Count == 0
                ? null
                : best.Outliers.Select(o => new { row = o.RowNumber, value = o.RawValue }),
            alsoFits = column.Hypotheses.Select(h => h.Type).Where(t => t != best.Type).Distinct().ToArray() is { Length: > 0 } others
                ? others
                : null,
            empty = facts.EmptyCount,
            distinct = facts.DistinctCount,
            unique = facts.IsUnique,
            min = (object?)facts.MinNumeric ?? facts.MinDate?.ToString("yyyy-MM-dd"),
            max = (object?)facts.MaxNumeric ?? facts.MaxDate?.ToString("yyyy-MM-dd"),
        };
    }),
};

JsonSerializerOptions options = new()
{
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
};

Console.WriteLine(JsonSerializer.Serialize(summary, options));
