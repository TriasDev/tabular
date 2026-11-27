using System.Security.Cryptography;

using Xunit;

namespace TriasDev.Tabular.Tests.Writing;

/// <summary>
/// Pins the exact bytes a sheet without row grouping produces, so a change to the writers can prove that
/// a file without an outline stays byte-for-byte what it was.
/// </summary>
public sealed class OutlineUnchangedBytesTests
{
    private static readonly CellStyle Header = new() { Fill = CellColor.FromRgb(0x1F4E78), Font = new CellFont { Color = CellColor.FromRgb(0xFFFFFF), Bold = true } };

    private static readonly CellStyle Marked = new() { Fill = CellColor.FromRgb(0xFFFF00), Wrap = true };

    private static readonly Dictionary<TabularFormat, string> Expected = new()
    {
        [TabularFormat.Csv] = "D2418F89E05DE2E4383EC91A354876828A023DB3A8EC173009FF455FDEB61661",
        [TabularFormat.Xlsx] = "6AE2311523FBF808DC136D6DE8D4133F1C5D8C8945313BBB3EE9F37DAAE1BE6F",
        [TabularFormat.Ods] = "754CCB19EE42788FF4EDEDBC38BEBB085F8823ED244E538373C4AF030C042A0A",
        [TabularFormat.Zip] = "D27604A6A5DB6772F097FBC7D85558A058EEF4DAAD423769EA5C6868396BCC60",
    };

    /// <summary>
    /// The reference workbook: header style, frozen panes, filter, a styled cell, a 2 × 2 merge and every cell type, then a second sheet.
    /// <paramref name="afterBeginSheet"/> runs on the first sheet right after it begins, before any row.
    /// </summary>
    internal static Task<byte[]> Write(TabularFormat format, Action<TabularWriter>? afterBeginSheet = null) =>
        SheetLayoutTests.Write(format, writer =>
        {
            StyleId marked = writer.RegisterStyle(Marked);
            writer.BeginSheet(
                "data",
                [new("text"), new("long", 12), new("decimal"), new("date"), new("bool")],
                new SheetOptions { HeaderStyle = Header, FreezeRows = 1, FreezeColumns = 1, AutoFilter = true });
            afterBeginSheet?.Invoke(writer);

            for (int i = 0; i < 20; i++)
            {
                DataRow(writer, i, marked);
            }

            if (format != TabularFormat.Csv)               // a csv file holds one sheet
            {
                writer.BeginSheet("second", [new("only")]);
                writer.BeginRow();
                writer.Write("last");
                writer.EndRow();
            }
        });

    /// <summary>Row <paramref name="i"/>: the block merged in long and decimal starts in row 4, so row 4 skips decimal and row 5 both.</summary>
    private static void DataRow(TabularWriter writer, int i, StyleId marked)
    {
        writer.BeginRow();

        if (i == 0)
        {
            writer.Write("marked", marked);
        }
        else
        {
            writer.Write($"row {i}");
        }

        if (i == 4)
        {
            writer.Merge(2, 2);
        }

        if (i != 5)
        {
            writer.Write(i * 1_000_000_007L);
        }

        if (i is not (4 or 5))
        {
            writer.Write(i + 0.25m);
        }

        writer.Write(new DateOnly(2026, 1, 1).AddDays(i));
        writer.Write(i % 2 == 0);
        writer.EndRow();
    }

    [Theory]
    [InlineData(TabularFormat.Csv)]
    [InlineData(TabularFormat.Xlsx)]
    [InlineData(TabularFormat.Ods)]
    [InlineData(TabularFormat.Zip)]
    public async Task ASheetWithoutAnOutlineWritesTheBytesItAlwaysDid(TabularFormat format)
    {
        byte[] bytes = await Write(format);

        Assert.Equal(Expected[format], Convert.ToHexString(SHA256.HashData(bytes)));
    }
}
