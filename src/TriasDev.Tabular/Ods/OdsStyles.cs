using System.Globalization;
using System.Text;

namespace TriasDev.Tabular.Ods;

/// <summary>A resolved cell style: its name in <c>styles.xml</c>, and whether its numbers are percentages.</summary>
internal readonly record struct OdsCellStyle(string Name, bool Percent);

/// <summary>
/// The spreadsheet's registered styles as common cell styles in <c>styles.xml</c>, with their data
/// styles, written at the end — so they may appear while <c>content.xml</c> streams. One per style
/// and data style actually used, resolved on first use and cached.
/// </summary>
internal sealed class OdsStyles
{
    private const string DateData = "tnd";
    private const string DateTimeData = "tndt";
    private const string BooleanData = "tnb";

    private readonly StyleTable _table;
    private readonly List<string> _cellStyles = [];                     // markup of each ts{n}, n = index + 1
    private readonly Dictionary<(int Style, string? Data), string> _cellNames = [];
    private readonly List<string> _dataStyles = [];                     // markup of each tn{n}
    private readonly Dictionary<string, string> _dataNames = new(StringComparer.Ordinal); // code + kind → name
    private int _numbered;
    private OdsCellStyle[] _resolved = [];                              // per (style, kind); Name null = not yet

    public OdsStyles(StyleTable table) => _table = table;

    /// <summary>The cell style for a registered style (index 1 onwards) on a kind of value.</summary>
    public OdsCellStyle Cell(int style, ValueKind kind)
    {
        int slot = (style * StyleTable.ValueKinds) + (int)kind;
        OdsCellStyle[] resolved = _resolved;
        return slot < resolved.Length && resolved[slot].Name is not null ? resolved[slot] : Resolve(slot, style, kind);
    }

    /// <summary>The whole <c>styles.xml</c>.</summary>
    public byte[] Build()
    {
        StringBuilder xml = new(OdsParts.XmlDeclaration + "<office:document-styles" + OdsParts.Namespaces + "><office:styles>");

        foreach (string data in _dataStyles)
        {
            xml.Append(data);
        }

        foreach (string cell in _cellStyles)
        {
            xml.Append(cell);
        }

        return Encoding.UTF8.GetBytes(xml.Append("</office:styles></office:document-styles>").ToString());
    }

    private OdsCellStyle Resolve(int slot, int style, ValueKind kind)
    {
        CellStyle cell = _table[style];
        bool percent = kind is ValueKind.Integer or ValueKind.Number && cell.Number is { Percent: true };

        string? data = kind switch
        {
            ValueKind.Integer or ValueKind.Number when cell.Number is { } number => DataStyle("n:" + number.Code, name => NumberStyle(name, number)),
            ValueKind.Date or ValueKind.DateTime when cell.Date is { } date => DataStyle("d:" + date.Code, name => DateStyle(name, date)),
            ValueKind.Date => FixedDataStyle(DateData, OdsParts.DateDataStyle),
            ValueKind.DateTime => FixedDataStyle(DateTimeData, OdsParts.DateTimeDataStyle),
            ValueKind.Boolean => FixedDataStyle(BooleanData, name => $"<number:boolean-style style:name=\"{name}\"><number:boolean/></number:boolean-style>"),
            _ => null,
        };

        if (!_cellNames.TryGetValue((style, data), out string? cellName))
        {
            cellName = string.Create(CultureInfo.InvariantCulture, $"ts{_cellStyles.Count + 1}");
            _cellStyles.Add(CellStyleMarkup(cellName, data, cell));
            _cellNames.Add((style, data), cellName);
        }

        if (slot >= _resolved.Length)
        {
            Array.Resize(ref _resolved, Math.Max(slot + 1, _table.Count * StyleTable.ValueKinds));
        }

        return _resolved[slot] = new OdsCellStyle(cellName, percent);
    }

    /// <summary>The data style for a format code — keys "n:…" and "d:…" keep numbers and dates apart — numbered tn1, tn2, … on first use.</summary>
    private string DataStyle(string key, Func<string, string> markup)
    {
        if (!_dataNames.TryGetValue(key, out string? name))
        {
            name = string.Create(CultureInfo.InvariantCulture, $"tn{++_numbered}");
            _dataStyles.Add(markup(name));
            _dataNames.Add(key, name);
        }

        return name;
    }

    /// <summary>A data style with a fixed name (the default date, date-time and boolean ones), declared on first use.</summary>
    private string FixedDataStyle(string name, Func<string, string> markup)
    {
        if (_dataNames.TryAdd(name, name))
        {
            _dataStyles.Add(markup(name));
        }

        return name;
    }

    private static string NumberStyle(string name, NumberFormat format)
    {
        StringBuilder xml = new();
        xml.Append(format.Percent ? "<number:percentage-style" : "<number:number-style").Append(" style:name=\"").Append(name).Append("\">");
        AppendText(xml, format.Prefix);
        xml.Append(CultureInfo.InvariantCulture, $"<number:number number:decimal-places=\"{format.DecimalPlaces}\" number:min-decimal-places=\"{format.MinDecimalPlaces}\" number:min-integer-digits=\"{format.MinIntegerDigits}\"");
        xml.Append(format.Grouping ? " number:grouping=\"true\"/>" : "/>");
        AppendText(xml, format.Percent ? "%" : string.Empty);
        AppendText(xml, format.Suffix);
        return xml.Append(format.Percent ? "</number:percentage-style>" : "</number:number-style>").ToString();
    }

    private static string DateStyle(string name, DateFormat format)
    {
        StringBuilder xml = new($"<number:date-style style:name=\"{name}\">");

        foreach (DatePart part in format.Parts)
        {
            string? element = part.Kind switch
            {
                DatePartKind.Year => "year",
                DatePartKind.Month => "month",
                DatePartKind.Day => "day",
                DatePartKind.Hour => "hours",
                DatePartKind.Minute => "minutes",
                DatePartKind.Second => "seconds",
                _ => null,
            };

            if (element is null)
            {
                AppendText(xml, part.Text);
                continue;
            }

            xml.Append("<number:").Append(element).Append(part.Long ? " number:style=\"long\"/>" : "/>");
        }

        return xml.Append("</number:date-style>").ToString();
    }

    private static string CellStyleMarkup(string name, string? data, CellStyle cell)
    {
        StringBuilder xml = new($"<style:style style:name=\"{name}\" style:family=\"table-cell\"");
        xml.Append(data is null ? ">" : $" style:data-style-name=\"{data}\">");

        xml.Append("<style:table-cell-properties");
        xml.Append(cell.Fill is { } fill ? $" fo:background-color=\"{fill}\"" : string.Empty);
        xml.Append(cell.Border is { } border ? $" fo:border=\"0.06pt solid {border.Color}\"" : string.Empty);
        xml.Append(cell.Wrap ? " fo:wrap-option=\"wrap\"" : string.Empty);
        xml.Append(cell.Horizontal != HorizontalAlignment.General ? " style:text-align-source=\"fix\"" : string.Empty);
        xml.Append("/>");

        if (cell.Horizontal != HorizontalAlignment.General)
        {
            xml.Append("<style:paragraph-properties fo:text-align=\"").Append(cell.Horizontal switch { HorizontalAlignment.Left => "start", HorizontalAlignment.Center => "center", _ => "end" }).Append("\"/>");
        }

        if (cell.Font is { } font)
        {
            xml.Append("<style:text-properties");
            xml.Append(font.Color is { } color ? $" fo:color=\"{color}\"" : string.Empty);
            xml.Append(font.Bold ? " fo:font-weight=\"bold\" style:font-weight-asian=\"bold\" style:font-weight-complex=\"bold\"" : string.Empty);
            xml.Append(font.Italic ? " fo:font-style=\"italic\" style:font-style-asian=\"italic\" style:font-style-complex=\"italic\"" : string.Empty);
            xml.Append("/>");
        }

        return xml.Append("</style:style>").ToString();
    }

    private static void AppendText(StringBuilder xml, string text)
    {
        if (text.Length == 0)
        {
            return;
        }

        xml.Append("<number:text>");

        foreach (char c in text)
        {
            _ = c switch
            {
                '&' => xml.Append("&amp;"),
                '<' => xml.Append("&lt;"),
                '>' => xml.Append("&gt;"),
                _ => xml.Append(c),
            };
        }

        xml.Append("</number:text>");
    }
}
