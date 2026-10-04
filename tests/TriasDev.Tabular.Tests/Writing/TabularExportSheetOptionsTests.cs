using TriasDev.Tabular.Tests.Fixtures;

using Xunit;

namespace TriasDev.Tabular.Tests.Writing;

/// <summary>A declared export carries its sheets' layout: header style, frozen panes, filter.</summary>
public sealed class TabularExportSheetOptionsTests
{
    private static readonly CellStyle Header = new() { Fill = CellColor.FromRgb(0x1F4E78), Font = new CellFont { Bold = true } };

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private sealed record Item(long Id, string Name);

    private static readonly TabularExport<Item> Export = TabularExport.For<Item>()
        .Column("Id", i => i.Id)
        .Column("Name", i => i.Name)
        .Sheet(new SheetOptions { HeaderStyle = Header, FreezeRows = 1, AutoFilter = true })
        .Build();

    [Fact]
    public async Task EverySheetTheExportWritesHasTheLayout()
    {
        WriteTarget target = new();

        await using (TabularWriter writer = TabularWriter.Create(target, TabularFormat.Xlsx))
        {
            await Export.WriteSheetAsync(writer, "First", new[] { new Item(1, "a") }, Token);
            await Export.WriteSheetAsync(writer, "Second", new[] { new Item(2, "b"), new Item(3, "c") }, Token);
            await writer.CompleteAsync(Token);
        }

        byte[] xlsx = target.ToArray();
        Assert.Empty(OoxmlValidation.Errors(xlsx));

        foreach ((string part, string range) in new[] { ("xl/worksheets/sheet1.xml", "A1:B2"), ("xl/worksheets/sheet2.xml", "A1:B3") })
        {
            string sheet = SheetLayoutTests.Entry(xlsx, part);
            Assert.Contains("state=\"frozen\"", sheet, StringComparison.Ordinal);
            Assert.Contains($"<autoFilter ref=\"{range}\"/>", sheet, StringComparison.Ordinal);
            Assert.Matches("<row r=\"1\"><c s=\"[1-9][0-9]*\" t=\"inlineStr\">", sheet);
        }
    }

    [Fact]
    public async Task WriteAsyncAppliesTheLayoutToo()
    {
        WriteTarget target = new();
        await Export.WriteAsync(target, TabularFormat.Ods, "data", new[] { new Item(1, "a") }, cancellationToken: Token);

        Assert.Contains("table:database-range", SheetLayoutTests.Entry(target.ToArray(), "content.xml"), StringComparison.Ordinal);
    }

    [Fact]
    public void AFreezePastTheExportsColumnsFailsAtBuild() =>
        Assert.Equal("Sheet", Assert.Throws<ArgumentOutOfRangeException>(() => TabularExport.For<Item>()
            .Column("Id", i => i.Id)
            .Sheet(new SheetOptions { FreezeColumns = 2 })
            .Build()).ParamName);

    [Fact]
    public void ANegativeFreezeFailsAtBuild() =>
        Assert.Equal("Sheet", Assert.Throws<ArgumentOutOfRangeException>(() => TabularExport.For<Item>()
            .Column("Id", i => i.Id)
            .Sheet(new SheetOptions { FreezeRows = -1 })
            .Build()).ParamName);

    [Fact]
    public async Task OneExportWithALayoutServesConcurrentWriters()
    {
        Item[] items = [.. Enumerable.Range(1, 2_000).Select(i => new Item(i, $"n{i}"))];

        byte[][] files = await Task.WhenAll(Enumerable.Range(0, 8).Select(async _ =>
        {
            WriteTarget target = new();
            await Export.WriteAsync(target, TabularFormat.Xlsx, "data", items, cancellationToken: Token);
            return target.ToArray();
        }));

        foreach (byte[] file in files)
        {
            Assert.Equal(files[0], file);
        }
    }

    [Fact]
    public async Task AnExportWithoutALayoutWritesWhatItWroteBefore()
    {
        TabularExport<Item> plain = TabularExport.For<Item>().Column("Id", i => i.Id).Column("Name", i => i.Name).Build();
        WriteTarget a = new();
        WriteTarget b = new();
        await plain.WriteAsync(a, TabularFormat.Xlsx, "data", new[] { new Item(1, "a") }, cancellationToken: Token);
        await TabularExport.For<Item>().Column("Id", i => i.Id).Column("Name", i => i.Name).Sheet(new SheetOptions()).Build()
            .WriteAsync(b, TabularFormat.Xlsx, "data", new[] { new Item(1, "a") }, cancellationToken: Token);

        Assert.Equal(a.ToArray(), b.ToArray());
    }
}
