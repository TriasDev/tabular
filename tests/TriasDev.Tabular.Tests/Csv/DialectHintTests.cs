using System.Text;

using TriasDev.Tabular.Tests.Fixtures;

using Xunit;

namespace TriasDev.Tabular.Tests.Csv;

/// <summary>
/// Each part of the dialect a caller states wins and is reported as stated; the rest is still
/// detected. A stated encoding wins over a byte order mark, and a mark of that encoding is not content.
/// </summary>
public sealed class DialectHintTests
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private static ITabularCursor Open(byte[] file, CsvCursorOptions csv, string name = "t.csv") =>
        TabularFile.Open(new MemoryStream(file, writable: false), name, new TabularOpenOptions { Csv = csv },
            TestContext.Current.CancellationToken);

    private static string?[] FirstRow(ITabularCursor cursor)
    {
        Assert.True(cursor.ReadRow(TestContext.Current.CancellationToken));
        return [.. cursor.CurrentRow.ToArray().Select(c => c.AsText())];
    }

    [Fact]
    public void AStatedDelimiterWinsAndTheEncodingIsStillDetected()
    {
        // Detection would pick the semicolon, and nothing states the encoding: the umlauts say UTF-8.
        using ITabularCursor cursor = Open(Utf8NoBom.GetBytes("Straße;Köln,x\nä;ö,y\n"), new CsvCursorOptions { Delimiter = ',' });

        Assert.Equal(["Straße;Köln", "x"], FirstRow(cursor));
        Assert.Equal(',', cursor.Dialect!.Delimiter);
        Assert.Equal(DialectSource.Specified, cursor.Dialect.DelimiterSource);
        Assert.Equal("utf-8", cursor.Dialect.Encoding.WebName);
        Assert.Equal(DialectSource.Detected, cursor.Dialect.EncodingSource);
    }

    [Fact]
    public void AStatedEncodingWinsAndTheDelimiterIsStillDetected()
    {
        // Valid UTF-8, which detection would choose; the caller says Latin-1.
        using ITabularCursor cursor = Open(Utf8NoBom.GetBytes("ä,b\nc,d\n"), new CsvCursorOptions { Encoding = Encoding.Latin1 });

        Assert.Equal(["Ã¤", "b"], FirstRow(cursor));
        Assert.Equal(Encoding.Latin1.WebName, cursor.Dialect!.Encoding.WebName);
        Assert.Equal(DialectSource.Specified, cursor.Dialect.EncodingSource);
        Assert.Equal(',', cursor.Dialect.Delimiter);
        Assert.Equal(DialectSource.Detected, cursor.Dialect.DelimiterSource);
    }

    [Fact]
    public void AStatedQuoteIsReadAndHonouredByDelimiterDetection()
    {
        // Ignoring the apostrophes, the comma is the commoner character on every line; honouring
        // them, it stands only inside quoted fields and the semicolon divides the records.
        byte[] file = Utf8NoBom.GetBytes("'a,b,c';x\n'd,e,f';y\n'g,h,i';z\n");

        using ITabularCursor cursor = Open(file, new CsvCursorOptions { Quote = '\'' });

        Assert.Equal(["a,b,c", "x"], FirstRow(cursor));
        Assert.Equal(';', cursor.Dialect!.Delimiter);
        Assert.Equal(DialectSource.Detected, cursor.Dialect.DelimiterSource);
        Assert.Equal('\'', cursor.Dialect.Quote);
    }

    [Fact]
    public void NothingStatedIsReportedAsDetected()
    {
        using ITabularCursor cursor = Open(Utf8NoBom.GetBytes("a;b\nc;d\n"), CsvCursorOptions.Default);

        Assert.Equal(DialectSource.Detected, cursor.Dialect!.DelimiterSource);
        Assert.Equal(DialectSource.Detected, cursor.Dialect.EncodingSource);
        Assert.Equal('"', cursor.Dialect.Quote);
    }

    public static TheoryData<byte[], Encoding, bool> MarksOfTheStatedEncoding => new()
    {
        // The file's mark, the stated encoding, and whether the delimiter is stated too (then the
        // head is not read at all).
        { [0xEF, 0xBB, 0xBF], new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), false },
        { [0xEF, 0xBB, 0xBF], new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), true },
        { [0xEF, 0xBB, 0xBF], Encoding.UTF8, true },
        { [0xFF, 0xFE], new UnicodeEncoding(bigEndian: false, byteOrderMark: false), true },
        { [0xFF, 0xFE], Encoding.Unicode, false },
        { [0xFE, 0xFF], new UnicodeEncoding(bigEndian: true, byteOrderMark: false), true },
    };

    [Theory]
    [MemberData(nameof(MarksOfTheStatedEncoding))]
    public void AMarkOfTheStatedEncodingIsSkippedNotReadAsContent(byte[] mark, Encoding encoding, bool delimiterStated)
    {
        byte[] file = [.. mark, .. encoding.GetBytes("name;x\na;b\n")];

        using ITabularCursor cursor = Open(file, new CsvCursorOptions { Encoding = encoding, Delimiter = delimiterStated ? ';' : null });

        Assert.Equal(["name", "x"], FirstRow(cursor));
        Assert.Equal(DialectSource.Specified, cursor.Dialect!.EncodingSource);
    }

    [Fact]
    public void AStatedEncodingWinsOverTheMarkOfAnother()
    {
        // A UTF-8 mark on a file the caller says is Latin-1: the caller wins, and the mark's three
        // bytes are three Latin-1 characters.
        byte[] file = [0xEF, 0xBB, 0xBF, .. Encoding.Latin1.GetBytes("näme;x\na;b\n")];

        using ITabularCursor cursor = Open(file, new CsvCursorOptions { Encoding = Encoding.Latin1 });

        Assert.Equal(["ï»¿näme", "x"], FirstRow(cursor));
        Assert.Equal(Encoding.Latin1.WebName, cursor.Dialect!.Encoding.WebName);
        Assert.Equal(DialectSource.Specified, cursor.Dialect.EncodingSource);
    }

    [Fact]
    public void TheHintsApplyToACsvFileInAnArchive()
    {
        byte[] zip = new ZipArchiveBuilder()
            .With("orders.csv", Utf8NoBom.GetBytes("a;b,c\nd;e,f\n"))
            .Build();

        using ITabularCursor cursor = Open(zip, new CsvCursorOptions { Delimiter = ',' }, "orders.zip");

        Assert.Equal(["a;b", "c"], FirstRow(cursor));
        Assert.Equal(DialectSource.Specified, cursor.Dialect!.DelimiterSource);
        Assert.Equal(DialectSource.Detected, cursor.Dialect.EncodingSource);
    }

    [Fact]
    public void TheHintsApplyToAGzipFile()
    {
        byte[] gzip = GzipFile.Of([0xEF, 0xBB, 0xBF, .. Utf8NoBom.GetBytes("a;b,c\nd;e,f\n")]);

        using ITabularCursor cursor = Open(gzip, new CsvCursorOptions { Delimiter = ',', Encoding = Utf8NoBom }, "orders.csv.gz");

        Assert.Equal(["a;b", "c"], FirstRow(cursor));
        Assert.Equal(DialectSource.Specified, cursor.Dialect!.DelimiterSource);
        Assert.Equal(DialectSource.Specified, cursor.Dialect.EncodingSource);
    }

    public static TheoryData<string, byte[], bool> NotCsvAtAll => new()
    {
        // A head that is not csv, and whether the delimiter is stated beside the encoding.
        { "xls", [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1, .. new byte[504]], false },
        { "xls", [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1, .. new byte[504]], true },
        { "png", [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0x0D, (byte)'I', (byte)'H', (byte)'D', (byte)'R', 0, 0, 0, 1, 0, 0, 0, 1, 8, 6, 0, 0, 0, 0x1F, 0x15, 0xC4], false },
        { "png", [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0x0D, (byte)'I', (byte)'H', (byte)'D', (byte)'R', 0, 0, 0, 1, 0, 0, 0, 1, 8, 6, 0, 0, 0, 0x1F, 0x15, 0xC4], true },
        { "xml", Utf8NoBom.GetBytes("<?xml version=\"1.0\"?><office:document/>"), true },
    };

    [Theory]
    [MemberData(nameof(NotCsvAtAll))]
    public void AStatedDialectStillRefusesAFileThatIsNotCsv(string kind, byte[] head, bool delimiterStated)
    {
        CsvCursorOptions csv = new() { Encoding = Encoding.Latin1, Delimiter = delimiterStated ? ';' : null };

        TabularFormatException error = Assert.Throws<TabularFormatException>(() => Open(head, csv, "upload." + kind));

        Assert.Equal(ErrorCodes.Format.Unsupported, error.Code);
    }

    [Fact]
    public void AStatedDialectStillRefusesABinaryGzipFile()
    {
        byte[] gzip = GzipFile.Of([0x89, (byte)'P', (byte)'N', (byte)'G', 0, 0, 0, 0x0D, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0, 0]);

        TabularFormatException error = Assert.Throws<TabularFormatException>(
            () => Open(gzip, new CsvCursorOptions { Encoding = Encoding.Latin1, Delimiter = ';' }, "upload.gz"));

        Assert.Equal(ErrorCodes.Format.Unsupported, error.Code);
    }

    [Fact]
    public void AStatedWideEncodingKeepsItsNulBytes()
    {
        // UTF-16 without its mark writes a NUL beside every ASCII character: text, not binary.
        Encoding utf16 = new UnicodeEncoding(bigEndian: false, byteOrderMark: false);

        using ITabularCursor cursor = Open(utf16.GetBytes("name;x\na;b\nc;d\n"), new CsvCursorOptions { Encoding = utf16 });

        Assert.Equal(["name", "x"], FirstRow(cursor));
    }

    [Fact]
    public void OnlyAReaderMakesADialect() =>
        Assert.Empty(typeof(CsvDialect).GetConstructors());
}
