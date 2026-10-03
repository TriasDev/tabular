using System.IO.Compression;
using System.Text;

using TriasDev.Tabular.Ods;
using TriasDev.Tabular.Tests.Fixtures;

using Xunit;

namespace TriasDev.Tabular.Tests.Writing;

/// <summary>The OpenDocument spreadsheet the ods writer streams, read by the library's own cursor as written.</summary>
public sealed class OdsWriterTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static async Task<byte[]> Spreadsheet(Action<TabularWriter> write)
    {
        WriteTarget target = new();

        await using (TabularWriter writer = TabularWriter.Create(target, TabularFormat.Ods))
        {
            write(writer);
            await writer.CompleteAsync(Token);
        }

        return target.ToArray();
    }

    private static List<RawCell[]> Rows(byte[] ods, int sheet = 0)
    {
        using OdsCursor cursor = new(new MemoryStream(ods, writable: false), cancellationToken: Token);
        Assert.True(cursor.MoveToSheet(sheet, Token));

        List<RawCell[]> rows = [];

        while (cursor.ReadRow(Token))
        {
            rows.Add(cursor.CurrentRow.ToArray());
        }

        return rows;
    }

    private static string Content(byte[] ods)
    {
        using ZipArchive archive = new(new MemoryStream(ods, writable: false), ZipArchiveMode.Read);
        using StreamReader reader = new(archive.GetEntry("content.xml")!.Open(), Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private static async Task<RawCell[]> Column(params string[] values)
    {
        byte[] ods = await Spreadsheet(writer =>
        {
            writer.BeginSheet("data", [new("v")]);

            foreach (string value in values)
            {
                writer.BeginRow();
                writer.Write(value);
                writer.EndRow();
            }
        });

        return [.. Rows(ods).Skip(1).Select(r => r[0])];
    }

    [Fact]
    public async Task WritesASpreadsheetTheCursorReadsAndDetects()
    {
        byte[] ods = await Spreadsheet(writer =>
        {
            writer.BeginSheet("Portfolios", [new("Id"), new("Name"), new("Active")]);
            writer.BeginRow();
            writer.Write("P-1");
            writer.Write("Alpha");
            writer.Write(true);
            writer.EndRow();
            writer.BeginRow();
            writer.WriteEmpty();
            writer.Write("Beta");
            writer.Write(false);
            writer.EndRow();
        });

        Assert.Equal(TabularFormat.Ods, TabularFile.Detect(new MemoryStream(ods)));

        List<RawCell[]> rows = Rows(ods);
        Assert.Equal(3, rows.Count);
        Assert.Equal(["Id", "Name", "Active"], rows[0].Select(c => c.Text));
        Assert.Equal(RawCell.FromText("P-1"), rows[1][0]);
        Assert.Equal(RawCell.FromBoolean(true), rows[1][2]);
        Assert.True(rows[2][0].IsEmpty);
        Assert.Equal(RawCell.FromText("Beta"), rows[2][1]);
        Assert.Equal(RawCell.FromBoolean(false), rows[2][2]);
    }

    [Fact]
    public async Task StoresTheMimetypeFirstAsOpenDocumentRequires()
    {
        byte[] ods = await Spreadsheet(writer => writer.BeginSheet("data", [new("a")]));

        Assert.Equal("mimetype", Encoding.ASCII.GetString(ods, 30, 8));
        Assert.Equal("application/vnd.oasis.opendocument.spreadsheet", Encoding.ASCII.GetString(ods, 38, 46));
        Assert.Equal(0, ods[8] | ods[9]);                                   // stored
        Assert.Equal(0, ods[6] & 0x08);                                    // no data descriptor

        using ZipArchive archive = new(new MemoryStream(ods, writable: false), ZipArchiveMode.Read);
        Assert.Equal(["mimetype", "content.xml", "META-INF/manifest.xml", "styles.xml"], archive.Entries.Select(e => e.FullName));
    }

    [Fact]
    public async Task WritesSeveralSheetsInOneContentPart()
    {
        byte[] ods = await Spreadsheet(writer =>
        {
            writer.BeginSheet("First", [new("a")]);
            writer.BeginRow();
            writer.Write("1");
            writer.EndRow();
            writer.BeginSheet("Second", [new("b")]);
            writer.BeginRow();
            writer.Write("2");
            writer.EndRow();
        });

        using OdsCursor cursor = new(new MemoryStream(ods, writable: false), cancellationToken: Token);
        Assert.Equal(["First", "Second"], cursor.Sheets.Select(s => s.Name));
        Assert.Equal(RawCell.FromText("2"), Rows(ods, 1)[1][0]);
    }

    [Fact]
    public async Task NamesSheetsAsWrittenEvenWithXmlCharacters()
    {
        byte[] ods = await Spreadsheet(writer => writer.BeginSheet("R&D <2026> \"q\"", [new("a")]));

        using OdsCursor cursor = new(new MemoryStream(ods, writable: false), cancellationToken: Token);
        Assert.Equal("R&D <2026> \"q\"", Assert.Single(cursor.Sheets).Name);
    }

    [Fact]
    public async Task WritesTextXmlCannotCarryLiterally()
    {
        string[] values = ["a & b < c > d", "Grüße 👍", "_x0041_ stays", "quote \" and apostrophe '"];

        Assert.Equal(values.Select(v => RawCell.FromText(v)), await Column(values));
    }

    [Fact]
    public async Task WritesSpacesTabsAndLineBreaksAsTheReaderReadsThem()
    {
        string[] values = ["a  b", "x   y", "tab\there", "\tlead tab", "two\nlines", "x\n\ny", "a\n  indented", "a \t b"];

        Assert.Equal(values.Select(v => RawCell.FromText(v)), await Column(values));
    }

    [Fact]
    public async Task KeepsACarriageReturnExactly()
    {
        string[] values = ["windows\r\nline", "lone\rreturn", "mixed\r\n\nlines\r"];

        Assert.Equal(values.Select(v => RawCell.FromText(v)), await Column(values));
        Assert.Contains("office:string-value=\"windows&#13;&#10;line\"", Content(await Spreadsheet(writer =>
        {
            writer.BeginSheet("data", [new("v")]);
            writer.BeginRow();
            writer.Write("windows\r\nline");
            writer.EndRow();
        })), StringComparison.Ordinal);
    }

    [Fact]
    public async Task WritesColumnWidthsRoundedToWholeCharacters()
    {
        byte[] ods = await Spreadsheet(writer => writer.BeginSheet("data", [new("a", 12.4), new("b"), new("c", 30)]));

        string content = Content(ods);

        Assert.Contains("<table:table-column table:style-name=\"co12\"/><table:table-column/><table:table-column table:style-name=\"co30\"/>", content, StringComparison.Ordinal);
        Assert.Contains("style:name=\"co255\"", content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RefusesTextLongerThanTheReaderReads()
    {
        await using TabularWriter writer = TabularWriter.Create(new WriteTarget(), TabularFormat.Ods);
        writer.BeginSheet("data", [new("v")]);
        writer.BeginRow();

        Assert.Equal(ErrorCodes.Write.TextTooLong, Assert.Throws<TabularWriteException>(() => writer.Write(new string('x', OdsCursorOptions.Default.MaxValueChars + 1))).Code);
    }

    [Theory]
    [InlineData("History")]
    [InlineData("a/b")]
    [InlineData("a\tb")]
    public async Task RefusesASheetNameExcelRefuses(string name)
    {
        await using TabularWriter writer = TabularWriter.Create(new WriteTarget(), TabularFormat.Ods);

        Assert.Throws<ArgumentException>(() => writer.BeginSheet(name, [new("a")]));
    }

    [Fact]
    public async Task ASpreadsheetDisposedBeforeCompletingDoesNotOpen()
    {
        WriteTarget target = new();
        Random random = new(72);

        await using (TabularWriter writer = TabularWriter.Create(target, TabularFormat.Ods))
        {
            writer.BeginSheet("data", [new("v")]);

            for (int i = 0; i < 50_000; i++)
            {
                writer.BeginRow();
                writer.Write($"{random.NextInt64():x16} a value long enough that a flush writes part of the sheet");
                writer.EndRow();
            }

            await writer.FlushAsync(Token);
        }

        byte[] partial = target.ToArray();
        Assert.True(partial.Length > 64 * 1024, $"only {partial.Length} bytes went out");

        TabularFormatException refused = Assert.Throws<TabularFormatException>(() => new OdsCursor(new MemoryStream(partial, writable: false), cancellationToken: Token));
        Assert.Contains(refused.Code, new[] { ErrorCodes.Format.Corrupt, ErrorCodes.Format.Truncated, ErrorCodes.Format.Unsupported });
    }

    [Fact]
    public async Task TouchesTheTargetOnlyAsynchronously()
    {
        WriteTarget target = new();

        await using (TabularWriter writer = TabularWriter.Create(target, TabularFormat.Ods))
        {
            writer.BeginSheet("data", [new("v")]);

            for (int i = 0; i < 100_000; i++)
            {
                writer.BeginRow();
                writer.Write("row value");
                writer.EndRow();

                if (writer.FlushRecommended)
                {
                    await writer.FlushAsync(Token);
                }
            }

            await writer.CompleteAsync(Token);
        }

        Assert.False(target.DisposedSynchronously);
        Assert.Equal(100_001, Rows(target.ToArray()).Count);
    }

    [Fact]
    public void RefusesACompressionLevelItDoesNotKnow()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => TabularWriter.Create(
            new MemoryStream(),
            TabularFormat.Ods,
            new TabularWriterOptions { Ods = new OdsWriterOptions { CompressionLevel = (CompressionLevel)42 } }));
    }
}
