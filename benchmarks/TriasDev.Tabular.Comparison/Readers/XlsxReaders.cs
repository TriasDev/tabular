using System.Globalization;
using System.Text;

using ClosedXML.Excel;

using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;

using ExcelDataReader;

using MiniExcelLibs;

using NPOI.SS.UserModel;

using OfficeOpenXml;
using NPOI.XSSF.UserModel;

namespace TriasDev.Tabular.Comparison.Readers;

/// <summary>Sylvan.Data.Excel, through its data reader.</summary>
internal sealed class SylvanExcel : IReader
{
    public string Name => "Sylvan.Data.Excel";

    public Type Anchor => typeof(Sylvan.Data.Excel.ExcelDataReader);

    public FileKind Kind => FileKind.Xlsx;

    public ReadCount Read(string path, char delimiter)
    {
        using Sylvan.Data.Excel.ExcelDataReader excel = Sylvan.Data.Excel.ExcelDataReader.Create(
            path,
            new Sylvan.Data.Excel.ExcelDataReaderOptions { Schema = Sylvan.Data.Excel.ExcelSchema.NoHeaders });

        Tally tally = default;

        while (excel.Read())
        {
            tally.Row();

            for (int i = 0; i < excel.RowFieldCount; i++)
            {
                tally.Value(excel.GetString(i));
            }
        }

        return tally.Result;
    }
}

/// <summary>
/// ExcelDataReader. It needs the code-page provider registered before it opens any workbook, a
/// process-wide change it makes its host responsible for.
/// </summary>
internal sealed class ExcelDataReaderXlsx : IReader
{
    public string Name => "ExcelDataReader";

    public Type Anchor => typeof(ExcelReaderFactory);

    public FileKind Kind => FileKind.Xlsx;

    public ReadCount Read(string path, char delimiter)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

        using FileStream stream = File.OpenRead(path);
        using IExcelDataReader excel = ExcelReaderFactory.CreateReader(stream);

        Tally tally = default;

        while (excel.Read())
        {
            tally.Row();

            for (int i = 0; i < excel.FieldCount; i++)
            {
                tally.Value(Convert.ToString(excel.GetValue(i), CultureInfo.InvariantCulture));
            }
        }

        return tally.Result;
    }
}

/// <summary>MiniExcel's streaming query, which hands back each row as a dictionary.</summary>
internal sealed class MiniExcelXlsx : IReader
{
    public string Name => "MiniExcel";

    public Type Anchor => typeof(MiniExcel);

    public FileKind Kind => FileKind.Xlsx;

    public ReadCount Read(string path, char delimiter)
    {
        Tally tally = default;

        foreach (IDictionary<string, object?> row in MiniExcel.Query(path).Cast<IDictionary<string, object?>>())
        {
            tally.Row();

            foreach (object? value in row.Values)
            {
                tally.Value(Convert.ToString(value, CultureInfo.InvariantCulture));
            }
        }

        return tally.Result;
    }
}

/// <summary>
/// The Open XML SDK in its streaming (SAX) mode — the way its documentation recommends for large
/// files — for both the shared string table and the worksheet.
/// </summary>
internal sealed class OpenXmlSax : IReader
{
    public string Name => "DocumentFormat.OpenXml (SAX)";

    public Type Anchor => typeof(OpenXmlReader);

    public FileKind Kind => FileKind.Xlsx;

    public ReadCount Read(string path, char delimiter)
    {
        using SpreadsheetDocument document = SpreadsheetDocument.Open(path, isEditable: false);

        WorkbookPart workbook = document.WorkbookPart
            ?? throw new InvalidDataException("The package holds no workbook.");

        List<string> shared = [];

        if (workbook.SharedStringTablePart is { } table)
        {
            using OpenXmlReader strings = OpenXmlReader.Create(table);

            while (strings.Read())
            {
                if (strings.ElementType == typeof(SharedStringItem) && strings.IsStartElement)
                {
                    shared.Add(strings.LoadCurrentElement()!.InnerText);
                }
            }
        }

        Sheet first = workbook.Workbook?.Sheets?.Elements<Sheet>().First()
            ?? throw new InvalidDataException("The workbook declares no sheet.");
        WorksheetPart sheet = (WorksheetPart)workbook.GetPartById(first.Id!.Value!);

        using OpenXmlReader reader = OpenXmlReader.Create(sheet);

        Tally tally = default;

        while (reader.Read())
        {
            if (!reader.IsStartElement)
            {
                continue;
            }

            if (reader.ElementType == typeof(Row))
            {
                tally.Row();
            }
            else if (reader.ElementType == typeof(Cell))
            {
                Cell cell = (Cell)reader.LoadCurrentElement()!;

                tally.Value(TextOf(cell, shared));
            }
        }

        return tally.Result;
    }

    private static string? TextOf(Cell cell, List<string> shared)
    {
        if (cell.DataType?.Value == CellValues.SharedString)
        {
            return int.TryParse(cell.CellValue?.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int index)
                && index >= 0 && index < shared.Count
                    ? shared[index]
                    : null;
        }

        return cell.DataType?.Value == CellValues.InlineString
            ? cell.InlineString?.InnerText
            : cell.CellValue?.Text;
    }
}

/// <summary>ClosedXML, which loads the whole workbook into an object model before anything is read.</summary>
internal sealed class ClosedXmlXlsx : IReader
{
    public string Name => "ClosedXML";

    public Type Anchor => typeof(XLWorkbook);

    public FileKind Kind => FileKind.Xlsx;

    public ReadCount Read(string path, char delimiter)
    {
        using XLWorkbook workbook = new(path);

        Tally tally = default;

        foreach (IXLRow row in workbook.Worksheet(1).RowsUsed())
        {
            tally.Row();

            foreach (IXLCell cell in row.CellsUsed())
            {
                tally.Value(cell.GetString());
            }
        }

        return tally.Result;
    }
}

/// <summary>NPOI's XSSF model, which, like ClosedXML, materialises the workbook first.</summary>
internal sealed class NpoiXlsx : IReader
{
    public string Name => "NPOI";

    public Type Anchor => typeof(XSSFWorkbook);

    public FileKind Kind => FileKind.Xlsx;

    public ReadCount Read(string path, char delimiter)
    {
        using FileStream stream = File.OpenRead(path);

        XSSFWorkbook workbook = new(stream);

        try
        {
            ISheet sheet = workbook.GetSheetAt(0);

            Tally tally = default;

            for (int r = sheet.FirstRowNum; r <= sheet.LastRowNum; r++)
            {
                if (sheet.GetRow(r) is not { } row)
                {
                    continue;
                }

                tally.Row();

                foreach (ICell cell in row.Cells)
                {
                    tally.Value(cell.ToString());
                }
            }

            return tally.Result;
        }
        finally
        {
            workbook.Close();
        }
    }
}

/// <summary>
/// EPPlus 4.5.3.3, the last release under the LGPL, which loads the worksheet into its cell store
/// before anything is read.
/// </summary>
internal sealed class EpplusXlsx : IReader
{
    public string Name => "EPPlus";

    public Type Anchor => typeof(ExcelPackage);

    public FileKind Kind => FileKind.Xlsx;

    public ReadCount Read(string path, char delimiter)
    {
        using ExcelPackage package = new(new FileInfo(path));

        ExcelWorksheet sheet = package.Workbook.Worksheets.First();
        Tally tally = default;

        if (sheet.Dimension is not { } used)
        {
            return tally.Result;
        }

        for (int r = used.Start.Row; r <= used.End.Row; r++)
        {
            tally.Row();

            for (int c = used.Start.Column; c <= used.End.Column; c++)
            {
                tally.Value(Convert.ToString(sheet.Cells[r, c].Value, CultureInfo.InvariantCulture));
            }
        }

        return tally.Result;
    }
}
