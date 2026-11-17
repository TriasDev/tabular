using System.IO.Compression;
using System.Text;

using TriasDev.Tabular.Ods;
using TriasDev.Tabular.Tests.Fixtures;

using Xunit;

namespace TriasDev.Tabular.Tests.Ods;

/// <summary>
/// Every loop that scans the content part checks its token as it goes, not only before it starts: a
/// hostile part of countless nodes this reader wants nothing from — or of one node that runs for
/// megabytes — is stopped where the cancellation lands, rather than read to its end first.
/// </summary>
/// <remarks>
/// As in <see cref="Xlsx.XlsxCancellationLoopTests"/>, the noise is incompressible — random digits in
/// attributes or a comment — so the part is still being inflated from the stream while its loop runs,
/// and how far the stream was read when the cancellation surfaced tells a check inside the loop from
/// one after it: past the loop, the whole part is read.
/// </remarks>
public sealed class OdsCancellationLoopTests
{
    /// <summary>Enough nodes that the part's compressed bytes far exceed one stride of 4,096 nodes.</summary>
    private const int Nodes = 60_000;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static string Text(string text) => $"<table:table-cell office:value-type=\"string\"><text:p>{text}</text:p></table:table-cell>";

    private static string Row(string cells) => $"<table:table-row>{cells}</table:table-row>";

    private static string Noise()
    {
        Random random = new(151);
        StringBuilder xml = new(Nodes * 28);

        for (int i = 0; i < Nodes; i++)
        {
            xml.Append("<z q=\"").Append(random.NextInt64()).Append("\"/>");
        }

        return xml.ToString();
    }

    /// <summary>One comment of as many random digits as the noise holds: a single node of megabytes.</summary>
    private static string LongComment()
    {
        Random random = new(151);
        StringBuilder xml = new("<!--", Nodes * 20);

        for (int i = 0; i < Nodes; i++)
        {
            xml.Append(random.NextInt64());
        }

        return xml.Append("-->").ToString();
    }

    private static string Content(string body) =>
        $"""<?xml version="1.0" encoding="UTF-8"?><office:document-content {OdsPackage.Namespaces}office:version="1.3"><office:body><office:spreadsheet>{body}</office:spreadsheet></office:body></office:document-content>""";

    private static long CompressedLength(byte[] package)
    {
        using ZipArchive zip = new(new MemoryStream(package, writable: false), ZipArchiveMode.Read);
        return zip.GetEntry("content.xml")!.CompressedLength;
    }

    [Fact]
    public void RefusesToStartAReadWithATokenAlreadyCancelled()
    {
        using OdsCursor cursor = new(new MemoryStream(new OdsPackage().WithTable("S", Row(Text("a"))).Build(), writable: false), cancellationToken: Token);
        using CancellationTokenSource source = new();
        source.Cancel();

        Assert.Throws<OperationCanceledException>(() => cursor.ReadRow(source.Token));
    }

    public static TheoryData<string> OpenTimeMarkup => ["a comment in UTF-8", "nodes in UTF-8", "nodes in UTF-16"];

    /// <summary>
    /// Opening lists the sheets in one pass over the whole part: by its bytes in UTF-8, where a single
    /// comment is one step of the pass however long it runs, and tokenized in UTF-16.
    /// </summary>
    [Theory]
    [MemberData(nameof(OpenTimeMarkup))]
    public void OpeningStopsInsideAContentPartOfCountlessNodesOrOneEndlessOne(string markup)
    {
        string noise = markup.StartsWith("a comment", StringComparison.Ordinal) ? LongComment() : Noise();
        OdsPackage spreadsheet = new OdsPackage().WithRawContent(Content(noise + $"""<table:table table:name="S">{Row(Text("a"))}</table:table>"""));
        byte[] package = (markup.EndsWith("UTF-16", StringComparison.Ordinal) ? spreadsheet.WithContentEncoding(Encoding.Unicode) : spreadsheet).Build();
        long partBytes = CompressedLength(package);

        // Sound before anything cancels: the noise is markup the reader may ignore.
        using (OdsCursor whole = new(new MemoryStream(package, writable: false), cancellationToken: Token))
        {
            Assert.True(whole.ReadRow(Token));
        }

        using CancellationTokenSource source = new();
        using CancellingStream stream = new(package, source);
        stream.CancelAfter(16 * 1024);

        Assert.Throws<OperationCanceledException>(() => new OdsCursor(stream, cancellationToken: source.Token));
        Assert.True(stream.Served < (16 * 1024) + (partBytes / 2), $"{stream.Served} bytes were read of a {partBytes}-byte part before the cancellation was seen");
    }

    public static TheoryData<string> RowTimeMarkup => ["between rows", "inside a row", "inside a text cell", "inside a typed cell"];

    [Theory]
    [MemberData(nameof(RowTimeMarkup))]
    public void ARowReadStopsInsideMarkupOfCountlessNodes(string where)
    {
        string rows = where switch
        {
            "between rows" => Noise() + Row(Text("a")),
            "inside a row" => Row(Noise() + Text("a")),
            "inside a text cell" => Row($"<table:table-cell office:value-type=\"string\"><text:p>{Noise()}a</text:p></table:table-cell>"),
            _ => Row($"<table:table-cell office:value-type=\"float\" office:value=\"1\">{Noise()}<text:p>1</text:p></table:table-cell>"),
        };
        byte[] package = new OdsPackage().WithTable("S", rows).Build();
        long partBytes = CompressedLength(package);

        using (OdsCursor whole = new(new MemoryStream(package, writable: false), cancellationToken: Token))
        {
            Assert.True(whole.ReadRow(Token));
        }

        using CancellationTokenSource source = new();
        using CancellingStream stream = new(package, source);
        using OdsCursor cursor = new(stream, cancellationToken: Token);
        stream.CancelAfter(4 * 1024);

        Assert.Throws<OperationCanceledException>(() => cursor.ReadRow(source.Token));
        Assert.True(stream.Served < (4 * 1024) + (partBytes / 2), $"{stream.Served} bytes were read of a {partBytes}-byte part before the cancellation was seen");
    }
}
