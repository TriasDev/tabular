using TriasDev.Tabular.Tests.Fixtures;

using Xunit;

namespace TriasDev.Tabular.Tests.Writing;

/// <summary>An export declared with the import's fields writes files the import maps back by header.</summary>
public sealed class TabularExportRoundTripTests
{
    private static readonly IntegerImportField IdField = ImportField.Integer("Id");
    private static readonly TextImportField NameField = ImportField.Text("Name");
    private static readonly DecimalImportField AmountField = ImportField.Decimal("Amount");
    private static readonly DecimalImportField RateField = ImportField.Decimal("Rate");
    private static readonly DateImportField StartField = ImportField.Date("Start");
    private static readonly DateImportField SettledField = ImportField.Date("Settled");
    private static readonly BooleanImportField ActiveField = ImportField.Boolean("Active");

    private static readonly ImportSchema Schema = new() { Fields = [IdField, NameField, AmountField, RateField, StartField, SettledField, ActiveField] };

    private sealed record Portfolio(long Id, string? Name, decimal? Amount, double? Rate, DateTime Start, DateOnly? Settled, bool Active);

    private sealed record Batch(List<Portfolio> Items);

    private static readonly TabularExport<Portfolio> Export = TabularExport.For<Portfolio>()
        .Column(IdField, p => p.Id)
        .Column(NameField, p => p.Name)
        .Column(AmountField, p => p.Amount)
        .Column(RateField, p => p.Rate)
        .Column(StartField, p => p.Start)
        .Column(SettledField, p => p.Settled)
        .Column(ActiveField, p => p.Active)
        .Build();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public static TheoryData<TabularFormat, string> Formats => new()
    {
        { TabularFormat.Csv, "t.csv" },
        { TabularFormat.Xlsx, "t.xlsx" },
        { TabularFormat.Ods, "t.ods" },
    };

    private static DateTime At(int year, int month, int day, int hour = 0, int minute = 0, int second = 0, int millisecond = 0) =>
        new(year, month, day, hour, minute, second, millisecond, DateTimeKind.Unspecified);

    private static Portfolio[] Portfolios(int count) =>
    [
        .. Enumerable.Range(1, count).Select(i => new Portfolio(
            i,
            i % 7 == 0 ? null : $"Portfolio {i}, \"quoted\"",
            i % 5 == 0 ? null : i * 1.25m,
            i % 3 == 0 ? null : i / 8d,
            At(2026, 1 + (i % 12), 1 + (i % 28), i % 24, i % 60, i % 60, i % 1000),
            i % 4 == 0 ? null : new DateOnly(2026, 1 + (i % 12), 1 + (i % 28)),
            i % 2 == 0)),
    ];

    private static async IAsyncEnumerable<Batch> Batches(Portfolio[] items, int size)
    {
        for (int at = 0; at < items.Length; at += size)
        {
            await Task.Yield();
            yield return new Batch([.. items[at..Math.Min(items.Length, at + size)]]);
        }
    }

    private static List<Portfolio> Import(byte[] file, string name)
    {
        using ITabularCursor cursor = TabularFile.Open(new MemoryStream(file, writable: false), name, cancellationToken: Token);
        FileProfile profile = TabularAnalyzer.Analyze(cursor, cancellationToken: Token);
        MappingPlan plan = MappingPlan.ByHeader(profile.Sheets[0], Schema);

        using ImportRun<Portfolio> run = TabularImporter.Import(
            new MemoryStream(file, writable: false),
            name,
            plan,
            Schema,
            row => new Portfolio(row[IdField]!.Value, row[NameField], row[AmountField], row[RateField] is { } rate ? (double)rate : null, row[StartField]!.Value, row[SettledField] is { } settled ? DateOnly.FromDateTime(settled) : null, row[ActiveField]!.Value),
            cancellationToken: Token);

        List<ImportOutcome<Portfolio>> outcomes = [.. run.ReadRows(Token)];
        Assert.All(outcomes, outcome => Assert.False(outcome.HasErrors, string.Join(", ", outcome.Errors.Select(e => e.Code))));
        return [.. outcomes.Select(outcome => outcome.Value!)];
    }

    [Theory]
    [MemberData(nameof(Formats))]
    public async Task MapsBackByHeaderAsWritten(TabularFormat format, string name)
    {
        Portfolio[] written = Portfolios(500);
        WriteTarget target = new();

        await Export.WriteAsync(target, format, "Portfolios", written, cancellationToken: Token);

        Assert.Equal(written, Import(target.ToArray(), name));
    }

    [Theory]
    [MemberData(nameof(Formats))]
    public async Task WritesChunksTakenFromMessages(TabularFormat format, string name)
    {
        Portfolio[] written = Portfolios(2_345);
        WriteTarget target = new();

        long rows = await Export.WriteAsync(target, format, "Portfolios", Batches(written, 1_000), batch => batch.Items, cancellationToken: Token);

        Assert.Equal(written.Length, rows);
        Assert.Equal(written, Import(target.ToArray(), name));
    }

    [Theory]
    [MemberData(nameof(Formats))]
    public async Task OneExportServesConcurrentWrites(TabularFormat format, string name)
    {
        Portfolio[] first = Portfolios(3_000);
        Portfolio[] second = [.. Portfolios(3_000).Select(p => p with { Name = "other " + p.Id })];
        WriteTarget one = new();
        WriteTarget two = new();

        await Task.WhenAll(
            Task.Run(async () => await Export.WriteAsync(one, format, "a", Batches(first, 100), batch => batch.Items, cancellationToken: Token), Token),
            Task.Run(async () => await Export.WriteAsync(two, format, "b", Batches(second, 100), batch => batch.Items, cancellationToken: Token), Token));

        Assert.Equal(first, Import(one.ToArray(), name));
        Assert.Equal(second, Import(two.ToArray(), name));
    }

    [Theory]
    [InlineData(TabularFormat.Csv)]
    [InlineData(TabularFormat.Xlsx)]
    [InlineData(TabularFormat.Ods)]
    public async Task AllocatesNothingPerRow(TabularFormat format)
    {
        // One reused chunk, a source that completes synchronously, and Stream.Null: every await stays
        // on this thread, so the thread's allocation counter sees the whole write and nothing else.
        Portfolio[] chunk = Portfolios(10_000);

        async ValueTask<long> Allocated(int chunks)
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            await Export.WriteAsync(Stream.Null, format, "data", Repeat(chunk, chunks), new TabularWriterOptions { LeaveOpen = true }, Token);
            return GC.GetAllocatedBytesForCurrentThread() - before;
        }

        await Allocated(2);                       // warm-up: static state, pools, JIT
        long tenThousandRows = await Allocated(1);
        long millionRows = await Allocated(100);

        // A million rows against ten thousand: what grows with the row count is under a byte a row.
        Assert.True(millionRows - tenThousandRows < 1_000_000, $"{format}: {tenThousandRows:N0} bytes for 10k rows, {millionRows:N0} for 1M");
    }

    private static async IAsyncEnumerable<IReadOnlyList<Portfolio>> Repeat(Portfolio[] chunk, int times)
    {
        for (int i = 0; i < times; i++)
        {
            yield return chunk;
        }

        await Task.CompletedTask;
    }
}
