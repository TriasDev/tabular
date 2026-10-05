using System.IO.Compression;
using System.Text;

using TriasDev.Tabular.Archive;
using TriasDev.Tabular.Csv;
using TriasDev.Tabular.Tests.Fixtures;

using Xunit;

namespace TriasDev.Tabular.Tests.Writing;

/// <summary>A zip of csv sheets: one entry per sheet, read back sheet for sheet.</summary>
public sealed class ZipCsvWriterTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static void ThreeSheets(TabularWriter writer)
    {
        writer.BeginSheet("Data", [new("Id"), new("Name"), new("Score")]);

        for (int i = 1; i <= 3; i++)
        {
            writer.BeginRow();
            writer.Write((long)i);
            writer.Write($"Location {i}");
            writer.Write(i / 2.0);
            writer.EndRow();
        }

        writer.BeginSheet("Grüße", [new("Key"), new("Value")]);
        writer.BeginRow();
        writer.Write("a,b");
        writer.Write("line\nbreak");
        writer.EndRow();

        writer.BeginSheet("Empty", [new("Only header")]);
    }

    private static Dictionary<string, string> Entries(byte[] zip)
    {
        using ZipArchive archive = new(new MemoryStream(zip, writable: false), ZipArchiveMode.Read);
        return archive.Entries.ToDictionary(e => e.FullName, e =>
        {
            using StreamReader reader = new(e.Open(), new UTF8Encoding(false), detectEncodingFromByteOrderMarks: false);
            return reader.ReadToEnd();
        });
    }

    [Fact]
    public async Task WritesEverySheetAsACsvEntry()
    {
        byte[] zip = await SheetLayoutTests.Write(TabularFormat.Zip, ThreeSheets);
        Dictionary<string, string> entries = Entries(zip);

        Assert.Equal(["Data.csv", "Grüße.csv", "Empty.csv"], entries.Keys);
        Assert.Equal("﻿Id,Name,Score\r\n1,Location 1,0.5\r\n2,Location 2,1\r\n3,Location 3,1.5\r\n", entries["Data.csv"]);
        Assert.Equal("﻿Key,Value\r\n\"a,b\",\"line\nbreak\"\r\n", entries["Grüße.csv"]);
        Assert.Equal("﻿Only header\r\n", entries["Empty.csv"]);
    }

    [Fact]
    public async Task EachEntryIsTheCsvFileTheSameSheetWouldBe()
    {
        byte[] zip = await SheetLayoutTests.Write(TabularFormat.Zip, ThreeSheets);
        byte[] csv = await SheetLayoutTests.Write(TabularFormat.Csv, writer =>
        {
            writer.BeginSheet("Data", [new("Id"), new("Name"), new("Score")]);

            for (int i = 1; i <= 3; i++)
            {
                writer.BeginRow();
                writer.Write((long)i);
                writer.Write($"Location {i}");
                writer.Write(i / 2.0);
                writer.EndRow();
            }
        });

        Assert.Equal(csv, EntryBytes(zip, "Data.csv"));
    }

    private static byte[] EntryBytes(byte[] zip, string name)
    {
        using ZipArchive archive = new(new MemoryStream(zip, writable: false), ZipArchiveMode.Read);
        using MemoryStream entry = new();
        using Stream opened = archive.GetEntry(name)!.Open();
        opened.CopyTo(entry);
        return entry.ToArray();
    }

    [Fact]
    public async Task TheImportReadsTheSheetsBackByName()
    {
        byte[] zip = await SheetLayoutTests.Write(TabularFormat.Zip, ThreeSheets);

        using ITabularCursor cursor = TabularFile.Open(new MemoryStream(zip, writable: false), "export.zip", cancellationToken: Token);
        Dictionary<string, List<RawCell[]>> sheets = [];

        for (int i = 0; cursor.MoveToSheet(i, Token); i++)
        {
            List<RawCell[]> rows = [];

            while (cursor.ReadRow(Token))
            {
                rows.Add(cursor.CurrentRow.ToArray());
            }

            sheets[cursor.Sheets[i].Name] = rows;
        }

        Assert.Equal(["Data", "Empty", "Grüße"], sheets.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(RawCell.FromText("Location 2"), sheets["Data"][2][1]);
        Assert.Equal(RawCell.FromText("line\nbreak"), sheets["Grüße"][1][1]);
        Assert.Single(sheets["Empty"]);
    }

    [Theory]
    [InlineData("a<b")]
    [InlineData("a>b")]
    [InlineData("a\"b")]
    [InlineData("a|b")]
    [InlineData("trailing.")]
    [InlineData("trailing ")]
    [InlineData("CON")]
    [InlineData("prn")]
    [InlineData("Aux")]
    [InlineData("NUL")]
    [InlineData("COM1")]
    [InlineData("com9")]
    [InlineData("LPT1")]
    [InlineData("lpt9")]
    public async Task ANameAFileSystemRefusesIsRefused(string name)
    {
        await using TabularWriter writer = TabularWriter.Create(new WriteTarget(), TabularFormat.Zip);

        Assert.Throws<ArgumentException>(() => writer.BeginSheet(name, [new("a")]));
    }

    [Theory]
    [InlineData("COM0")]
    [InlineData("COM10")]
    [InlineData("CONSOLE")]
    [InlineData("LPT")]
    public async Task NamesLikeDeviceNamesButNotOnesAreAccepted(string name)
    {
        await using TabularWriter writer = TabularWriter.Create(new WriteTarget(), TabularFormat.Zip);

        Assert.Null(Record.Exception(() => writer.BeginSheet(name, [new("a")])));
    }

    [Fact]
    public async Task AWindowsDeviceNameIsAcceptedByXlsx()
    {
        byte[] xlsx = await SheetLayoutTests.Write(TabularFormat.Xlsx, writer => writer.BeginSheet("CON", [new("a")]));

        Assert.NotEmpty(xlsx);
    }

    [Fact]
    public async Task ASheetNameOfMoreThan31CharactersIsRefused()
    {
        await using TabularWriter writer = TabularWriter.Create(new WriteTarget(), TabularFormat.Zip);

        Assert.Throws<ArgumentException>(() => writer.BeginSheet(new string('n', 32), [new("a")]));
    }

    [Fact]
    public async Task AHugeCellDoesNotKeepItsBufferForTheRestOfTheEntry()
    {
        using SpillBuffer buffer = new();
        ZipCsvSheetWriter sheet = new(buffer, ZipWriterOptions.Default, CsvWriterOptions.Default.Resolve());
        sheet.BeginSheet("data", [new("v")], SheetOptions.Default);

        sheet.BeginRow();
        Assert.Null(sheet.WriteText(new string('x', 10_000_000), 0, 0));
        sheet.EndRow();
        sheet.BeginRow();
        Assert.Null(sheet.WriteText("small", 0, 0));
        sheet.EndRow();

        Assert.True(sheet.EntryBufferLength <= 64 * 1024);
        sheet.Complete();
        await buffer.DrainToAsync(Stream.Null, Token);
    }

    [Fact]
    public async Task NamesDifferingOnlyInCaseAreRefused()
    {
        await using TabularWriter writer = TabularWriter.Create(new WriteTarget(), TabularFormat.Zip);
        writer.BeginSheet("Data", [new("a")]);

        Assert.Throws<ArgumentException>(() => writer.BeginSheet("DATA", [new("a")]));
    }

    [Fact]
    public async Task OtherFormatsAcceptThoseNames()
    {
        byte[] xlsx = await SheetLayoutTests.Write(TabularFormat.Xlsx, writer => writer.BeginSheet("a|b.", [new("a")]));

        Assert.NotEmpty(xlsx);
    }

    [Fact]
    public async Task AFailureInTheSecondSheetLeavesNoCentralDirectory()
    {
        WriteTarget target = new();

        await using (TabularWriter writer = TabularWriter.Create(target, TabularFormat.Zip))
        {
            writer.BeginSheet("First", [new("a")]);
            writer.BeginRow();
            writer.Write("ok");
            writer.EndRow();
            writer.BeginSheet("Second", [new("a")]);
            writer.BeginRow();
            Assert.Throws<TabularWriteException>(() => writer.Write(0.1 + 0.2));
            await Assert.ThrowsAsync<InvalidOperationException>(async () => await writer.CompleteAsync(Token));
        }

        Assert.Throws<InvalidDataException>(() => new ZipArchive(new MemoryStream(target.ToArray()), ZipArchiveMode.Read));
    }

    [Fact]
    public async Task TheCsvOptionsApplyToEveryEntry()
    {
        TabularWriterOptions options = new() { Csv = new CsvWriterOptions { ByteOrderMark = false, Delimiter = ';' } };
        WriteTarget target = new();

        await using (TabularWriter writer = TabularWriter.Create(target, TabularFormat.Zip, options))
        {
            writer.BeginSheet("One", [new("a"), new("b")]);
            writer.BeginSheet("Two", [new("c"), new("d")]);
            await writer.CompleteAsync(Token);
        }

        Dictionary<string, string> entries = Entries(target.ToArray());
        Assert.Equal("a;b\r\n", entries["One.csv"]);
        Assert.Equal("c;d\r\n", entries["Two.csv"]);
    }

    [Fact]
    public async Task ACompressionLevelOutsideTheEnumIsRefused()
    {
        TabularWriterOptions options = new() { Zip = new ZipWriterOptions { CompressionLevel = (CompressionLevel)42 } };

        Assert.Throws<ArgumentOutOfRangeException>(() => TabularWriter.Create(new WriteTarget(), TabularFormat.Zip, options));
    }

    [Fact]
    public async Task StylesAndLayoutAreIgnoredAsInCsv()
    {
        CellStyle red = new() { Fill = CellColor.FromRgb(0xFF0000) };

        byte[] plain = await SheetLayoutTests.Write(TabularFormat.Zip, writer =>
        {
            writer.BeginSheet("Data", [new("a"), new("b")]);
            writer.BeginRow();
            writer.Write("x");
            writer.Write("y");
            writer.EndRow();
        });

        byte[] laidOut = await SheetLayoutTests.Write(TabularFormat.Zip, writer =>
        {
            StyleId style = writer.RegisterStyle(red);
            writer.BeginSheet("Data", [new("a"), new("b")], new SheetOptions { HeaderStyle = red, FreezeRows = 1, AutoFilter = true });
            writer.BeginRow();
            writer.Write("x", style);
            writer.Write("y", style);
            writer.EndRow();
        });

        Assert.Equal(plain, laidOut);
    }

    private sealed record Item(long Id, string Name);

    private static readonly TabularExport<Item> Export = TabularExport.For<Item>().Column("Id", i => i.Id).Column("Name", i => i.Name).Build();

    [Fact]
    public async Task AnExportWritesSeveralSheetsIntoOneZip()
    {
        WriteTarget target = new();

        await using (TabularWriter writer = TabularWriter.Create(target, TabularFormat.Zip))
        {
            await Export.WriteSheetAsync(writer, "First", new[] { new Item(1, "a") }, Token);
            await Export.WriteSheetAsync(writer, "Second", new[] { new Item(2, "b"), new Item(3, "c") }, Token);
            await writer.CompleteAsync(Token);
        }

        Dictionary<string, string> entries = Entries(target.ToArray());
        Assert.Equal("\uFEFFId,Name\r\n1,a\r\n", entries["First.csv"]);
        Assert.Equal("\uFEFFId,Name\r\n2,b\r\n3,c\r\n", entries["Second.csv"]);
    }

    [Fact]
    public async Task AFiveHundredThousandRowSheetStreamsWithFlatMemory()
    {
        WriteTarget target = new();
        long[] ids = [.. Enumerable.Range(0, 10_000).Select(i => (long)i)];
        double[] scores = [.. Enumerable.Range(0, 10_000).Select(i => i / 4.0)];
        int largestPending = 0;

        await using (TabularWriter writer = TabularWriter.Create(target, TabularFormat.Zip))
        {
            writer.BeginSheet("Big", [new("Id"), new("Score")]);
            ColumnBatch batch = new();

            for (int chunk = 0; chunk < 50; chunk++)
            {
                batch.Reset(ids.Length);
                batch.Add(ids);
                batch.Add(scores);
                await writer.WriteBatchAsync(batch, Token);
                largestPending = Math.Max(largestPending, writer.PendingBytes);
            }

            writer.BeginSheet("Small", [new("a")]);
            await writer.CompleteAsync(Token);
        }

        Assert.True(largestPending < 2 * 1024 * 1024, $"{largestPending:N0} bytes pending after a batch");

        int lines = CountLines(target.ToArray(), "Big.csv");

        Assert.Equal(500_001, lines);
    }

    private static int CountLines(byte[] zip, string name)
    {
        using ZipArchive archive = new(new MemoryStream(zip, writable: false), ZipArchiveMode.Read);
        using StreamReader reader = new(archive.GetEntry(name)!.Open());
        int lines = 0;

        while (reader.ReadLine() is not null)
        {
            lines++;
        }

        return lines;
    }
}
