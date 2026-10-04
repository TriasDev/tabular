using System.Drawing;

using LargeXlsx;

using MiniExcelLibs;
using MiniExcelLibs.OpenXml;

using SpreadCheetah;
using SpreadCheetah.Styling;
using SpreadCheetah.Worksheets;

using SheetNumberFormat = SpreadCheetah.Styling.NumberFormat;
using SheetStyleId = SpreadCheetah.Styling.StyleId;

namespace TriasDev.Tabular.WriteComparison.Writers;

/// <summary>
/// The number formats the xlsx writers give dates. A date is only a date to a reader when its cell has a date
/// format, so the unstyled scenarios carry the plain ISO ones and the styled scenarios the formats of <see cref="Styles"/>.
/// </summary>
internal static class XlsxFormats
{
    public const string PlainDate = "yyyy-mm-dd";
    public const string PlainDateTime = "yyyy-mm-dd hh:mm:ss";

    public static string DateOf(Scenario scenario) => scenario.Styled ? Styles.DateFormat : PlainDate;

    /// <summary>
    /// LargeXlsx maps the code <c>dd/mm/yyyy</c> to the built-in id 14, which a spreadsheet shows in the viewer's
    /// locale (m/d/yyyy in en-US). The escaped slashes are the same display in every locale and are a code of their own.
    /// </summary>
    public static string LocaleFree(string code) => code.Replace("/", "\\/", StringComparison.Ordinal);

    public static string DateTimeOf(Scenario scenario) => scenario.Styled ? Styles.DateTimeFormat : PlainDateTime;

    public static Color Rgb(int rgb) => Color.FromArgb((rgb >> 16) & 0xFF, (rgb >> 8) & 0xFF, rgb & 0xFF);
}

/// <summary>
/// LargeXlsx's streaming <c>XlsxWriter</c>: <c>BeginRow</c> and <c>Write</c> per cell, inline strings (its default,
/// the constant-memory form), styles as <c>XlsxStyle</c> objects it deduplicates, the header row frozen with
/// <c>splitRow</c>. Its fastest compression level is its default.
/// </summary>
internal sealed class LargeXlsxWriter : IWriter
{
    public string Name => "LargeXlsx";

    public Type Anchor => typeof(XlsxWriter);

    public FileKind Kind => FileKind.Xlsx;

    public bool Styled => true;

    public void Write(Scenario scenario, Stream target)
    {
        using XlsxWriter xlsx = new(target);
        IDataset dataset = scenario.Dataset;
        DataColumn[] columns = dataset.Columns;
        XlsxStyle plain = XlsxStyle.Default;
        XlsxStyle[] byColumn = [.. columns.Select(c => ColumnStyle(scenario, c.Kind))];
        XlsxStyle[] legend = [.. Styles.LegendFills.Select(f => plain.With(new XlsxFill(XlsxFormats.Rgb(f))))];

        xlsx.BeginWorksheet("Data", splitRow: scenario.Styled ? 1 : 0);
        xlsx.BeginRow();

        XlsxStyle header = scenario.Styled
            ? plain.With(XlsxFont.Default.WithBold()).With(new XlsxFill(XlsxFormats.Rgb(Styles.HeaderFill)))
            : plain;

        foreach (DataColumn column in columns)
        {
            xlsx.Write(column.Header, header);
        }

        Datum[] cells = new Datum[columns.Length];

        for (long row = 0; row < scenario.Rows; row++)
        {
            dataset.Fill(row, cells);
            xlsx.BeginRow();

            for (int c = 0; c < cells.Length; c++)
            {
                WriteCell(xlsx, in cells[c], byColumn[c], scenario.Styled ? legend : null);
            }
        }
    }

    private static XlsxStyle ColumnStyle(Scenario scenario, ValueKind kind) => kind switch
    {
        ValueKind.Date => XlsxStyle.Default.With(new XlsxNumberFormat(XlsxFormats.LocaleFree(XlsxFormats.DateOf(scenario)))),
        ValueKind.DateTime => XlsxStyle.Default.With(new XlsxNumberFormat(XlsxFormats.LocaleFree(XlsxFormats.DateTimeOf(scenario)))),
        ValueKind.Decimal when scenario.Styled => XlsxStyle.Default.With(new XlsxNumberFormat(Styles.DecimalFormat)),
        _ => XlsxStyle.Default,
    };

    private static void WriteCell(XlsxWriter xlsx, in Datum cell, XlsxStyle style, XlsxStyle[]? legend)
    {
        switch (cell.Kind)
        {
            case ValueKind.Text:
                xlsx.Write(cell.Text, style);
                break;
            case ValueKind.Long:
                xlsx.Write((double)cell.Long, style);
                break;
            case ValueKind.Double:
                xlsx.Write(cell.Double, legend is null ? style : legend[Styles.Legend(cell.Double)]);
                break;
            case ValueKind.Decimal:
                xlsx.Write(cell.Decimal, style);
                break;
            case ValueKind.Date:
            case ValueKind.DateTime:
                xlsx.Write(cell.Date, style);
                break;
            case ValueKind.Boolean:
                xlsx.Write(cell.Boolean, style);
                break;
            default:
                xlsx.SkipColumns(1);
                break;
        }
    }
}

/// <summary>
/// SpreadCheetah's <c>Spreadsheet</c>: <c>AddRowAsync</c> with one reused <c>Cell[]</c> per row (its documented
/// allocation-free form), styles registered once with <c>AddStyle</c> and passed as <c>StyleId</c>, the header
/// frozen through <c>WorksheetOptions.FrozenRows</c>; its default buffer and fastest compression. The async
/// calls are completed synchronously at this boundary, as the harness is synchronous and the target a file.
/// </summary>
internal sealed class SpreadCheetahWriter : IWriter
{
    public string Name => "SpreadCheetah";

    public Type Anchor => typeof(Spreadsheet);

    public FileKind Kind => FileKind.Xlsx;

    public bool Styled => true;

    public void Write(Scenario scenario, Stream target) => WriteAsync(scenario, target).GetAwaiter().GetResult();

    private static async Task WriteAsync(Scenario scenario, Stream target)
    {
        await using Spreadsheet xlsx = await Spreadsheet.CreateNewAsync(target).ConfigureAwait(false);
        IDataset dataset = scenario.Dataset;
        DataColumn[] columns = dataset.Columns;

        SheetStyleId?[] byColumn = [.. columns.Select(c => ColumnStyle(xlsx, scenario, c.Kind))];
        SheetStyleId[] legend = scenario.Styled
            ? [.. Styles.LegendFills.Select(f => xlsx.AddStyle(new Style { Fill = Fill(f) }))]
            : [];

        await xlsx.StartWorksheetAsync("Data", scenario.Styled ? new WorksheetOptions { FrozenRows = 1 } : null).ConfigureAwait(false);

        SheetStyleId? header = scenario.Styled
            ? xlsx.AddStyle(new Style { Font = { Bold = true }, Fill = Fill(Styles.HeaderFill) })
            : null;

        Cell[] cells = [.. columns.Select(c => new Cell(c.Header, header))];
        await xlsx.AddRowAsync(cells).ConfigureAwait(false);

        Datum[] data = new Datum[columns.Length];

        for (long row = 0; row < scenario.Rows; row++)
        {
            dataset.Fill(row, data);

            for (int c = 0; c < data.Length; c++)
            {
                cells[c] = CellOf(in data[c], byColumn[c], legend);
            }

            await xlsx.AddRowAsync(cells).ConfigureAwait(false);
        }

        await xlsx.FinishAsync().ConfigureAwait(false);
    }

    private static Fill Fill(int rgb) => new() { Color = XlsxFormats.Rgb(rgb) };

    private static SheetStyleId? ColumnStyle(Spreadsheet xlsx, Scenario scenario, ValueKind kind) => kind switch
    {
        ValueKind.Date => xlsx.AddStyle(new Style { Format = SheetNumberFormat.Custom(XlsxFormats.DateOf(scenario)) }),
        ValueKind.DateTime => xlsx.AddStyle(new Style { Format = SheetNumberFormat.Custom(XlsxFormats.DateTimeOf(scenario)) }),
        ValueKind.Decimal when scenario.Styled => xlsx.AddStyle(new Style { Format = SheetNumberFormat.Custom(Styles.DecimalFormat) }),
        _ => default,
    };

    private static Cell CellOf(in Datum cell, SheetStyleId? style, SheetStyleId[] legend) => cell.Kind switch
    {
        ValueKind.Text => new Cell(cell.Text, style),
        ValueKind.Long => new Cell(cell.Long, style),
        ValueKind.Double => new Cell(cell.Double, legend.Length == 0 ? style : legend[Styles.Legend(cell.Double)]),
        ValueKind.Decimal => new Cell(cell.Decimal, style),
        ValueKind.Date or ValueKind.DateTime => new Cell(cell.Date, style),
        ValueKind.Boolean => new Cell(cell.Boolean, style),
        _ => default,
    };
}

/// <summary>
/// MiniExcel's <c>SaveAs</c> over an <see cref="System.Data.IDataReader"/>, which it reads row by row: its streaming
/// form (a collection of dictionaries is its other, and allocates one per row). It has no cell styles, so it
/// takes part in the unstyled scenarios only. Its dates are written through its <c>DateTimeFormat</c>
/// configuration, so they carry a date format, and column widths are left alone (auto width needs the whole sheet).
/// </summary>
internal sealed class MiniExcelWriter : IWriter
{
    public string Name => "MiniExcel";

    public Type Anchor => typeof(MiniExcel);

    public FileKind Kind => FileKind.Xlsx;

    public bool Styled => false;

    public void Write(Scenario scenario, Stream target)
    {
        using DatasetReader reader = new(scenario);

        // SaveAs opens its zip in update mode, so the target has to be readable and seekable: the harness opens
        // its files read-write for that. It is how MiniExcel writes, and its memory is measured as such.
        target.SaveAs(reader, true, "Data", ExcelType.XLSX, new OpenXmlConfiguration { EnableAutoWidth = false, FastMode = true });
    }
}
