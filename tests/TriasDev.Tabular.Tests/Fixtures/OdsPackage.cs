using System.IO.Compression;
using System.Text;

namespace TriasDev.Tabular.Tests.Fixtures;

/// <summary>
/// Builds an OpenDocument spreadsheet from raw XML, one table at a time — for markup a well-behaved
/// writer never produces as much as for the ordinary kind.
/// </summary>
public sealed class OdsPackage
{
    public const string Namespaces =
        """xmlns:office="urn:oasis:names:tc:opendocument:xmlns:office:1.0" xmlns:table="urn:oasis:names:tc:opendocument:xmlns:table:1.0" xmlns:text="urn:oasis:names:tc:opendocument:xmlns:text:1.0" xmlns:dc="http://purl.org/dc/elements/1.1/" xmlns:calcext="urn:org:documentfoundation:names:experimental:calc:xmlns:calcext:1.0" """;

    private readonly List<string> _tables = [];
    private string? _rawContent;
    private string _mimetype = "application/vnd.oasis.opendocument.spreadsheet";
    private bool _mimetypeFirst = true;
    private Encoding _contentEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    /// <summary>Adds a table whose rows (and anything else inside it) are given verbatim.</summary>
    public OdsPackage WithTable(string name, string rowsXml)
    {
        _tables.Add($"""<table:table table:name="{name}"><table:table-column table:number-columns-repeated="3"/>{rowsXml}</table:table>""");
        return this;
    }

    /// <summary>Adds a whole table element verbatim, its own start tag included.</summary>
    public OdsPackage WithRawTable(string tableXml)
    {
        _tables.Add(tableXml);
        return this;
    }

    /// <summary>Supplies the whole content.xml verbatim.</summary>
    public OdsPackage WithRawContent(string contentXml)
    {
        _rawContent = contentXml;
        return this;
    }

    /// <summary>Declares another OpenDocument type, such as a text document.</summary>
    public OdsPackage WithMimetype(string mimetype)
    {
        _mimetype = mimetype;
        return this;
    }

    /// <summary>Writes content.xml in another encoding, with that encoding's byte order mark.</summary>
    public OdsPackage WithContentEncoding(Encoding encoding)
    {
        _contentEncoding = encoding;
        return this;
    }

    /// <summary>Writes the mimetype entry last, against the rule that it comes first.</summary>
    public OdsPackage WithMimetypeLast()
    {
        _mimetypeFirst = false;
        return this;
    }

    public byte[] Build()
    {
        string content = _rawContent ?? ContentXml();

        using MemoryStream buffer = new();

        using (ZipArchive zip = new(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            if (_mimetypeFirst)
            {
                Write(zip, "mimetype", _mimetype, CompressionLevel.NoCompression);
            }

            Write(zip, "META-INF/manifest.xml",
                """<?xml version="1.0" encoding="UTF-8"?><manifest:manifest xmlns:manifest="urn:oasis:names:tc:opendocument:xmlns:manifest:1.0"><manifest:file-entry manifest:full-path="/" manifest:media-type="application/vnd.oasis.opendocument.spreadsheet"/><manifest:file-entry manifest:full-path="content.xml" manifest:media-type="text/xml"/></manifest:manifest>""",
                CompressionLevel.Optimal);
            Write(zip, "content.xml", content, CompressionLevel.Optimal, _contentEncoding);

            if (!_mimetypeFirst)
            {
                Write(zip, "mimetype", _mimetype, CompressionLevel.NoCompression);
            }
        }

        return buffer.ToArray();
    }

    private string ContentXml()
    {
        StringBuilder xml = new($"""<?xml version="1.0" encoding="UTF-8"?><office:document-content {Namespaces}office:version="1.3"><office:body><office:spreadsheet>""");

        foreach (string table in _tables)
        {
            xml.Append(table);
        }

        return xml.Append("</office:spreadsheet></office:body></office:document-content>").ToString();
    }

    private static void Write(ZipArchive zip, string name, string content, CompressionLevel level, Encoding? encoding = null)
    {
        encoding ??= new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        using Stream entry = zip.CreateEntry(name, level).Open();
        entry.Write(encoding.GetPreamble());
        entry.Write(encoding.GetBytes(content));
    }
}
