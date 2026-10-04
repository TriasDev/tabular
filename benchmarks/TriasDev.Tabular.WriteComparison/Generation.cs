namespace TriasDev.Tabular.WriteComparison;

/// <summary>The column arrays of one batch of the wide dataset, reused from batch to batch: what a column-oriented writer is handed.</summary>
internal sealed class WideChunk
{
    public WideChunk(int rows)
    {
        Ids = new long[rows];
        Names = new string[rows];
        Starts = new DateOnly[rows];
        Measured = [.. Enumerable.Range(0, WideDataset.Measured).Select(_ => new double?[rows])];
    }

    public long[] Ids { get; }

    public string[] Names { get; }

    public DateOnly[] Starts { get; }

    public double?[][] Measured { get; }

    public void Fill(long first)
    {
        for (int r = 0; r < Ids.Length; r++)
        {
            long row = first + r;
            Ids[r] = row;
            Names[r] = WideDataset.NameOf(row);
            Starts[r] = DateOnly.FromDateTime(WideDataset.Start(row));
        }

        for (int c = 0; c < Measured.Length; c++)
        {
            double?[] column = Measured[c];

            for (int r = 0; r < column.Length; r++)
            {
                column[r] = WideDataset.Value(first + r, c);
            }
        }
    }
}

/// <summary>
/// Generates a scenario's data and writes nothing, the way each kind of writer consumes it, so the share of
/// every library's time that is only producing the values is on view.
/// </summary>
internal static class Generation
{
    /// <summary>Rows of <see cref="IDataset.Fill"/>, one cell struct per value: how a row-oriented writer reads the data.</summary>
    public static long ByRow(Scenario scenario)
    {
        IDataset dataset = scenario.Dataset;
        Datum[] cells = new Datum[dataset.Columns.Length];
        long values = 0;

        for (long row = 0; row < scenario.Rows; row++)
        {
            dataset.Fill(row, cells);

            foreach (Datum cell in cells)
            {
                values += cell.Kind == ValueKind.Empty ? 0 : 1;
            }
        }

        return values;
    }

    /// <summary>Typed column arrays in batches of <paramref name="chunk"/> rows: how <c>ColumnBatch</c> is fed.</summary>
    public static long ByColumn(Scenario scenario, int chunk)
    {
        long values = 0;
        WideChunk wide = new(chunk);

        for (long first = 0; first < scenario.Rows; first += chunk)
        {
            WideChunk current = scenario.Rows - first >= chunk ? wide : new WideChunk((int)(scenario.Rows - first));
            current.Fill(first);
            values += current.Measured.Length;
        }

        return values;
    }
}
