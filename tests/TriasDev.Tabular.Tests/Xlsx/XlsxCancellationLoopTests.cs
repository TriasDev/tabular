using System.IO.Compression;
using System.Text;

using TriasDev.Tabular.Tests.Fixtures;
using TriasDev.Tabular.Xlsx;

using Xunit;

namespace TriasDev.Tabular.Tests.Xlsx;

/// <summary>
/// Every loop that scans a part of a workbook checks its token as it goes, not only before it starts:
/// a hostile part of countless nodes this reader wants nothing from is stopped where the cancellation
/// lands, rather than read to its end first.
/// </summary>
/// <remarks>
/// The noise is incompressible — random digits in attributes or comments — so the part is still being
/// inflated from the stream while its loop runs, and how far the stream was read when the cancellation
/// surfaced tells a check inside the loop from one after it: past the loop, the whole part is read.
/// </remarks>
public sealed class XlsxCancellationLoopTests
{
    /// <summary>Enough nodes that the part's compressed bytes far exceed one stride of 4,096 nodes.</summary>
    private const int Nodes = 60_000;

    private const string Cell = """<row r="1"><c r="A1" t="inlineStr"><is><t>a</t></is></c></row>""";

    private static string Noise(string element = "z")
    {
        Random random = new(106);
        StringBuilder xml = new(Nodes * 28);

        for (int i = 0; i < Nodes; i++)
        {
            xml.Append('<').Append(element).Append(" q=\"").Append(random.NextInt64()).Append("\"/>");
        }

        return xml.ToString();
    }

    /// <summary>A value of countless text nodes, each cut off from the next by a comment.</summary>
    private static string TextNodes()
    {
        Random random = new(106);
        StringBuilder xml = new(Nodes * 28);

        for (int i = 0; i < Nodes; i++)
        {
            xml.Append(i % 10).Append("<!--").Append(random.NextInt64()).Append("-->");
        }

        return xml.ToString();
    }

    /// <summary>The package with one part's text rewritten.</summary>
    private static byte[] WithPart(byte[] package, string path, Func<string, string> edit)
    {
        using MemoryStream buffer = new();
        buffer.Write(package);

        using (ZipArchive zip = new(buffer, ZipArchiveMode.Update, leaveOpen: true))
        {
            ZipArchiveEntry entry = zip.GetEntry(path) ?? throw new InvalidOperationException(path);
            string text;

            using (StreamReader reader = new(entry.Open(), Encoding.UTF8))
            {
                text = reader.ReadToEnd();
            }

            entry.Delete();

            using StreamWriter writer = new(zip.CreateEntry(path, CompressionLevel.Optimal).Open(), new UTF8Encoding(false));
            writer.Write(edit(text));
        }

        return buffer.ToArray();
    }

    private static long CompressedLength(byte[] package, string path)
    {
        using ZipArchive zip = new(new MemoryStream(package, writable: false), ZipArchiveMode.Read);
        return zip.GetEntry(path)!.CompressedLength;
    }

    public static TheoryData<string> OpenTimeParts => ["_rels/.rels", "xl/_rels/workbook.xml.rels", "xl/workbook.xml", "xl/styles.xml"];

    private static byte[] OpenTimeWorkbook(string part)
    {
        byte[] plain = new XlsxPackage()
            .WithSheet("Sheet1", Cell)
            .WithStyles("""<?xml version="1.0" encoding="UTF-8"?><styleSheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><cellXfs count="1"><xf numFmtId="0"/></cellXfs></styleSheet>""")
            .Build();

        // Ahead of the part's own content, so the loop meets the noise before anything it wants.
        return WithPart(plain, part, xml =>
        {
            int root = xml.IndexOf('>', xml.IndexOf('<', xml.IndexOf("?>", StringComparison.Ordinal) + 2)) + 1;
            return xml[..root] + Noise() + xml[root..];
        });
    }

    [Theory]
    [MemberData(nameof(OpenTimeParts))]
    public void OpeningStopsInsideAPartOfCountlessNodes(string part)
    {
        byte[] package = OpenTimeWorkbook(part);
        long partBytes = CompressedLength(package, part);

        // Sound before anything cancels: the noise is markup the reader may ignore.
        using (XlsxCursor whole = new(new MemoryStream(package, writable: false), cancellationToken: TestContext.Current.CancellationToken))
        {
            Assert.True(whole.ReadRow(TestContext.Current.CancellationToken));
        }

        using CancellationTokenSource source = new();
        using CancellingStream stream = new(package, source);
        stream.CancelAfter(16 * 1024);

        Assert.Throws<OperationCanceledException>(() => new XlsxCursor(stream, cancellationToken: source.Token));
        Assert.True(stream.Served < (16 * 1024) + (partBytes / 2), $"{stream.Served} bytes were read of a {partBytes}-byte part before the cancellation was seen");
    }

    public static TheoryData<string> RowTimeMarkup => ["between rows", "inside a cell", "inside a value", "between shared strings", "inside a shared string"];

    private static (byte[] Package, string Part) RowTimeWorkbook(string where)
    {
        XlsxPackage package = new();

        package = where switch
        {
            "between rows" => package.WithSheet("Sheet1", Noise() + Cell),
            "inside a cell" => package.WithSheet("Sheet1", $"""<row r="1"><c r="A1">{Noise()}<v>1</v></c></row>"""),
            "inside a value" => package.WithSheet("Sheet1", $"""<row r="1"><c r="A1" t="str"><v>{TextNodes()}</v></c></row>"""),
            "between shared strings" => package.WithSheet("Sheet1", """<row r="1"><c r="A1" t="s"><v>0</v></c></row>""").WithSharedStrings(Noise() + "<si><t>a</t></si>"),
            _ => package.WithSheet("Sheet1", """<row r="1"><c r="A1" t="s"><v>0</v></c></row>""").WithSharedStrings($"<si>{Noise()}<t>a</t></si>"),
        };

        return (package.Build(), where.Contains("shared", StringComparison.Ordinal) ? "xl/sharedStrings.xml" : "xl/worksheets/sheet1.xml");
    }

    [Theory]
    [MemberData(nameof(RowTimeMarkup))]
    public void ARowReadStopsInsideMarkupOfCountlessNodes(string where)
    {
        (byte[] package, string part) = RowTimeWorkbook(where);
        long partBytes = CompressedLength(package, part);

        using (XlsxCursor whole = new(new MemoryStream(package, writable: false), cancellationToken: TestContext.Current.CancellationToken))
        {
            Assert.True(whole.ReadRow(TestContext.Current.CancellationToken));
        }

        using CancellationTokenSource source = new();
        using CancellingStream stream = new(package, source);
        using XlsxCursor cursor = new(stream, cancellationToken: TestContext.Current.CancellationToken);
        stream.CancelAfter(4 * 1024);

        Assert.Throws<OperationCanceledException>(() => cursor.ReadRow(source.Token));
        Assert.True(stream.Served < (4 * 1024) + (partBytes / 2), $"{stream.Served} bytes were read of a {partBytes}-byte part before the cancellation was seen");
    }
}
