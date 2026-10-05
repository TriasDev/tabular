using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;

using TriasDev.Tabular.Tests.Fixtures;

using Xunit;

namespace TriasDev.Tabular.Tests.Writing;

/// <summary>
/// A workbook column is never narrower than its header: the width written is the larger of the
/// declared one and the header's, measured generously, so the header is readable wherever it opens.
/// </summary>
/// <remarks>
/// The expected widths are worked out by hand from the measure in <c>HeaderWidth</c>: per character
/// the wider of its Helvetica and Helvetica-Bold advance, in thousandths of an em, 477.27 of them to a
/// character, one character of cell padding, rounded up. "Id" is 278 + 611 = 889 → 1.9 + 1 → 3;
/// "Customer" is 4,667 → 9.8 + 1 → 11; "Customer name" is 7,557 → 15.8 + 1 → 17.
/// </remarks>
public sealed partial class HeaderWidthTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public static TheoryData<TabularFormat> Workbooks => [TabularFormat.Xlsx, TabularFormat.Ods];

    private static async Task<byte[]> Write(TabularFormat format, Action<TabularWriter> write)
    {
        WriteTarget target = new();

        await using (TabularWriter writer = TabularWriter.Create(target, format))
        {
            write(writer);
            await writer.CompleteAsync(Token);
        }

        return target.ToArray();
    }

    private static Task<byte[]> Write(TabularFormat format, WriteColumn[] columns, SheetOptions? options = null) =>
        Write(format, writer => writer.BeginSheet("data", columns, options));

    /// <summary>The widths of the first sheet's columns as written, null for a column given none.</summary>
    private static List<double?> Widths(TabularFormat format, byte[] file)
    {
        using ZipArchive archive = new(new MemoryStream(file, writable: false), ZipArchiveMode.Read);
        string part = format == TabularFormat.Xlsx ? "xl/worksheets/sheet1.xml" : "content.xml";
        using StreamReader reader = new(archive.GetEntry(part)!.Open(), Encoding.UTF8);
        string xml = reader.ReadToEnd();

        if (format == TabularFormat.Xlsx)
        {
            // A column the writer gives no width has no <col>; every test here gives each one.
            return [.. XlsxColumn().Matches(xml).Select(m => (double?)double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture))];
        }

        return [.. OdsColumn().Matches(xml).Select(m => m.Groups[1].Success ? (double?)double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : null)];
    }

    [Theory]
    [MemberData(nameof(Workbooks))]
    public async Task GivesAColumnWithoutAWidthItsHeadersWidth(TabularFormat format)
    {
        byte[] file = await Write(format, [new("Id"), new("Customer name")]);

        Assert.Equal([3, 17], Widths(format, file));
    }

    [Theory]
    [MemberData(nameof(Workbooks))]
    public async Task RaisesADeclaredWidthBelowTheHeadersWidth(TabularFormat format)
    {
        byte[] file = await Write(format, [new("Customer name", 5)]);

        Assert.Equal([17], Widths(format, file));
    }

    [Theory]
    [MemberData(nameof(Workbooks))]
    public async Task KeepsADeclaredWidthAboveTheHeadersWidth(TabularFormat format)
    {
        byte[] file = await Write(format, [new("Id", 30)]);

        Assert.Equal([30], Widths(format, file));
    }

    [Fact]
    public async Task KeepsAFractionalDeclaredWidthInXlsx()
    {
        byte[] file = await Write(TabularFormat.Xlsx, [new("Id", 12.5)]);

        Assert.Equal([12.5], Widths(TabularFormat.Xlsx, file));
    }

    [Theory]
    [MemberData(nameof(Workbooks))]
    public async Task LeavesRoomForTheFilterButton(TabularFormat format)
    {
        // 15.8 + 1 of padding + 3 for the drop-down button → 19.8 → 20.
        byte[] file = await Write(format, [new("Customer name")], new SheetOptions { AutoFilter = true });

        Assert.Equal([20], Widths(format, file));
    }

    [Theory]
    [MemberData(nameof(Workbooks))]
    public async Task MeasuresAWrappedHeaderByItsLongestWord(TabularFormat format)
    {
        byte[] file = await Write(format, [new("Customer name")], new SheetOptions { HeaderStyle = new CellStyle { Wrap = true } });

        Assert.Equal([11], Widths(format, file));
    }

    [Theory]
    [MemberData(nameof(Workbooks))]
    public async Task MeasuresAHeaderOfSeveralLinesByItsLongestLine(TabularFormat format)
    {
        byte[] file = await Write(format, [new("Id\nCustomer"), new("Customer\r\nId")]);

        Assert.Equal([11, 11], Widths(format, file));
    }

    [Theory]
    [MemberData(nameof(Workbooks))]
    public async Task CountsAWideCharacterAsAWholeEm(TabularFormat format)
    {
        // Two CJK ideographs: 2,000 → 4.2 + 1 → 6, where two ASCII letters would be 3.
        byte[] file = await Write(format, [new("顧客")]);

        Assert.Equal([6], Widths(format, file));
    }

    [Theory]
    [MemberData(nameof(Workbooks))]
    public async Task CapsTheHeadersWidthAtTheWidestColumn(TabularFormat format)
    {
        byte[] file = await Write(format, [new(new string('W', 300))]);

        Assert.Equal([255], Widths(format, file));
    }

    [Theory]
    [MemberData(nameof(Workbooks))]
    public async Task GivesADeclaredExportColumnWithoutAWidthItsHeadersWidth(TabularFormat format)
    {
        TabularExport<(int Id, string Name)> export = new TabularExportBuilder<(int Id, string Name)>()
            .Column("Id", r => r.Id)
            .Column("Customer name", r => r.Name, width: 40)
            .Build();

        WriteTarget target = new();
        await export.WriteAsync(target, format, "data", [(1, "a")], cancellationToken: Token);

        Assert.Equal([3, 40], Widths(format, target.ToArray()));
    }

    [Fact]
    public async Task LeavesCsvUnchanged()
    {
        byte[] csv = await Write(TabularFormat.Csv, writer =>
        {
            writer.BeginSheet("data", [new("Id"), new("Customer name", 2)], new SheetOptions { AutoFilter = true });
            writer.BeginRow();
            writer.Write(1);
            writer.Write("a");
            writer.EndRow();
        });

        Assert.Equal([.. Encoding.UTF8.Preamble, .. "Id,Customer name\r\n1,a\r\n"u8], csv);
    }

    [GeneratedRegex("""<col min="\d+" max="\d+" width="([0-9.]+)" customWidth="1"/>""")]
    private static partial Regex XlsxColumn();

    [GeneratedRegex("""<table:table-column(?: table:style-name="co(\d+)")?/>""")]
    private static partial Regex OdsColumn();
}
