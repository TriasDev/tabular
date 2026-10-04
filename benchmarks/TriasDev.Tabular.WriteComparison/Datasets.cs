using System.Globalization;

namespace TriasDev.Tabular.WriteComparison;

internal enum ValueKind
{
    Empty,
    Text,
    Long,
    Double,
    Decimal,

    /// <summary>A date without a time of day.</summary>
    Date,
    DateTime,
    Boolean,
}

/// <summary>One column of a dataset: its header and the kind of every value in it (a value may also be empty).</summary>
internal readonly record struct DataColumn(string Header, ValueKind Kind);

/// <summary>One cell, as a struct a writer can read without boxing; only the field of <see cref="Kind"/> counts.</summary>
internal readonly struct Datum
{
    public ValueKind Kind { get; init; }

    public string? Text { get; init; }

    public long Long { get; init; }

    public double Double { get; init; }

    public decimal Decimal { get; init; }

    public DateTime Date { get; init; }

    public bool Boolean { get; init; }
}

/// <summary>
/// Deterministic data, generated per row from the row's index: nothing is stored beyond what a caller
/// holds, and every writer gets the same values.
/// </summary>
internal interface IDataset
{
    string Name { get; }

    DataColumn[] Columns { get; }

    /// <summary>Fills <paramref name="cells"/> (one per column) with the values of data row <paramref name="row"/>, counted from 0.</summary>
    void Fill(long row, Datum[] cells);
}

/// <summary>Thirty columns of the mix a business table has: text that needs quoting, doubles, decimals, dates, flags.</summary>
internal sealed class NarrowDataset : IDataset
{
    public const int TextColumns = 5;
    public const int DoubleColumns = 10;
    public const int DecimalColumns = 5;

    public static readonly NarrowDataset Instance = new();

    private static readonly string[] Cities = [.. Enumerable.Range(0, 200).Select(i => $"City {i:D3}")];
    private static readonly string[] Countries = ["Germany", "Austria", "France", "Spain", "Italy", "Poland", "Norway", "Chile", "Japan", "Peru", "Korea, Republic of", "Côte d'Ivoire \"CI\""];
    private static readonly DateTime DateOrigin = new(2000, 1, 1, 0, 0, 0, DateTimeKind.Unspecified);
    private static readonly DateTime StampOrigin = new(2020, 1, 1, 0, 0, 0, DateTimeKind.Unspecified);

    private static readonly DataColumn[] All = BuildColumns();

    private NarrowDataset()
    {
    }

    public string Name => "narrow";

    public DataColumn[] Columns => All;

    private static DataColumn[] BuildColumns() =>
    [
        new("Id", ValueKind.Long),
        new("Name", ValueKind.Text),
        new("Street", ValueKind.Text),
        new("City", ValueKind.Text),
        new("Country", ValueKind.Text),
        new("Code", ValueKind.Text),
        .. Enumerable.Range(1, DoubleColumns).Select(i => new DataColumn($"Value{i}", ValueKind.Double)),
        .. Enumerable.Range(1, DecimalColumns).Select(i => new DataColumn($"Amount{i}", ValueKind.Decimal)),
        new("Quantity", ValueKind.Long),
        new("Start", ValueKind.Date),
        new("End", ValueKind.Date),
        new("Created", ValueKind.DateTime),
        new("Updated", ValueKind.DateTime),
        .. Enumerable.Range(1, 4).Select(i => new DataColumn($"Active{i}", ValueKind.Boolean)),
    ];

    /// <summary>The double of column <paramref name="k"/> (0-based among the doubles) in <paramref name="row"/>: 0–100, twelve significant digits at most; one in ten empty.</summary>
    public static double? Double(long row, int k) =>
        (row + (k * 3)) % 10 == 0 ? null : ((row * 7_919_000_017L) + (k * 104_729_000_003L)) % 1_000_000_000_000L / 1e10;

    public static decimal Decimal(long row, int k) => new((int)(((row * 31) + (k * 17L)) % 2_000_000_000L), 0, 0, false, 2);

    public void Fill(long row, Datum[] cells)
    {
        string text = row.ToString(CultureInfo.InvariantCulture);
        int c = 0;

        cells[c++] = new Datum { Kind = ValueKind.Long, Long = row };

        // A comma in one row of seven, a quote in one of eleven: the quoting paths are exercised, not dominant.
        cells[c++] = new Datum { Kind = ValueKind.Text, Text = row % 7 == 3 ? $"Smith, Jane {text}" : $"Person {text}" };
        cells[c++] = new Datum { Kind = ValueKind.Text, Text = row % 11 == 5 ? $"\"Old\" Mill Road {text}" : $"{text} Main Street" };
        cells[c++] = new Datum { Kind = ValueKind.Text, Text = Cities[row % Cities.Length] };
        cells[c++] = new Datum { Kind = ValueKind.Text, Text = Countries[row % Countries.Length] };
        cells[c++] = new Datum { Kind = ValueKind.Text, Text = $"C-{row % 100_000:D5}" };

        for (int k = 0; k < DoubleColumns; k++)
        {
            cells[c++] = Double(row, k) is { } d
                ? new Datum { Kind = ValueKind.Double, Double = d }
                : default;
        }

        for (int k = 0; k < DecimalColumns; k++)
        {
            cells[c++] = new Datum { Kind = ValueKind.Decimal, Decimal = Decimal(row, k) };
        }

        cells[c++] = new Datum { Kind = ValueKind.Long, Long = (row * 13) % 1000 };

        DateTime start = DateOrigin.AddDays((row * 3) % 9000);
        cells[c++] = new Datum { Kind = ValueKind.Date, Date = start };
        cells[c++] = new Datum { Kind = ValueKind.Date, Date = start.AddDays(30) };
        cells[c++] = new Datum { Kind = ValueKind.DateTime, Date = StampOrigin.AddSeconds((row * 977) % 100_000_000) };
        cells[c++] = new Datum { Kind = ValueKind.DateTime, Date = StampOrigin.AddSeconds(((row * 977) % 100_000_000) + 3600) };

        for (int k = 0; k < 4; k++)
        {
            cells[c++] = new Datum { Kind = ValueKind.Boolean, Boolean = (row + k) % 3 == 0 };
        }
    }
}

/// <summary>Three fixed columns and five thousand measured ones, as the library's wide-export test generates them.</summary>
internal sealed class WideDataset : IDataset
{
    public const int Measured = 5_000;

    public static readonly WideDataset Instance = new();

    private static readonly DateTime Origin = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Unspecified);

    private static readonly DataColumn[] All =
    [
        new("Id", ValueKind.Long),
        new("Name", ValueKind.Text),
        new("Start", ValueKind.Date),
        .. Enumerable.Range(1, Measured).Select(i => new DataColumn($"Service__Field{i}", ValueKind.Double)),
    ];

    private WideDataset()
    {
    }

    public string Name => "wide";

    public DataColumn[] Columns => All;

    /// <summary>Measured column <paramref name="column"/> (0-based) in <paramref name="row"/>: a tenth in 0–99.9, one in ten empty.</summary>
    public static double? Value(long row, int column) =>
        (row + column) % 10 == 0 ? null : ((row * 7) + (column * 13L)) % 1000 / 10.0;

    public static string NameOf(long row) => $"Location {row.ToString(CultureInfo.InvariantCulture)}";

    public static DateTime Start(long row) => Origin.AddDays(row % 365);

    public void Fill(long row, Datum[] cells)
    {
        cells[0] = new Datum { Kind = ValueKind.Long, Long = row };
        cells[1] = new Datum { Kind = ValueKind.Text, Text = NameOf(row) };
        cells[2] = new Datum { Kind = ValueKind.Date, Date = Start(row) };

        for (int k = 0; k < Measured; k++)
        {
            cells[k + 3] = Value(row, k) is { } d
                ? new Datum { Kind = ValueKind.Double, Double = d }
                : default;
        }
    }
}

/// <summary>What the styled scenarios apply, in one place so every writer is asked for the same look.</summary>
internal static class Styles
{
    public const int HeaderFill = 0xD9E1F2;
    public static readonly int[] LegendFills = [0x63BE7B, 0xFFEB84, 0xF8696B];
    public const string DateFormat = "dd/mm/yyyy";
    public const string DateTimeFormat = "dd/mm/yyyy hh:mm:ss";
    public const string DecimalFormat = "#,##0.00";

    /// <summary>The legend colour of a double: below 33, below 66, above.</summary>
    public static int Legend(double value)
    {
        if (value < 33)
        {
            return 0;
        }

        return value < 66 ? 1 : 2;
    }
}

/// <summary>A scenario: a dataset, a number of rows, a kind of file, and whether it is styled.</summary>
internal sealed record Scenario(string Name, IDataset Dataset, long FullRows, FileKind Kind, bool Styled, bool TabularOnly)
{
    /// <summary>Rows written: the full count, or a fraction of it when <c>TABULAR_ROWS_SCALE</c> says so (for trying the harness).</summary>
    public long Rows { get; } = Scaled(FullRows);

    public static IReadOnlyList<Scenario> All { get; } =
    [
        new("csv-5m", NarrowDataset.Instance, 5_000_000, FileKind.Csv, false, false),
        new("zip-5m", NarrowDataset.Instance, 5_000_000, FileKind.Zip, false, true),
        new("xlsx-1m", NarrowDataset.Instance, 1_048_575, FileKind.Xlsx, false, false),
        new("xlsx-1m-styled", NarrowDataset.Instance, 1_048_575, FileKind.Xlsx, true, false),
        new("ods-1m", NarrowDataset.Instance, 1_048_575, FileKind.Ods, false, true),
        new("ods-1m-styled", NarrowDataset.Instance, 1_048_575, FileKind.Ods, true, true),
        new("wide-xlsx", WideDataset.Instance, 10_000, FileKind.Xlsx, true, false),
        new("wide-csv", WideDataset.Instance, 10_000, FileKind.Csv, false, false),
        new("wide-ods", WideDataset.Instance, 10_000, FileKind.Ods, false, true),
    ];

    public string Extension => Kind switch
    {
        FileKind.Csv => ".csv",
        FileKind.Xlsx => ".xlsx",
        FileKind.Ods => ".ods",
        _ => ".zip",
    };

    private static long Scaled(long rows) =>
        double.TryParse(Environment.GetEnvironmentVariable("TABULAR_ROWS_SCALE"), NumberStyles.Float, CultureInfo.InvariantCulture, out double scale) && scale > 0 && scale < 1
            ? Math.Max(10, (long)(rows * scale))
            : rows;
}
