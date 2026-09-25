using System.IO.Compression;
using System.Text;

using TriasDev.Tabular.Csv;

using Xunit;

namespace TriasDev.Tabular.Tests;

/// <summary>
/// Files in formats the library does not read are refused by name, not read as garbage.
/// </summary>
/// <remarks>
/// Every file that was not a zip used to be read as csv, so a legacy workbook or a PDF was profiled as
/// Windows-1252 text — columns of mojibake, reported as a successful analysis.
/// </remarks>
public sealed class UnsupportedFormatTests
{
    private static TabularFormatException Refusal(byte[] content)
    {
        using MemoryStream stream = new(content, writable: false);
        TabularFormatException error = Assert.Throws<TabularFormatException>(() => TabularFile.Open(stream, "upload", cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(TabularFormatException.Unsupported, error.Code);
        return error;
    }

    [Fact]
    public void RefusesALegacyOrEncryptedWorkbookByName()
    {
        // The OLE2 compound-file signature: a .xls workbook, or an .xlsx protected with a password.
        byte[] content = new byte[4096];
        new byte[] { 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1 }.CopyTo(content, 0);

        TabularFormatException error = Refusal(content);

        Assert.Contains(".xls", error.Message, StringComparison.Ordinal);
        Assert.Contains("password", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RefusesABinaryFileInsteadOfProfilingItAsText()
    {
        // The start of a PNG: its header carries NUL bytes, which no text file does.
        byte[] content = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00, 0x00, 0x0D, 0x49, 0x48, 0x44, 0x52, .. new byte[256]];

        Assert.Contains("not text", Refusal(content).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void StillReadsATextFileWithAStrayNulByte()
    {
        // Broken exports do leave the odd NUL in a field; one of them does not make a file binary.
        byte[] content = Encoding.UTF8.GetBytes("a;b\n1;x\0y\n2;z\n");

        using MemoryStream stream = new(content, writable: false);
        using ITabularCursor cursor = TabularFile.Open(stream, "t.csv", cancellationToken: TestContext.Current.CancellationToken);

        int rows = 0;

        while (cursor.ReadRow(TestContext.Current.CancellationToken))
        {
            rows++;
        }

        Assert.Equal(3, rows);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReadsUtf16WithoutAByteOrderMark(bool bigEndian)
    {
        // Some "Unicode text" exports leave the mark out. Their NUL bytes stand every other byte, which
        // is how UTF-16 writes ASCII — not how a binary file looks.
        Encoding encoding = bigEndian ? new UnicodeEncoding(bigEndian: true, byteOrderMark: false) : new UnicodeEncoding(bigEndian: false, byteOrderMark: false);
        byte[] content = encoding.GetBytes("name;city\r\nÄrger;München\r\nplain;Köln\r\n");

        using MemoryStream stream = new(content, writable: false);
        using CsvCursor cursor = new(stream, "t.csv");

        Assert.True(cursor.ReadRow(TestContext.Current.CancellationToken));
        Assert.True(cursor.ReadRow(TestContext.Current.CancellationToken));
        Assert.Equal(["Ärger", "München"], cursor.CurrentRow.ToArray().Select(c => c.AsText()));
    }

    [Fact]
    public void RefusesABinaryWorkbookByName()
    {
        byte[] content = Zip(
            ("[Content_Types].xml", "<Types/>"),
            ("_rels/.rels", """<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.bin"/></Relationships>"""),
            ("xl/workbook.bin", "\u0083\u0001\u0000"));

        Assert.Contains(".xlsb", Refusal(content).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RefusesAnOpenDocumentSpreadsheetByName()
    {
        byte[] content = Zip(
            ("mimetype", "application/vnd.oasis.opendocument.spreadsheet"),
            ("content.xml", "<office:document-content/>"));

        Assert.Contains("OpenDocument", Refusal(content).Message, StringComparison.Ordinal);
    }

    private static byte[] Zip(params (string Name, string Content)[] entries)
    {
        using MemoryStream buffer = new();

        using (ZipArchive zip = new(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach ((string name, string content) in entries)
            {
                using Stream entry = zip.CreateEntry(name).Open();
                entry.Write(Encoding.UTF8.GetBytes(content));
            }
        }

        return buffer.ToArray();
    }
}
