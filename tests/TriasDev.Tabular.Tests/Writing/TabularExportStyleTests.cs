using TriasDev.Tabular.Tests.Fixtures;

using Xunit;

namespace TriasDev.Tabular.Tests.Writing;

/// <summary>An export column styled by a rule on its value.</summary>
public sealed class TabularExportStyleTests
{
    private static readonly CellStyle High = new() { Fill = CellColor.FromRgb(0xF8696B) };

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private sealed record Location(long Id, double? Score);

    private static readonly TabularExport<Location> Export = TabularExport.For<Location>()
        .Column("Id", l => l.Id)
        .Column("Score", l => l.Score, style: s => s > 5 ? High : null)
        .Build();

    [Fact]
    public async Task TheRuleStylesTheCellsItReturnsAStyleFor()
    {
        WriteTarget target = new();
        await Export.WriteAsync(target, TabularFormat.Xlsx, "data", new[] { new Location(1, 2), new Location(2, 9), new Location(3, null) }, cancellationToken: Token);
        string sheet = SheetLayoutTests.Entry(target.ToArray(), "xl/worksheets/sheet1.xml");

        Assert.Contains("<row r=\"2\"><c s=\"3\"><v>1</v></c><c><v>2</v></c></row>", sheet, StringComparison.Ordinal);
        Assert.Contains("<row r=\"3\"><c s=\"3\"><v>2</v></c><c s=\"4\"><v>9</v></c></row>", sheet, StringComparison.Ordinal);
        Assert.Empty(OoxmlValidation.Errors(target.ToArray()));
    }

    [Fact]
    public async Task OneExportServesConcurrentWritersWithTheirOwnStyles()
    {
        Location[] items = [.. Enumerable.Range(1, 2_000).Select(i => new Location(i, i % 10))];

        byte[][] files = await Task.WhenAll(Enumerable.Range(0, 8).Select(async _ =>
        {
            WriteTarget target = new();
            await Export.WriteAsync(target, TabularFormat.Xlsx, "data", items, cancellationToken: Token);
            return target.ToArray();
        }));

        foreach (byte[] file in files)
        {
            Assert.Equal(files[0], file);
            Assert.Empty(OoxmlValidation.Errors(file));
        }
    }

    [Fact]
    public async Task AnExportWithoutRulesWritesWhatItWroteBefore()
    {
        TabularExport<Location> plain = TabularExport.For<Location>().Column("Id", l => l.Id).Column("Score", l => l.Score).Build();
        WriteTarget a = new();
        WriteTarget b = new();
        await plain.WriteAsync(a, TabularFormat.Csv, "data", new[] { new Location(1, 2.5) }, cancellationToken: Token);
        await Export.WriteAsync(b, TabularFormat.Csv, "data", new[] { new Location(1, 2.5) }, cancellationToken: Token);

        Assert.Equal(a.ToArray(), b.ToArray());
    }
}
