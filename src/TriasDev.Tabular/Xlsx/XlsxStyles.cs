using System.Globalization;
using System.Text;

namespace TriasDev.Tabular.Xlsx;

/// <summary>
/// The workbook's styles: four fixed cell formats every unstyled cell uses, then one per registered
/// style and value kind actually written, resolved on first use and cached.
/// </summary>
/// <remarks>
/// The fixed formats, by index: 0 General, 1 a date (built-in 14), 2 a date and time (custom 164),
/// 3 an integer (built-in 1, so an id of twelve digits does not show as <c>1.23457E+11</c>). The
/// reader takes formats 14 and 164 — and any custom code with a date token — for dates. Custom
/// number formats start at 165.
/// </remarks>
internal sealed class XlsxStyles
{
    /// <summary>The cell format index of the fixed date format; the order of <c>_xfs</c> below.</summary>
    internal const int DateXf = 1;

    /// <summary>The cell format index of the fixed date-and-time format.</summary>
    internal const int DateTimeXf = 2;

    /// <summary>The cell format index of the fixed integer format.</summary>
    internal const int IntegerXf = 3;

    private const int BuiltInDate = 14;
    private const int BuiltInDateTime = 164;
    private const int BuiltInInteger = 1;
    private const int FirstCustomFormat = 165;
    private const string XmlDeclaration = "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>";

    private readonly StyleTable _table;
    private readonly List<CellFormat> _xfs = [new(0, 0, 0, 0, CellHorizontalAlignment.General, false), new(BuiltInDate, 0, 0, 0, CellHorizontalAlignment.General, false), new(BuiltInDateTime, 0, 0, 0, CellHorizontalAlignment.General, false), new(BuiltInInteger, 0, 0, 0, CellHorizontalAlignment.General, false)];
    private readonly Dictionary<CellFormat, int> _xfIndices = [];
    private readonly List<CellFont> _fonts = [];          // index + 1 in <fonts>; 0 is the default font
    private readonly Dictionary<CellFont, int> _fontIndices = [];
    private readonly List<CellColor> _fills = [];         // index + 2 in <fills>; 0 none, 1 gray125
    private readonly Dictionary<CellColor, int> _fillIndices = [];
    private readonly List<CellColor> _borders = [];       // index + 1 in <borders>; 0 none
    private readonly Dictionary<CellColor, int> _borderIndices = [];
    private readonly List<string> _formats = [];          // id FirstCustomFormat + index
    private readonly Dictionary<string, int> _formatIds = new(StringComparer.Ordinal);
    private int[] _resolved = [];                         // xf index + 1 per (style, kind); 0 = not yet

    public XlsxStyles(StyleTable table)
    {
        _table = table;

        for (int i = 0; i < _xfs.Count; i++)
        {
            _xfIndices.Add(_xfs[i], i);
        }
    }

    /// <summary>The cell format for a registered style (index 1 onwards) on a kind of value.</summary>
    public int Xf(int style, ValueKind kind)
    {
        int slot = (style * StyleTable.ValueKinds) + (int)kind;
        int[] resolved = _resolved;
        return slot < resolved.Length && resolved[slot] != 0 ? resolved[slot] - 1 : Resolve(slot, style, kind);
    }

    /// <summary>The whole <c>xl/styles.xml</c>.</summary>
    public byte[] Build()
    {
        StringBuilder xml = new(XmlDeclaration + "<styleSheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">");

        xml.Append(CultureInfo.InvariantCulture, $"<numFmts count=\"{1 + _formats.Count}\"><numFmt numFmtId=\"{BuiltInDateTime}\" formatCode=\"yyyy\\-mm\\-dd\\ hh:mm:ss\"/>");

        for (int i = 0; i < _formats.Count; i++)
        {
            xml.Append(CultureInfo.InvariantCulture, $"<numFmt numFmtId=\"{FirstCustomFormat + i}\" formatCode=\"");
            AppendEscaped(xml, _formats[i]);
            xml.Append("\"/>");
        }

        xml.Append(CultureInfo.InvariantCulture, $"</numFmts><fonts count=\"{1 + _fonts.Count}\"><font><sz val=\"11\"/><name val=\"Calibri\"/></font>");

        foreach (CellFont font in _fonts)
        {
            xml.Append("<font>");
            xml.Append(font.Bold ? "<b/>" : string.Empty);
            xml.Append(font.Italic ? "<i/>" : string.Empty);
            xml.Append("<sz val=\"11\"/>");

            if (font.Color is { } color)
            {
                xml.Append("<color rgb=\"").Append(Argb(color)).Append("\"/>");
            }

            xml.Append("<name val=\"Calibri\"/></font>");
        }

        xml.Append(CultureInfo.InvariantCulture, $"</fonts><fills count=\"{2 + _fills.Count}\"><fill><patternFill patternType=\"none\"/></fill><fill><patternFill patternType=\"gray125\"/></fill>");

        foreach (CellColor fill in _fills)
        {
            xml.Append("<fill><patternFill patternType=\"solid\"><fgColor rgb=\"").Append(Argb(fill)).Append("\"/><bgColor indexed=\"64\"/></patternFill></fill>");
        }

        xml.Append(CultureInfo.InvariantCulture, $"</fills><borders count=\"{1 + _borders.Count}\"><border><left/><right/><top/><bottom/><diagonal/></border>");

        foreach (CellColor border in _borders)
        {
            string side = "style=\"thin\"><color rgb=\"" + Argb(border) + "\"/>";
            xml.Append("<border><left ").Append(side).Append("</left><right ").Append(side).Append("</right><top ").Append(side).Append("</top><bottom ").Append(side).Append("</bottom><diagonal/></border>");
        }

        xml.Append("</borders><cellStyleXfs count=\"1\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\"/></cellStyleXfs>");
        xml.Append(CultureInfo.InvariantCulture, $"<cellXfs count=\"{_xfs.Count}\">");

        foreach (CellFormat xf in _xfs)
        {
            AppendXf(xml, xf);
        }

        xml.Append("</cellXfs><cellStyles count=\"1\"><cellStyle name=\"Normal\" xfId=\"0\" builtinId=\"0\"/></cellStyles></styleSheet>");
        return Encoding.UTF8.GetBytes(xml.ToString());
    }

    private int Resolve(int slot, int style, ValueKind kind)
    {
        CellStyle cell = _table[style];

        int format = kind switch
        {
            ValueKind.Integer => cell.NumberFormat is { } number ? FormatId(number.Code) : BuiltInInteger,
            ValueKind.Number => cell.NumberFormat is { } number ? FormatId(number.Code) : 0,
            ValueKind.Date => cell.DateFormat is { } date ? FormatId(date.Code) : BuiltInDate,
            ValueKind.DateTime => cell.DateFormat is { } date ? FormatId(date.Code) : BuiltInDateTime,
            _ => 0,
        };

        int font = cell.Font is { } f ? Index(_fonts, _fontIndices, f) + 1 : 0;
        int fill = cell.Fill is { } c ? Index(_fills, _fillIndices, c) + 2 : 0;
        int border = cell.Border is { } b ? Index(_borders, _borderIndices, b.Color) + 1 : 0;
        int xf = Index(_xfs, _xfIndices, new CellFormat(format, font, fill, border, cell.Horizontal, cell.Wrap));

        if (slot >= _resolved.Length)
        {
            Array.Resize(ref _resolved, Math.Max(slot + 1, _table.Count * StyleTable.ValueKinds));
        }

        _resolved[slot] = xf + 1;
        return xf;
    }

    private int FormatId(string code)
    {
        if (!_formatIds.TryGetValue(code, out int id))
        {
            id = FirstCustomFormat + _formats.Count;
            _formats.Add(code);
            _formatIds.Add(code, id);
        }

        return id;
    }

    private static int Index<T>(List<T> list, Dictionary<T, int> indices, T value)
        where T : notnull
    {
        if (!indices.TryGetValue(value, out int index))
        {
            index = list.Count;
            list.Add(value);
            indices.Add(value, index);
        }

        return index;
    }

    private static void AppendXf(StringBuilder xml, CellFormat xf)
    {
        xml.Append(CultureInfo.InvariantCulture, $"<xf numFmtId=\"{xf.Format}\" fontId=\"{xf.Font}\" fillId=\"{xf.Fill}\" borderId=\"{xf.Border}\" xfId=\"0\"");
        xml.Append(xf.Format != 0 ? " applyNumberFormat=\"1\"" : string.Empty);
        xml.Append(xf.Font != 0 ? " applyFont=\"1\"" : string.Empty);
        xml.Append(xf.Fill != 0 ? " applyFill=\"1\"" : string.Empty);
        xml.Append(xf.Border != 0 ? " applyBorder=\"1\"" : string.Empty);

        if (xf.Horizontal == CellHorizontalAlignment.General && !xf.Wrap)
        {
            xml.Append("/>");
            return;
        }

        xml.Append(" applyAlignment=\"1\"><alignment");

        if (xf.Horizontal != CellHorizontalAlignment.General)
        {
            xml.Append(" horizontal=\"").Append(xf.Horizontal switch { CellHorizontalAlignment.Left => "left", CellHorizontalAlignment.Center => "center", _ => "right" }).Append('"');
        }

        xml.Append(xf.Wrap ? " wrapText=\"1\"" : string.Empty).Append("/></xf>");
    }

    private static string Argb(CellColor color) => string.Create(CultureInfo.InvariantCulture, $"FF{color.Rgb:X6}");

    private static void AppendEscaped(StringBuilder xml, string value)
    {
        foreach (char c in value)
        {
            _ = c switch
            {
                '&' => xml.Append("&amp;"),
                '<' => xml.Append("&lt;"),
                '>' => xml.Append("&gt;"),
                '"' => xml.Append("&quot;"),
                '\t' => xml.Append("&#9;"),
                '\n' => xml.Append("&#10;"),
                '\r' => xml.Append("&#13;"),
                _ => xml.Append(c),
            };
        }
    }

    private readonly record struct CellFormat(int Format, int Font, int Fill, int Border, CellHorizontalAlignment Horizontal, bool Wrap);
}
