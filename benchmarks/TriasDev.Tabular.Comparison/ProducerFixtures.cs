using System.Data;

using ClosedXML.Excel;

using MiniExcelLibs;

using NPOI.SS.UserModel;
using NPOI.SS.Util;
using NPOI.XSSF.UserModel;

using OfficeOpenXml;

using Sylvan.Data.Excel;

namespace TriasDev.Tabular.Comparison;

/// <summary>
/// Writes one small table with each xlsx writer this project references, so the tests can hold the
/// reader to what real producers emit rather than to what our own fixture builder does.
/// </summary>
/// <remarks>
/// The files are committed under <c>tests/TriasDev.Tabular.Tests/Fixtures/Producers</c>; rerun this
/// only to add a producer or change the table, and the test beside them says what each must read as.
/// </remarks>
internal static class ProducerFixtures
{
    public const string SheetName = "Data";

    public const string SecondSheetName = "Zweite & <Seite>";

    public const string Merged = "merged";

    public const string Second = "second";

    public static readonly string[] Header = ["text", "integer", "decimal", "date", "datetime", "boolean", "gap", "note"];

    /// <summary>The table's rows, header excluded: text, integer, decimal, date, date and time, boolean, nothing, note.</summary>
    public static readonly object?[][] Rows =
    [
        ["plain", 42d, 1.5d, At(2024, 1, 15), At(2024, 1, 15, 10, 30, 0), true, null, "line one\nline two"],
        ["  padded  ", -7d, 0.1d, At(1900, 3, 1), At(1999, 12, 31, 23, 59, 59), false, null, "tab\there"],
        ["<&>\"'", 1e15, 123456.789d, At(2000, 2, 29), At(2038, 1, 19, 3, 14, 7), true, null, "😀 ä ß €"],
        ["_x000D_ as typed", 0d, -0.5d, At(9999, 12, 31), At(1970, 1, 1, 0, 0, 1), false, null, "carriage\r\nreturn"],
    ];

    private static DateTime At(int year, int month, int day, int hour = 0, int minute = 0, int second = 0) =>
        new(year, month, day, hour, minute, second, DateTimeKind.Unspecified);

    public static int Write(string folder)
    {
        Directory.CreateDirectory(folder);

        Run(folder, "closedxml.xlsx", WriteClosedXml);
        Run(folder, "openxml-sdk.xlsx", OpenXmlProducer.Write);
        Run(folder, "epplus4.xlsx", WriteEpplus);
        Run(folder, "npoi.xlsx", WriteNpoi);
        Run(folder, "miniexcel.xlsx", WriteMiniExcel);
        Run(folder, "sylvan.xlsx", WriteSylvan);
        return 0;
    }

    private static void Run(string folder, string name, Action<string> write)
    {
        string path = Path.Combine(folder, name);
        File.Delete(path);
        write(path);
        Console.WriteLine($"{name}\t{new FileInfo(path).Length} bytes");
    }

    private static void WriteClosedXml(string path)
    {
        using XLWorkbook workbook = new();
        IXLWorksheet sheet = workbook.Worksheets.Add(SheetName);

        for (int c = 0; c < Header.Length; c++)
        {
            sheet.Cell(1, c + 1).Value = Header[c];
        }

        for (int r = 0; r < Rows.Length; r++)
        {
            for (int c = 0; c < Rows[r].Length; c++)
            {
                IXLCell cell = sheet.Cell(r + 2, c + 1);

                switch (Rows[r][c])
                {
                    case string text:
                        cell.Value = text;
                        break;
                    case double number:
                        cell.Value = number;
                        break;
                    case bool flag:
                        cell.Value = flag;
                        break;
                    case DateTime date:
                        cell.Value = date;
                        cell.Style.NumberFormat.Format = c == 3 ? "yyyy-mm-dd" : "yyyy-mm-dd hh:mm:ss";
                        break;
                }
            }
        }

        sheet.Range("A7:B7").Merge();
        sheet.Cell(7, 1).Value = Merged;
        workbook.Worksheets.Add(SecondSheetName).Cell(1, 1).Value = Second;
        workbook.SaveAs(path);
    }

    private static void WriteEpplus(string path)
    {
        using ExcelPackage package = new();
        ExcelWorksheet sheet = package.Workbook.Worksheets.Add(SheetName);

        for (int c = 0; c < Header.Length; c++)
        {
            sheet.Cells[1, c + 1].Value = Header[c];
        }

        for (int r = 0; r < Rows.Length; r++)
        {
            for (int c = 0; c < Rows[r].Length; c++)
            {
                if (Rows[r][c] is not { } value)
                {
                    continue;
                }

                sheet.Cells[r + 2, c + 1].Value = value;

                if (value is DateTime)
                {
                    sheet.Cells[r + 2, c + 1].Style.Numberformat.Format = c == 3 ? "yyyy-mm-dd" : "yyyy-mm-dd hh:mm:ss";
                }
            }
        }

        sheet.Cells["A7:B7"].Merge = true;
        sheet.Cells[7, 1].Value = Merged;
        package.Workbook.Worksheets.Add(SecondSheetName).Cells[1, 1].Value = Second;
        package.SaveAs(new FileInfo(path));
    }

    private static void WriteNpoi(string path)
    {
        XSSFWorkbook workbook = new();
        ISheet sheet = workbook.CreateSheet(SheetName);
        IDataFormat formats = workbook.CreateDataFormat();
        ICellStyle dateStyle = workbook.CreateCellStyle();
        dateStyle.DataFormat = formats.GetFormat("yyyy-mm-dd");
        ICellStyle dateTimeStyle = workbook.CreateCellStyle();
        dateTimeStyle.DataFormat = formats.GetFormat("yyyy-mm-dd hh:mm:ss");

        IRow header = sheet.CreateRow(0);

        for (int c = 0; c < Header.Length; c++)
        {
            header.CreateCell(c).SetCellValue(Header[c]);
        }

        for (int r = 0; r < Rows.Length; r++)
        {
            IRow row = sheet.CreateRow(r + 1);

            for (int c = 0; c < Rows[r].Length; c++)
            {
                switch (Rows[r][c])
                {
                    case string text:
                        row.CreateCell(c).SetCellValue(text);
                        break;
                    case double number:
                        row.CreateCell(c).SetCellValue(number);
                        break;
                    case bool flag:
                        row.CreateCell(c).SetCellValue(flag);
                        break;
                    case DateTime date:
                        ICell cell = row.CreateCell(c);
                        cell.SetCellValue(date);
                        cell.CellStyle = c == 3 ? dateStyle : dateTimeStyle;
                        break;
                }
            }
        }

        sheet.CreateRow(6).CreateCell(0).SetCellValue(Merged);
        sheet.AddMergedRegion(new CellRangeAddress(6, 6, 0, 1));
        workbook.CreateSheet(SecondSheetName).CreateRow(0).CreateCell(0).SetCellValue(Second);

        using FileStream file = File.Create(path);
        workbook.Write(file);
    }

    private static void WriteMiniExcel(string path)
    {
        // MiniExcel writes a header from the keys of the first row, and no merged cells.
        List<Dictionary<string, object?>> rows = [.. Rows.Select(row => Header.Zip(row).ToDictionary(p => p.First, p => p.Second))];
        Dictionary<string, object> sheets = new()
        {
            [SheetName] = rows,
            [SecondSheetName] = new[] { new Dictionary<string, object?> { [Second] = null } },
        };

        MiniExcel.SaveAs(path, sheets);
    }

    private static void WriteSylvan(string path)
    {
        // Sylvan writes a header from the reader's column names, one sheet per reader, and no merged cells.
        using DataTable table = new();

        foreach ((string name, object? sample) in Header.Zip(Rows[0]))
        {
            table.Columns.Add(name, sample?.GetType() ?? typeof(string));
        }

        foreach (object?[] row in Rows)
        {
            object[] values = [.. row.Select(v => v ?? DBNull.Value)];
            table.Rows.Add(values);
        }

        using ExcelDataWriter writer = ExcelDataWriter.Create(path);
        using DataTableReader reader = table.CreateDataReader();
        writer.Write(reader, SheetName);
    }
}
