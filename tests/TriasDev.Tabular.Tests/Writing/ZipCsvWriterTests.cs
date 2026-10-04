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
    public async Task ANameAFileSystemRefusesIsRefused(string name)
    {
        await using TabularWriter writer = TabularWriter.Create(new WriteTarget(), TabularFormat.Zip);

        Assert.Throws<ArgumentException>(() => writer.BeginSheet(name, [new("a")]));
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
            StyleId style = writer.Style(red);
            writer.BeginSheet("Data", [new("a"), new("b")], new SheetOptions { HeaderStyle = red, FreezeRows = 1, AutoFilter = true });
            writer.BeginRow();
            writer.Write("x", style);
            writer.Write("y", style);
            writer.EndRow();
        });

        Assert.Equal(plain, laidOut);
    }
}
