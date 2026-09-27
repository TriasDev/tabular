using System.Globalization;

using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;

namespace TriasDev.Tabular.Comparison;

// The SDK builds its trees through params constructors that also take an IEnumerable; every call
// here is the params form on purpose.
#pragma warning disable S3220

/// <summary>The producer table written through the Open XML SDK, the way code that uses it directly would.</summary>
internal static class OpenXmlProducer
{
    public static void Write(string path)
    {
        using SpreadsheetDocument document = SpreadsheetDocument.Create(path, SpreadsheetDocumentType.Workbook);
        WorkbookPart workbookPart = document.AddWorkbookPart();
        workbookPart.Workbook = new Workbook();

        WorkbookStylesPart styles = workbookPart.AddNewPart<WorkbookStylesPart>();
        styles.Stylesheet = new Stylesheet(
            new NumberingFormats(new NumberingFormat { NumberFormatId = 164, FormatCode = "yyyy-mm-dd hh:mm:ss" }) { Count = 1 },
            new Fonts(new Font()) { Count = 1 },
            new Fills(new Fill()) { Count = 1 },
            new Borders(new Border()) { Count = 1 },
            new CellFormats(
                new CellFormat(),
                new CellFormat { NumberFormatId = 14, ApplyNumberFormat = true },
                new CellFormat { NumberFormatId = 164, ApplyNumberFormat = true })
            { Count = 3 });

        Sheets sheets = workbookPart.Workbook.AppendChild(new Sheets());
        SheetData data = AddSheet(workbookPart, sheets, ProducerFixtures.SheetName, 1);

        data.Append(OpenXmlRow(1, [.. ProducerFixtures.Header]));

        for (int r = 0; r < ProducerFixtures.Rows.Length; r++)
        {
            data.Append(OpenXmlRow((uint)(r + 2), ProducerFixtures.Rows[r]));
        }

        data.Append(OpenXmlRow(7, [ProducerFixtures.Merged]));
        data.Parent!.InsertAfter(new MergeCells(new MergeCell { Reference = "A7:B7" }) { Count = 1 }, data);
        AddSheet(workbookPart, sheets, ProducerFixtures.SecondSheetName, 2).Append(OpenXmlRow(1, [ProducerFixtures.Second]));
    }

    private static SheetData AddSheet(WorkbookPart workbookPart, Sheets sheets, string name, uint id)
    {
        WorksheetPart part = workbookPart.AddNewPart<WorksheetPart>();
        SheetData data = new();
        part.Worksheet = new Worksheet(data);
        sheets.Append(new Sheet { Id = workbookPart.GetIdOfPart(part), SheetId = id, Name = name });
        return data;
    }

    private static Row OpenXmlRow(uint number, object?[] values)
    {
        Row row = new() { RowIndex = number };

        for (int c = 0; c < values.Length; c++)
        {
            string reference = $"{(char)('A' + c)}{number}";

            Cell? cell = values[c] switch
            {
                string text => new Cell { CellReference = reference, DataType = CellValues.InlineString, InlineString = new InlineString(new Text(text) { Space = SpaceProcessingModeValues.Preserve }) },
                double amount => new Cell { CellReference = reference, CellValue = new CellValue(amount) },
                bool flag => new Cell { CellReference = reference, DataType = CellValues.Boolean, CellValue = new CellValue(flag) },
                DateTime date => new Cell { CellReference = reference, StyleIndex = c == 3 ? 1U : 2U, CellValue = new CellValue(date.ToOADate().ToString("R", CultureInfo.InvariantCulture)) },
                _ => null,
            };

            if (cell is not null)
            {
                row.Append(cell);
            }
        }

        return row;
    }
}
