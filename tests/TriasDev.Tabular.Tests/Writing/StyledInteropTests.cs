using System.IO.Compression;
using System.Text;

using TriasDev.Tabular.Tests.Fixtures;

using Xunit;

namespace TriasDev.Tabular.Tests.Writing;

/// <summary>LibreOffice shows our formats and keeps our fills and fonts.</summary>
public sealed class StyledInteropTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static readonly CellStyle Red = new()
    {
        Fill = CellColor.FromRgb(0xF8696B),
        Font = new CellFont { Bold = true },
    };

    private static async Task<byte[]> Write(TabularFormat format)
    {
        WriteTarget target = new();

        await using (TabularWriter writer = TabularWriter.Create(target, format))
        {
            StyleId twoPlaces = writer.Style(new CellStyle { Number = NumberFormat.Parse("0.00") });
            StyleId percent = writer.Style(new CellStyle { Number = NumberFormat.Parse("0.0%") });
            StyleId prefixed = writer.Style(new CellStyle { Number = NumberFormat.Parse("\"EUR \"0") });
            StyleId day = writer.Style(new CellStyle { Date = DateFormat.Parse("dd/mm/yyyy") });
            StyleId stamp = writer.Style(new CellStyle { Date = DateFormat.Parse("dd.mm.yyyy hh:mm") });
            StyleId red = writer.Style(Red);

            writer.BeginSheet("data", [new("a"), new("b"), new("c"), new("d"), new("e"), new("f")]);
            writer.BeginRow();
            writer.Write(12.5, twoPlaces);
            writer.Write(0.125, percent);
            writer.Write(42L, prefixed);
            writer.Write(new DateOnly(2026, 10, 4), day);
            writer.Write(new DateTime(2026, 10, 4, 9, 5, 0, DateTimeKind.Unspecified), stamp);
            writer.Write("hot", red);
            writer.EndRow();
            await writer.CompleteAsync(Token);
        }

        return target.ToArray();
    }

    private static string Entry(byte[] zip, string name)
    {
        using ZipArchive archive = new(new MemoryStream(zip, writable: false), ZipArchiveMode.Read);
        using StreamReader reader = new(archive.GetEntry(name)!.Open(), Encoding.UTF8);
        return reader.ReadToEnd();
    }

    [Theory]
    [InlineData(TabularFormat.Xlsx, "xlsx")]
    [InlineData(TabularFormat.Ods, "ods")]
    public async Task ShowsTheFormats(TabularFormat format, string extension)
    {
        string[] lines = LibreOffice.ConvertToCsv(await Write(format), extension);

        // The decimal separator is the LibreOffice profile's locale; the rest is what the codes state.
        Assert.Matches("^\"?12[.,]50\"?,\"?12[.,]5%\"?,\"?EUR 42\"?,04/10/2026,04\\.10\\.2026 09:05,\"?hot\"?$", lines[1]);
    }

    [Fact]
    public async Task KeepsTheFillAndFontOfAnXlsx()
    {
        byte[] ods = LibreOffice.Convert(await Write(TabularFormat.Xlsx), "xlsx", "ods", "ods");
        string styles = Entry(ods, "content.xml") + Entry(ods, "styles.xml");

        Assert.Contains("fo:background-color=\"#f8696b\"", styles, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("fo:font-weight=\"bold\"", styles, StringComparison.Ordinal);
    }

    [Fact]
    public async Task KeepsTheFillAndFontOfAnOds()
    {
        byte[] xlsx = LibreOffice.Convert(await Write(TabularFormat.Ods), "ods", "xlsx:Calc MS Excel 2007 XML", "xlsx");
        string styles = Entry(xlsx, "xl/styles.xml");

        Assert.Contains("rgb=\"FFF8696B\"", styles, StringComparison.OrdinalIgnoreCase);
        Assert.Matches("<b( val=\"(true|1)\")?/>", styles);
    }
}
