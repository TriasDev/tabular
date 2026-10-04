using System.Globalization;
using System.Text;

using CsvHelper;
using CsvHelper.Configuration;

using nietras.SeparatedValues;

using Sylvan.Data.Csv;

namespace TriasDev.Tabular.WriteComparison.Writers;

/// <summary>What the csv writers share: a UTF-8 text writer without a byte order mark, over the target, with a 64 KB buffer.</summary>
internal abstract class CsvWriterBase : IWriter
{
    private protected static readonly UTF8Encoding Utf8 = new(false);

    public abstract string Name { get; }

    public abstract Type Anchor { get; }

    public FileKind Kind => FileKind.Csv;

    /// <summary>A csv has no styles: every csv writer is as styled as the scenario asks.</summary>
    public bool Styled => true;

    public abstract void Write(Scenario scenario, Stream target);
}

/// <summary>
/// CsvHelper's <c>CsvWriter</c> writing field by field with <c>WriteField</c> and <c>NextRecord</c> (no record
/// mapping, which is its documented way to write without a class), typed values through its converters, invariant culture.
/// </summary>
internal sealed class CsvHelperWriter : CsvWriterBase
{
    public override string Name => "CsvHelper";

    public override Type Anchor => typeof(CsvWriter);

    public override void Write(Scenario scenario, Stream target)
    {
        using StreamWriter text = new(target, Utf8, 64 * 1024, leaveOpen: true);
        using CsvWriter csv = new(text, new CsvConfiguration(CultureInfo.InvariantCulture) { NewLine = "\r\n" });

        // Dates as ISO text, once, in the converters' options: no per-cell format call.
        csv.Context.TypeConverterOptionsCache.GetOptions<DateOnly>().Formats = ["yyyy-MM-dd"];
        csv.Context.TypeConverterOptionsCache.GetOptions<DateTime>().Formats = ["yyyy-MM-dd HH:mm:ss"];

        IDataset dataset = scenario.Dataset;
        DataColumn[] columns = dataset.Columns;

        foreach (DataColumn column in columns)
        {
            csv.WriteField(column.Header);
        }

        csv.NextRecord();

        Datum[] cells = new Datum[columns.Length];

        for (long row = 0; row < scenario.Rows; row++)
        {
            dataset.Fill(row, cells);

            for (int c = 0; c < cells.Length; c++)
            {
                WriteCell(csv, in cells[c]);
            }

            csv.NextRecord();
        }

        csv.Flush();
    }

    private static void WriteCell(CsvWriter csv, in Datum cell)
    {
        switch (cell.Kind)
        {
            case ValueKind.Text:
                csv.WriteField(cell.Text);
                break;
            case ValueKind.Long:
                csv.WriteField(cell.Long);
                break;
            case ValueKind.Double:
                csv.WriteField(cell.Double);
                break;
            case ValueKind.Decimal:
                csv.WriteField(cell.Decimal);
                break;
            case ValueKind.Date:
                csv.WriteField(DateOnly.FromDateTime(cell.Date));
                break;
            case ValueKind.DateTime:
                csv.WriteField(cell.Date);
                break;
            case ValueKind.Boolean:
                csv.WriteField(cell.Boolean);
                break;
            default:
                csv.WriteField(string.Empty);
                break;
        }
    }
}

/// <summary>
/// Sep's <c>SepWriter</c> (its row terminator is not configurable: it writes <c>Environment.NewLine</c>, which is LF on macOS and Linux and CR LF on Windows): one row at a time with <c>NewRow</c>, columns by index (the header declared up
/// front, so no name lookup per cell), values formatted straight into its buffer with <c>Format</c> and interpolated
/// <c>Set</c> (no strings), escaping switched on because the data needs it.
/// </summary>
internal sealed class SepCsvWriter : CsvWriterBase
{
    public override string Name => "Sep";

    public override Type Anchor => typeof(Sep);

    public override void Write(Scenario scenario, Stream target)
    {
        using SepWriter writer = Sep
            .Writer(o => o with
            {
                Sep = Sep.New(','),
                Escape = true,
                CultureInfo = CultureInfo.InvariantCulture,
            })
            .To(target, leaveOpen: true);

        IDataset dataset = scenario.Dataset;
        DataColumn[] columns = dataset.Columns;
        writer.Header.Add([.. columns.Select(c => c.Header)]);
        Datum[] cells = new Datum[columns.Length];

        for (long row = 0; row < scenario.Rows; row++)
        {
            dataset.Fill(row, cells);
            using SepWriter.Row line = writer.NewRow();

            for (int c = 0; c < cells.Length; c++)
            {
                WriteCell(line[c], in cells[c]);
            }
        }
    }

    private static void WriteCell(SepWriter.Col col, in Datum cell)
    {
        switch (cell.Kind)
        {
            case ValueKind.Text:
                col.Set(cell.Text);
                break;
            case ValueKind.Long:
                col.Format(cell.Long);
                break;
            case ValueKind.Double:
                col.Format(cell.Double);
                break;
            case ValueKind.Decimal:
                col.Format(cell.Decimal);
                break;
            case ValueKind.Date:
                col.Set($"{cell.Date:yyyy-MM-dd}");
                break;
            case ValueKind.DateTime:
                col.Set($"{cell.Date:yyyy-MM-dd HH:mm:ss}");
                break;
            case ValueKind.Boolean:
                col.Set(cell.Boolean ? "True" : "False");
                break;
            default:
                col.Set(string.Empty);
                break;
        }
    }
}

/// <summary>
/// Sylvan.Data.Csv's <c>CsvDataWriter</c>, which writes a whole <see cref="System.Data.Common.DbDataReader"/>
/// in one call: the harness's <see cref="DatasetReader"/> hands it the dataset with typed getters, and
/// its date format is set once on the options.
/// </summary>
internal sealed class SylvanCsvWriter : CsvWriterBase
{
    public override string Name => "Sylvan.Data.Csv";

    public override Type Anchor => typeof(CsvDataWriter);

    public override void Write(Scenario scenario, Stream target)
    {
        using StreamWriter text = new(target, Utf8, 64 * 1024, leaveOpen: true);
        using CsvDataWriter writer = CsvDataWriter.Create(text, new CsvDataWriterOptions
        {
            Culture = CultureInfo.InvariantCulture,
            DateTimeFormat = "yyyy-MM-dd HH:mm:ss",
            DateOnlyFormat = "yyyy-MM-dd",
            NewLine = "\r\n",

            // The wide scenario's header row alone is 85 KB, over the default 64 KB buffer a record must fit in.
            BufferSize = 1 << 20,
        });

        writer.Write(new DatasetReader(scenario));
    }
}
