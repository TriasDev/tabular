using System.IO.Compression;
using System.Text;

using TriasDev.Tabular.Tests.Fixtures;
using TriasDev.Tabular.Xlsx;

using Xunit;

namespace TriasDev.Tabular.Tests.Writing;

/// <summary>
/// The workbook the xlsx writer streams: valid to the schema, and read by the library's own cursor
/// as written.
/// </summary>
public sealed class XlsxWriterTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static async Task<byte[]> Workbook(Action<TabularWriter> write)
    {
        WriteTarget target = new();

        await using (TabularWriter writer = TabularWriter.Create(target, TabularFormat.Xlsx))
        {
            write(writer);
            await writer.CompleteAsync(Token);
        }

        return target.ToArray();
    }

    private static List<RawCell[]> Rows(byte[] xlsx, int sheet = 0)
    {
        using XlsxCursor cursor = new(new MemoryStream(xlsx, writable: false), cancellationToken: Token);
        Assert.True(cursor.MoveToSheet(sheet, Token));

        List<RawCell[]> rows = [];

        while (cursor.ReadRow(Token))
        {
            rows.Add(cursor.CurrentRow.ToArray());
        }

        return rows;
    }

    private static string Part(byte[] xlsx, string name)
    {
        using ZipArchive archive = new(new MemoryStream(xlsx, writable: false), ZipArchiveMode.Read);
        using StreamReader reader = new(archive.GetEntry(name)!.Open(), Encoding.UTF8);
        return reader.ReadToEnd();
    }

    [Fact]
    public async Task WritesAWorkbookTheSchemaAcceptsAndTheCursorReads()
    {
        byte[] xlsx = await Workbook(writer =>
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

        Assert.Empty(OoxmlValidation.Errors(xlsx));
        Assert.Equal(TabularFormat.Xlsx, TabularFile.Detect(new MemoryStream(xlsx)));

        List<RawCell[]> rows = Rows(xlsx);
        Assert.Equal(3, rows.Count);
        Assert.Equal(["Id", "Name", "Active"], rows[0].Select(c => c.Text));
        Assert.Equal(RawCell.FromText("P-1"), rows[1][0]);
        Assert.Equal(RawCell.FromBoolean(true), rows[1][2]);
        Assert.True(rows[2][0].IsEmpty);
        Assert.Equal(RawCell.FromText("Beta"), rows[2][1]);
        Assert.Equal(RawCell.FromBoolean(false), rows[2][2]);
    }

    [Fact]
    public async Task WritesSeveralSheetsInOrder()
    {
        byte[] xlsx = await Workbook(writer =>
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

        Assert.Empty(OoxmlValidation.Errors(xlsx));

        using XlsxCursor cursor = new(new MemoryStream(xlsx, writable: false), cancellationToken: Token);
        Assert.Equal(["First", "Second"], cursor.Sheets.Select(s => s.Name));
        Assert.Equal(RawCell.FromText("2"), Rows(xlsx, 1)[1][0]);
    }

    [Fact]
    public async Task NamesSheetsAsWrittenEvenWithXmlCharacters()
    {
        byte[] xlsx = await Workbook(writer => writer.BeginSheet("R&D <2026> \"q\"", [new("a")]));

        Assert.Empty(OoxmlValidation.Errors(xlsx));

        using XlsxCursor cursor = new(new MemoryStream(xlsx, writable: false), cancellationToken: Token);
        Assert.Equal("R&D <2026> \"q\"", Assert.Single(cursor.Sheets).Name);
    }

    [Fact]
    public async Task NumbersEveryRowAndEveryCell()
    {
        byte[] xlsx = await Workbook(writer =>
        {
            writer.BeginSheet("data", [new("a"), new("b")]);
            writer.BeginRow();
            writer.EndRow();
            writer.BeginRow();
            writer.WriteEmpty();
            writer.Write("x");
            writer.EndRow();
        });

        string sheet = Part(xlsx, "xl/worksheets/sheet1.xml");

        Assert.Contains("<row r=\"1\">", sheet, StringComparison.Ordinal);
        Assert.Contains("<row r=\"2\">", sheet, StringComparison.Ordinal);
        Assert.Contains("<row r=\"3\"><c r=\"B3\"", sheet, StringComparison.Ordinal);
        Assert.Equal(RawCell.FromText("x"), Rows(xlsx)[2][1]);
    }

    [Fact]
    public async Task StylesIntegersDatesAndDateTimesOnTheirCells()
    {
        byte[] xlsx = await Workbook(writer =>
        {
            writer.BeginSheet("data", [new("a"), new("b"), new("c")]);
            writer.BeginRow();
            writer.Write(42L);
            writer.Write(new DateOnly(2026, 10, 3));
            writer.Write(new DateTime(2026, 10, 3, 14, 5, 6, DateTimeKind.Unspecified));
            writer.EndRow();
        });

        string sheet = Part(xlsx, "xl/worksheets/sheet1.xml");

        // Consecutive cells carry no reference: each is the column after the one before it.
        Assert.Matches("<row r=\"2\"><c s=\"3\"><v>42</v></c><c s=\"1\"><v>[^<]+</v></c><c s=\"2\"><v>[^<]+</v></c></row>", sheet);
    }

    [Fact]
    public async Task ADisposedMidSheetWriterReleasesItsEntryAndClosesTheTarget()
    {
        WriteTarget target = new();
        TabularWriter writer = TabularWriter.Create(target, TabularFormat.Xlsx);
        writer.BeginSheet("data", [new("v")]);
        writer.BeginRow();
        writer.Write("x");
        writer.EndRow();
        await writer.FlushAsync(Token);

        await writer.DisposeAsync();

        Assert.True(target.IsDisposed);
    }

    [Fact]
    public async Task AWriterFaultedByAValueProblemDisposesWithoutThrowingAndClosesTheTarget()
    {
        WriteTarget target = new();
        TabularWriter writer = TabularWriter.Create(target, TabularFormat.Xlsx);
        writer.BeginSheet("data", [new("v")]);
        writer.BeginRow();

        Assert.Throws<TabularWriteException>(() => writer.Write(new string('x', 32_768)));

        await writer.DisposeAsync();

        Assert.True(target.IsDisposed);
    }

    [Fact]
    public async Task WritesColumnWidthsBeforeTheData()
    {
        byte[] xlsx = await Workbook(writer => writer.BeginSheet("data", [new("a", 12.5), new("b"), new("c", 30)]));

        string sheet = Part(xlsx, "xl/worksheets/sheet1.xml");

        Assert.Contains("<cols><col min=\"1\" max=\"1\" width=\"12.5\" customWidth=\"1\"/><col min=\"3\" max=\"3\" width=\"30\" customWidth=\"1\"/></cols><sheetData>", sheet, StringComparison.Ordinal);
        Assert.Empty(OoxmlValidation.Errors(xlsx));
    }

    [Fact]
    public async Task WritesTextXmlCannotCarryLiterally()
    {
        string[] values = ["a & b < c > d", "two\nlines", "carriage\rreturn", "windows\r\nline", "tab\there", "Grüße 👍", "  padded  "];

        byte[] xlsx = await Workbook(writer =>
        {
            writer.BeginSheet("data", [new("v")]);

            foreach (string value in values)
            {
                writer.BeginRow();
                writer.Write(value);
                writer.EndRow();
            }
        });

        Assert.Empty(OoxmlValidation.Errors(xlsx));
        Assert.Equal(values.Select(v => RawCell.FromText(v)), Rows(xlsx).Skip(1).Select(r => r[0]));
    }

    [Fact]
    public async Task WritesUnderscoreXTextAsWritten()
    {
        string[] values = ["_x0041_", "C:\\_x86_\\", "_x", "a_x0001_b", "__x0041__"];

        byte[] xlsx = await Workbook(writer =>
        {
            writer.BeginSheet("data", [new("v")]);

            foreach (string value in values)
            {
                writer.BeginRow();
                writer.Write(value);
                writer.EndRow();
            }
        });

        Assert.Equal(values.Select(v => RawCell.FromText(v)), Rows(xlsx).Skip(1).Select(r => r[0]));
    }

    [Fact]
    public async Task RefusesTextLongerThanACellHolds()
    {
        await using TabularWriter writer = TabularWriter.Create(new WriteTarget(), TabularFormat.Xlsx);
        writer.BeginSheet("data", [new("v")]);
        writer.BeginRow();

        Assert.Equal(ErrorCodes.Write.TextTooLong, Assert.Throws<TabularWriteException>(() => writer.Write(new string('x', 32_768))).Code);
    }

    [Fact]
    public async Task TakesTextExactlyAsLongAsACellHolds()
    {
        string longest = new('x', 32_767);
        byte[] xlsx = await Workbook(writer =>
        {
            writer.BeginSheet("data", [new("v")]);
            writer.BeginRow();
            writer.Write(longest);
            writer.EndRow();
        });

        Assert.Equal(longest, Rows(xlsx)[1][0].Text);
    }

    [Theory]
    [InlineData("History")]
    [InlineData("a/b")]
    [InlineData("12345678901234567890123456789012")]
    public async Task RefusesASheetNameExcelRefuses(string name)
    {
        await using TabularWriter writer = TabularWriter.Create(new WriteTarget(), TabularFormat.Xlsx);

        Assert.Throws<ArgumentException>(() => writer.BeginSheet(name, [new("a")]));
    }

    [Fact]
    public async Task RefusesTwoSheetsOfTheSameNameIgnoringCase()
    {
        await using TabularWriter writer = TabularWriter.Create(new WriteTarget(), TabularFormat.Xlsx);
        writer.BeginSheet("Data", [new("a")]);

        Assert.Throws<ArgumentException>(() => writer.BeginSheet("DATA", [new("a")]));
    }

    [Fact]
    public async Task AWorkbookDisposedBeforeCompletingDoesNotOpen()
    {
        WriteTarget target = new();

        await using (TabularWriter writer = TabularWriter.Create(target, TabularFormat.Xlsx))
        {
            writer.BeginSheet("data", [new("v")]);

            Random random = new(7);

            for (int i = 0; i < 50_000; i++)
            {
                writer.BeginRow();
                writer.Write($"{random.NextInt64():x} a value long enough that a flush writes part of the sheet");
                writer.EndRow();
            }

            await writer.FlushAsync(Token);
        }

        byte[] partial = target.ToArray();
        Assert.True(partial.Length > 64 * 1024, $"only {partial.Length} bytes went out");

        // No central directory: the workbook reader refuses it rather than read a valid-looking part.
        TabularFormatException refused = Assert.Throws<TabularFormatException>(() => new XlsxCursor(new MemoryStream(partial, writable: false), cancellationToken: Token));
        Assert.Contains(refused.Code, new[] { ErrorCodes.Format.Corrupt, ErrorCodes.Format.Truncated });
    }

    [Fact]
    public async Task TouchesTheTargetOnlyAsynchronously()
    {
        WriteTarget target = new();

        await using (TabularWriter writer = TabularWriter.Create(target, TabularFormat.Xlsx))
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
            TabularFormat.Xlsx,
            new TabularWriterOptions { Xlsx = new XlsxWriterOptions { CompressionLevel = (CompressionLevel)42 } }));
    }
}
