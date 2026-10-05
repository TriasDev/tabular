namespace TriasDev.Tabular.WriteComparison.Writers;

/// <summary>
/// TriasDev.Tabular through its streaming writer: rows written cell by cell with <c>Write</c> and flushed
/// whenever the writer recommends it, which is the idiom its documentation gives for large files; a wide
/// dataset goes by column through a <see cref="ColumnBatch"/>, because its values arrive by column.
/// </summary>
/// <remarks>
/// Narrow rows go through <c>Write</c> rather than <c>TabularExport&lt;T&gt;</c>: measured on the same data, the direct
/// calls were faster (csv 2.78 s against 2.91 s per million rows, styled xlsx 1.80 s against 2.01 s per 210,000).
/// The writer's async surface is completed with <c>GetAwaiter().GetResult()</c> at this boundary: the
/// harness is synchronous and the target is a file, so no thread is held that matters.
/// </remarks>
internal abstract class TabularWriters : IWriter
{
    /// <summary>Rows per batch of the wide scenarios: a batch holds <c>5,000 × rows</c> doubles, so this is what the harness holds.</summary>
    internal const int WideChunkRows = 500;

    private static readonly CellStyle[] Legend = [.. Styles.LegendFills.Select(f => new CellStyle { Fill = CellColor.FromRgb(f) })];

    private static readonly Func<double?, CellStyle?> LegendRule = value => value is { } v ? Legend[Styles.Legend(v)] : null;

    public string Name => "TriasDev.Tabular";

    public Type Anchor => typeof(TabularWriter);

    public abstract FileKind Kind { get; }

    public bool Styled => true;

    protected abstract TabularFormat Format { get; }

    /// <summary>
    /// The writer's options: the defaults, except that <c>TABULAR_COMPRESSION</c> (Optimal, SmallestSize, NoCompression)
    /// sets the compression level of the xlsx, ods and zip writers, for measuring what a harder compression costs.
    /// </summary>
    private static TabularWriterOptions Options { get; } =
        Enum.TryParse(Environment.GetEnvironmentVariable("TABULAR_COMPRESSION"), true, out System.IO.Compression.CompressionLevel level)
            ? new TabularWriterOptions
            {
                Xlsx = new XlsxWriterOptions { CompressionLevel = level },
                Ods = new OdsWriterOptions { CompressionLevel = level },
                Zip = new ZipWriterOptions { CompressionLevel = level },
            }
            : TabularWriterOptions.Default;

    public void Write(Scenario scenario, Stream target) => WriteAsync(scenario, target).GetAwaiter().GetResult();

    private async Task WriteAsync(Scenario scenario, Stream target)
    {
        await using TabularWriter writer = TabularWriter.Create(target, Format, Options);

        if (scenario.Dataset is WideDataset)
        {
            WriteWide(writer, scenario);
        }
        else
        {
            WriteRows(writer, scenario);
        }

        await writer.CompleteAsync().ConfigureAwait(false);
    }

    private static SheetOptions LayoutOf(Scenario scenario) =>
        scenario.Styled
            ? new SheetOptions
            {
                HeaderStyle = new CellStyle { Font = new CellFont { Bold = true }, Fill = CellColor.FromRgb(Styles.HeaderFill) },
                FreezeRows = 1,
            }
            : new SheetOptions();

    private static WriteColumn[] ColumnsOf(IDataset dataset) => [.. dataset.Columns.Select(c => new WriteColumn(c.Header))];

    private static void WriteRows(TabularWriter writer, Scenario scenario)
    {
        IDataset dataset = scenario.Dataset;
        DataColumn[] columns = dataset.Columns;
        writer.BeginSheet("Data", ColumnsOf(dataset), LayoutOf(scenario));

        StyleId[] styles = [.. columns.Select(c => scenario.Styled ? StyleOf(writer, c.Kind) : default)];
        StyleId[] legend = scenario.Styled ? [.. Legend.Select(writer.RegisterStyle)] : [];
        Datum[] cells = new Datum[columns.Length];

        for (long row = 0; row < scenario.Rows; row++)
        {
            dataset.Fill(row, cells);
            writer.BeginRow();

            for (int c = 0; c < cells.Length; c++)
            {
                WriteCell(writer, in cells[c], styles[c], legend);
            }

            writer.EndRow();

            if (writer.FlushRecommended)
            {
                writer.FlushAsync().AsTask().GetAwaiter().GetResult();
            }
        }
    }

    private static StyleId StyleOf(TabularWriter writer, ValueKind kind) => kind switch
    {
        ValueKind.Date => writer.RegisterStyle(new CellStyle { DateFormat = DateFormat.Parse(Styles.DateFormat) }),
        ValueKind.DateTime => writer.RegisterStyle(new CellStyle { DateFormat = DateFormat.Parse(Styles.DateTimeFormat) }),
        ValueKind.Decimal => writer.RegisterStyle(new CellStyle { NumberFormat = NumberFormat.Parse(Styles.DecimalFormat) }),
        _ => default,
    };

    private static void WriteCell(TabularWriter writer, in Datum cell, StyleId style, StyleId[] legend)
    {
        switch (cell.Kind)
        {
            case ValueKind.Text:
                writer.Write(cell.Text, style);
                break;
            case ValueKind.Long:
                writer.Write(cell.Long, style);
                break;
            case ValueKind.Double:
                writer.Write(cell.Double, legend.Length == 0 ? style : legend[Styles.Legend(cell.Double)]);
                break;
            case ValueKind.Decimal:
                writer.Write(cell.Decimal, style);
                break;
            case ValueKind.Date:
                writer.Write(DateOnly.FromDateTime(cell.Date), style);
                break;
            case ValueKind.DateTime:
                writer.Write(cell.Date, style);
                break;
            case ValueKind.Boolean:
                writer.Write(cell.Boolean, style);
                break;
            default:
                writer.WriteEmpty();
                break;
        }
    }

    private static void WriteWide(TabularWriter writer, Scenario scenario)
    {
        writer.BeginSheet("Data", ColumnsOf(scenario.Dataset), LayoutOf(scenario));
        StyleId date = scenario.Styled ? writer.RegisterStyle(new CellStyle { DateFormat = DateFormat.Parse(Styles.DateFormat) }) : default;

        ColumnBatch batch = new();
        WideChunk chunk = new(WideChunkRows);

        for (long first = 0; first < scenario.Rows; first += WideChunkRows)
        {
            int rows = (int)Math.Min(WideChunkRows, scenario.Rows - first);
            WideChunk current = rows == WideChunkRows ? chunk : new WideChunk(rows);

            current.Fill(first);
            batch.Reset(rows);
            batch.Add(current.Ids);
            batch.Add(current.Names);
            batch.Add(current.Starts, date);

            foreach (double?[] column in current.Measured)
            {
                if (scenario.Styled)
                {
                    batch.Add(column, LegendRule);
                }
                else
                {
                    batch.Add(column);
                }
            }

            writer.WriteBatchAsync(batch).AsTask().GetAwaiter().GetResult();
        }
    }
}

internal sealed class TabularCsvWriter : TabularWriters
{
    public override FileKind Kind => FileKind.Csv;

    protected override TabularFormat Format => TabularFormat.Csv;
}

internal sealed class TabularXlsxWriter : TabularWriters
{
    public override FileKind Kind => FileKind.Xlsx;

    protected override TabularFormat Format => TabularFormat.Xlsx;
}

internal sealed class TabularOdsWriter : TabularWriters
{
    public override FileKind Kind => FileKind.Ods;

    protected override TabularFormat Format => TabularFormat.Ods;
}

internal sealed class TabularZipWriter : TabularWriters
{
    public override FileKind Kind => FileKind.Zip;

    protected override TabularFormat Format => TabularFormat.Zip;
}
