using TriasDev.Tabular.Tests.Fixtures;

using Xunit;

namespace TriasDev.Tabular.Tests.Writing;

/// <summary>A wide export — a few fixed columns and thousands of measured ones — written by batch and read back.</summary>
[Collection(AllocationMeasurementCollection.Name)]
public sealed class WideExportTests
{
    private const int Measured = 5_000;

    private static readonly CellStyle[] Legend = [new() { Fill = CellColor.FromRgb(0x63BE7B) }, new() { Fill = CellColor.FromRgb(0xFFEB84) }, new() { Fill = CellColor.FromRgb(0xF8696B) }];

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static readonly WriteColumn[] Columns =
        [new("Id"), new("Name"), new("Start"), .. Enumerable.Range(1, Measured).Select(i => new WriteColumn($"Service__Field{i}"))];

    /// <summary>The value of measured column <paramref name="column"/> in row <paramref name="row"/>: a tenth in 0–99.9, one in ten empty.</summary>
    private static double? Value(int row, int column) => (row + column) % 10 == 0 ? null : ((row * 7) + (column * 13)) % 1000 / 10.0;

    private static CellStyle? Colour(double? value) => value switch { null => null, < 33 => Legend[0], < 66 => Legend[1], _ => Legend[2] };

    /// <summary>The rule as one delegate: a method group converted at each Add would allocate a delegate per column.</summary>
    private static readonly Func<double?, CellStyle?> ColourRule = Colour;

    /// <summary>Fills a batch for rows <paramref name="first"/>…, reusing the column arrays.</summary>
    private static void Fill(ColumnBatch batch, double?[][] measured, long[] ids, string[] names, DateOnly[] starts, int first)
    {
        int rows = ids.Length;

        for (int r = 0; r < rows; r++)
        {
            ids[r] = first + r;
            names[r] = $"Location {first + r}";
            starts[r] = new DateOnly(2026, 1, 1).AddDays((first + r) % 365);
        }

        batch.Reset(rows);
        batch.Add(ids);
        batch.Add(names);
        batch.Add(starts);

        for (int c = 0; c < Measured; c++)
        {
            double?[] column = measured[c];

            for (int r = 0; r < rows; r++)
            {
                column[r] = Value(first + r, c);
            }

            batch.Add(column, ColourRule);
        }
    }

    private static async Task<byte[]> Write(TabularFormat format, int rows, int chunk)
    {
        WriteTarget target = new();
        double?[][] measured = [.. Enumerable.Range(0, Measured).Select(_ => new double?[chunk])];
        long[] ids = new long[chunk];
        string[] names = new string[chunk];
        DateOnly[] starts = new DateOnly[chunk];

        await using (TabularWriter writer = TabularWriter.Create(target, format))
        {
            writer.BeginSheet("Locations", Columns, new SheetOptions { FreezeRows = 1, FreezeColumns = 2 });
            ColumnBatch batch = new();

            for (int first = 0; first < rows; first += chunk)
            {
                Fill(batch, measured, ids, names, starts, first);
                await writer.WriteBatchAsync(batch, Token);
            }

            await writer.CompleteAsync(Token);
        }

        return target.ToArray();
    }

    [Theory]
    [InlineData(TabularFormat.Csv)]
    [InlineData(TabularFormat.Xlsx)]
    [InlineData(TabularFormat.Ods)]
    public async Task AThousandRowsOfFiveThousandColumnsReadBack(TabularFormat format)
    {
        byte[] file = await Write(format, rows: 1_000, chunk: 250);
        List<RawCell[]> rows = SheetLayoutTests.Rows(file);

        Assert.Equal(1_001, rows.Count);
        Assert.Equal(Measured + 3, rows[0].Length);

        for (int r = 0; r < 1_000; r++)
        {
            AssertRow(format, rows[r + 1], r);
        }
    }

    /// <summary>Every cell of a row: Id, Name, Start and all the measured columns, empty ones included.</summary>
    private static void AssertRow(TabularFormat format, RawCell[] row, int r)
    {
        Assert.InRange(row.Length, 3, Measured + 3);          // a reader drops trailing empty cells
        Assert.Equal(format == TabularFormat.Csv ? RawCell.FromText(r.ToString(System.Globalization.CultureInfo.InvariantCulture)) : RawCell.FromNumber(r), row[0]);
        Assert.Equal(RawCell.FromText($"Location {r}"), row[1]);
        Assert.False(row[2].IsEmpty, $"row {r}: Start");

        for (int c = 0; c < Measured; c++)
        {
            RawCell cell = c + 3 < row.Length ? row[c + 3] : default;

            if (Value(r, c) is { } expected)
            {
                Assert.Equal(format == TabularFormat.Csv ? RawCell.FromText(expected.ToString(System.Globalization.CultureInfo.InvariantCulture)) : RawCell.FromNumber(expected), cell);
            }
            else
            {
                Assert.True(cell.IsEmpty, $"row {r}, column {c}");
            }
        }
    }

    [Theory]
    [InlineData(TabularFormat.Csv)]
    [InlineData(TabularFormat.Xlsx)]
    [InlineData(TabularFormat.Ods)]
    public async Task ABatchAllocatesNothingPerCell(TabularFormat format)
    {
        const int Chunk = 100;
        double?[][] measured = [.. Enumerable.Range(0, Measured).Select(_ => new double?[Chunk])];
        long[] ids = new long[Chunk];
        string[] names = new string[Chunk];
        DateOnly[] starts = new DateOnly[Chunk];

        // GetAllocatedBytesForCurrentThread is read across awaits: that holds because every await here
        // completes synchronously on Stream.Null, so the whole run stays on this thread.
        async ValueTask<long> Allocated(int batches)
        {
            await using TabularWriter writer = TabularWriter.Create(Stream.Null, format, new TabularWriterOptions { LeaveOpen = true });
            writer.BeginSheet("Locations", Columns);
            ColumnBatch batch = new();

            // The names are strings built per row by the caller; count only the writer.
            Fill(batch, measured, ids, names, starts, 0);
            long before = GC.GetAllocatedBytesForCurrentThread();

            for (int i = 0; i < batches; i++)
            {
                batch.Reset(Chunk);
                Rebind(batch, measured, ids, names, starts);
                await writer.WriteBatchAsync(batch, Token);
            }

            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            await writer.CompleteAsync(Token);
            return allocated;
        }

        await Allocated(4);                       // warm-up: JIT, the style cache, and the pooled segments (the third batch is the first to need one more)
        long few = await Allocated(2);
        long many = await Allocated(12);

        // Ten more batches of 100 rows × 5,003 cells: what grows with them is under a byte per row.
        Assert.True(many - few < 1_000, $"{format}: {few:N0} bytes for 2 batches, {many:N0} for 12");
    }

    private static void Rebind(ColumnBatch batch, double?[][] measured, long[] ids, string[] names, DateOnly[] starts)
    {
        batch.Add(ids);
        batch.Add(names);
        batch.Add(starts);

        foreach (double?[] column in measured)
        {
            batch.Add(column, ColourRule);
        }
    }
}
