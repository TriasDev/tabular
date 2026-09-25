using System.IO.Compression;
using System.Text;

namespace TriasDev.Tabular.Tests.Fixtures;

/// <summary>
/// Writes a minimal OOXML spreadsheet package from raw part content.
/// </summary>
/// <remarks>
/// The golden fixtures exist to pin behaviour on shapes a normal writer will not produce — a shared
/// string built from several rich-text runs, an inline string, a row that starts past column A, the
/// 1904 date system. A high-level writer such as SpreadCheetah emits well-formed ordinary files by
/// design, so it cannot produce them. Assembling the parts by hand is what gives the tests control
/// over the exact bytes a reader will meet.
/// </remarks>
internal sealed class XlsxPackage
{
    private readonly List<(string Name, string RowsXml)> _sheets = [];
    private string? _sharedStringsXml;
    private string? _stylesXml;
    private bool _date1904;
    private Func<string, string> _partNaming = path => path;
    private string _folder = "xl/";
    private Func<int, string> _sheetPart = number => $"worksheets/sheet{number}.xml";
    private string _extraSheetsXml = string.Empty;
    private string _workbookTailXml = string.Empty;
    private string _relationshipNamespace = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";

    /// <summary>Adds a worksheet whose <c>&lt;sheetData&gt;</c> children are supplied verbatim.</summary>
    public XlsxPackage WithSheet(string name, string rowsXml)
    {
        _sheets.Add((name, rowsXml));
        return this;
    }

    /// <summary>
    /// Adds a worksheet whose whole part is supplied verbatim — for markup a well-behaved writer
    /// never produces, such as a part cut off before it ends.
    /// </summary>
    public XlsxPackage WithRawSheet(string name, string worksheetXml)
    {
        _sheets.Add((name, RawMarker + worksheetXml));
        return this;
    }

    private const string RawMarker = "\u0001raw\u0001";

    /// <summary>Supplies the <c>&lt;si&gt;</c> children of the shared string table verbatim.</summary>
    public XlsxPackage WithSharedStrings(string itemsXml)
    {
        _sharedStringsXml = itemsXml;
        return this;
    }

    /// <summary>Supplies the whole stylesheet verbatim.</summary>
    public XlsxPackage WithStyles(string stylesXml)
    {
        _stylesXml = stylesXml;
        return this;
    }

    /// <summary>Declares the workbook to use the 1904 date epoch instead of 1900.</summary>
    public XlsxPackage WithDate1904()
    {
        _date1904 = true;
        return this;
    }

    /// <summary>
    /// Puts the workbook and its parts in another folder — <c>""</c> for the package root — and points
    /// the package's relationships at it.
    /// </summary>
    public XlsxPackage WithWorkbookFolder(string folder)
    {
        _folder = folder;
        return this;
    }

    /// <summary>
    /// Appends raw <c>&lt;sheet&gt;</c> declarations after the worksheets, for sheets that have no
    /// worksheet part — a macro sheet, a chart sheet.
    /// </summary>
    public XlsxPackage WithSheetDeclarations(string sheetsXml)
    {
        _extraSheetsXml = sheetsXml;
        return this;
    }

    /// <summary>
    /// Names the worksheet parts, relative to the workbook's folder, so they can only be found
    /// through the relationships.
    /// </summary>
    public XlsxPackage WithSheetPartNames(Func<int, string> name)
    {
        _sheetPart = name;
        return this;
    }

    /// <summary>Appends raw markup to the workbook part after its sheet list — an extension list, say.</summary>
    public XlsxPackage WithWorkbookTail(string xml)
    {
        _workbookTailXml = xml;
        return this;
    }

    /// <summary>Declares the workbook's <c>r:id</c> attributes in the strict variant's namespace.</summary>
    public XlsxPackage WithStrictRelationshipNamespace()
    {
        _relationshipNamespace = "http://purl.oclc.org/ooxml/officeDocument/relationships";
        return this;
    }

    /// <summary>
    /// Rewrites every part's path, so a package can be built the way a non-Microsoft producer writes
    /// one.
    /// </summary>
    public XlsxPackage WithPartNaming(Func<string, string> naming)
    {
        _partNaming = naming;
        return this;
    }

    public byte[] Build()
    {
        if (_sheets.Count == 0)
        {
            throw new InvalidOperationException("A package needs at least one sheet.");
        }

        using MemoryStream buffer = new();

        using (ZipArchive zip = new(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            Write(zip, _partNaming("[Content_Types].xml"), ContentTypes());
            Write(zip, _partNaming("_rels/.rels"), RootRelationships());
            Write(zip, _partNaming($"{_folder}workbook.xml"), Workbook());
            Write(zip, _partNaming($"{_folder}_rels/workbook.xml.rels"), WorkbookRelationships());

            for (int i = 0; i < _sheets.Count; i++)
            {
                Write(zip, _partNaming($"{_folder}{_sheetPart(i + 1)}"), Worksheet(_sheets[i].RowsXml));
            }

            if (_sharedStringsXml is not null)
            {
                Write(zip, _partNaming($"{_folder}sharedStrings.xml"), SharedStrings(_sharedStringsXml));
            }

            if (_stylesXml is not null)
            {
                Write(zip, _partNaming($"{_folder}styles.xml"), _stylesXml);
            }
        }

        return buffer.ToArray();
    }

    private static void Write(ZipArchive zip, string path, string content)
    {
        ZipArchiveEntry entry = zip.CreateEntry(path, CompressionLevel.Optimal);
        using Stream stream = entry.Open();
        using StreamWriter writer = new(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        writer.Write(content);
    }

    private string ContentTypes()
    {
        StringBuilder sb = new();
        sb.Append("""<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">""");
        sb.Append("""<Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/>""");
        sb.Append("""<Default Extension="xml" ContentType="application/xml"/>""");
        sb.Append($"""<Override PartName="/{_folder}workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/>""");

        for (int i = 0; i < _sheets.Count; i++)
        {
            sb.Append($"""<Override PartName="/{_folder}{_sheetPart(i + 1)}" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/>""");
        }

        if (_sharedStringsXml is not null)
        {
            sb.Append($"""<Override PartName="/{_folder}sharedStrings.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sharedStrings+xml"/>""");
        }

        if (_stylesXml is not null)
        {
            sb.Append($"""<Override PartName="/{_folder}styles.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml"/>""");
        }

        sb.Append("</Types>");
        return sb.ToString();
    }

    private string RootRelationships() =>
        $"""<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="{_folder}workbook.xml"/></Relationships>""";

    private string Workbook()
    {
        StringBuilder sb = new();
        sb.Append($"""<?xml version="1.0" encoding="UTF-8" standalone="yes"?><workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="{_relationshipNamespace}">""");

        if (_date1904)
        {
            sb.Append("""<workbookPr date1904="1"/>""");
        }

        sb.Append("<sheets>");
        for (int i = 0; i < _sheets.Count; i++)
        {
            sb.Append($"""<sheet name="{Escape(_sheets[i].Name)}" sheetId="{i + 1}" r:id="rId{i + 1}"/>""");
        }
        sb.Append(_extraSheetsXml);
        sb.Append("</sheets>");
        sb.Append(_workbookTailXml);
        sb.Append("</workbook>");
        return sb.ToString();
    }

    private string WorkbookRelationships()
    {
        StringBuilder sb = new();
        sb.Append("""<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">""");

        for (int i = 0; i < _sheets.Count; i++)
        {
            sb.Append($"""<Relationship Id="rId{i + 1}" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="{_sheetPart(i + 1)}"/>""");
        }

        int next = _sheets.Count + 1;

        if (_sharedStringsXml is not null)
        {
            sb.Append($"""<Relationship Id="rId{next++}" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/sharedStrings" Target="sharedStrings.xml"/>""");
        }

        if (_stylesXml is not null)
        {
            sb.Append($"""<Relationship Id="rId{next}" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles" Target="styles.xml"/>""");
        }

        sb.Append("</Relationships>");
        return sb.ToString();
    }

    private static string Worksheet(string rowsXml) =>
        rowsXml.StartsWith(RawMarker, StringComparison.Ordinal)
            ? rowsXml[RawMarker.Length..]
            : $"""<?xml version="1.0" encoding="UTF-8" standalone="yes"?><worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><sheetData>{rowsXml}</sheetData></worksheet>""";

    private static string SharedStrings(string itemsXml) =>
        $"""<?xml version="1.0" encoding="UTF-8" standalone="yes"?><sst xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main">{itemsXml}</sst>""";

    private static string Escape(string value) =>
        value.Replace("&", "&amp;", StringComparison.Ordinal)
             .Replace("<", "&lt;", StringComparison.Ordinal)
             .Replace(">", "&gt;", StringComparison.Ordinal)
             .Replace("\"", "&quot;", StringComparison.Ordinal);
}
