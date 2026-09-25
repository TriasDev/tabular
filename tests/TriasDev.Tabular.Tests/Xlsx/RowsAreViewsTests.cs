using System.Text;

using TriasDev.Tabular.Tests.Fixtures;
using TriasDev.Tabular.Xlsx;

using Xunit;

namespace TriasDev.Tabular.Tests.Xlsx;

/// <summary>
/// "Rows are views": the cursor hands out one reused buffer, so a row of numbers costs no allocation
/// at all once reading is under way.
/// </summary>
/// <remarks>
/// Numbers only, because a text cell allocates its string by design — that is the value, not the
/// row. What must not grow with the row count is everything else: the row, the cells, the scanning.
/// </remarks>
public sealed class RowsAreViewsTests
{
    [Fact]
    public void ReadsRowsOfNumbersWithoutAllocatingPerRow()
    {
        StringBuilder rows = new();

        for (int r = 1; r <= 20_000; r++)
        {
            rows.Append("<row r=\"").Append(r).Append("\">");

            for (int c = 0; c < 8; c++)
            {
                rows.Append("<c><v>").Append((r * 8) + c).Append(".5</v></c>");
            }

            rows.Append("</row>");
        }

        byte[] workbook = new XlsxPackage().WithSheet("S", rows.ToString()).Build();
        using XlsxCursor cursor = new(new MemoryStream(workbook), cancellationToken: TestContext.Current.CancellationToken);
        CancellationToken token = TestContext.Current.CancellationToken;

        // Warm up past the first buffer fills and any lazy set-up.
        for (int i = 0; i < 2_000; i++)
        {
            Assert.True(cursor.ReadRow(token));
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        int read = 0;

        while (cursor.ReadRow(token))
        {
            read++;
        }

        long perRow = (GC.GetAllocatedBytesForCurrentThread() - before) / read;

        // Decompression buffers are refilled, not reallocated; a few bytes a row of slack covers the
        // runtime's own bookkeeping. One string or array per row would be well above this.
        Assert.True(perRow < 16, $"{perRow} bytes allocated per row");
    }

    [Theory]
    [InlineData(13, false)]
    [InlineData(14, true)]
    [InlineData(22, true)]
    [InlineData(23, false)]
    [InlineData(26, false)]
    [InlineData(27, true)]
    [InlineData(36, true)]
    [InlineData(37, false)]
    [InlineData(44, false)]
    [InlineData(45, true)]
    [InlineData(47, true)]
    [InlineData(48, false)]
    [InlineData(49, false)]
    [InlineData(50, true)]
    [InlineData(58, true)]
    [InlineData(59, false)]
    public void KnowsTheEdgesOfTheBuiltInDateFormats(int numFmtId, bool isDate)
    {
        byte[] workbook = new XlsxPackage()
            .WithSheet("S", """<row r="1"><c r="A1" s="1"><v>45000</v></c></row>""")
            .WithStyles($"""<styleSheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><cellXfs count="2"><xf numFmtId="0"/><xf numFmtId="{numFmtId}" applyNumberFormat="1"/></cellXfs></styleSheet>""")
            .Build();
        using XlsxCursor cursor = new(new MemoryStream(workbook), cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(cursor.ReadRow(TestContext.Current.CancellationToken));
        Assert.Equal(isDate ? RawCellKind.Date : RawCellKind.Number, cursor.CurrentRow[0].Kind);
    }
}
