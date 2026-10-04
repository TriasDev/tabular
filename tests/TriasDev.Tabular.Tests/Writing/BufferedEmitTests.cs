using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

using Xunit;

namespace TriasDev.Tabular.Tests.Writing;

/// <summary>
/// The sheet writers collect rows in one buffer and hand it to the deflate stream in large writes. The
/// bytes of every part, once inflated, must be those the per-row writes produced: the expected digests
/// below were taken from the writers before the buffering, on the same data. (Deflate block boundaries
/// may differ between the two, so the compressed bytes are not compared.)
/// </summary>
public sealed class BufferedEmitTests
{
    private const string XlsxDigests =
        "[Content_Types].xml=DE7B6AD03BA57780707E68A38D9B57CB5C95F8CDE27469D3AED927A03AA5B93E;" +
        "_rels/.rels=F2F3948A1B6F661F2A5169A86B7029166ACD4B79D7ABFD37FA2B646FE302BFD6;" +
        "xl/_rels/workbook.xml.rels=2372344717CC04FDF4983D6B5A2A815515CB851C5FCCA40DE351FF35DFDD63A2;" +
        "xl/styles.xml=27B976567DC15ACC3E267A2CE54F27D1F9763687CA3DDEF0A90318D84017D54B;" +
        "xl/workbook.xml=74D6691709AE82EF5F75AA69395F7491DC706F0E33483741705445ACF49FBF9E;" +
        "xl/worksheets/sheet1.xml=A6B185F0AE7FADCC00878A5EDBF466080EBAA19DCA198E1102B4ED65A9624389;" +
        "xl/worksheets/sheet2.xml=A6B185F0AE7FADCC00878A5EDBF466080EBAA19DCA198E1102B4ED65A9624389;";

    private const string OdsDigests =
        "META-INF/manifest.xml=ACAD1AC2F8F631AD9AB0434C935A9797F0836422569EA36CABFEA7810AC731BD;" +
        "content.xml=A198EAFBC6939739FA04748BA505A12290A922F79C4524CA1AA5634D8CF97202;" +
        "mimetype=252A8B94D7A7231935A1E64B941040221EBE10B640BA182F6780FBB89BE1D8B1;" +
        "settings.xml=F9CB132137542FA0DCFAE3D4A4ABD54E8BEEB87129276F17D7526066CA08CB27;" +
        "styles.xml=E12B0A25E90BFD1B8A9F3F485345DA9D9E2487C1B82EEE115F186C0085416AE0;";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static async Task<string> Digests(TabularFormat format)
    {
        using MemoryStream target = new();
        await using (TabularWriter writer = TabularWriter.Create(target, format, new TabularWriterOptions { LeaveOpen = true }))
        {
            WriteColumn[] columns = [new("Id"), new("Name"), new("Amount"), new("When"), new("Flag"), new("Note")];
            string longNote = new string('é', 20_000) + "<&>\r\n" + new string('x', 9_000);
            CellStyle bold = new() { Fill = CellColor.FromRgb(0xFFEB84) };
            StyleId boldId = writer.Style(bold);
            string[] sheets = ["First", "Second"];

            foreach (string sheet in sheets)
            {
                writer.BeginSheet(sheet, columns, new SheetOptions { FreezeRows = 1, AutoFilter = true });

                for (int r = 0; r < 10_000; r++)
                {
                    writer.BeginRow();
                    writer.Write(r);
                    writer.Write($"Name {r % 97} Grüße 数据 & <{r}>", r % 7 == 0 ? boldId : default);
                    writer.Write((r % 1000) * 0.25);
                    writer.Write(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Unspecified).AddDays(r % 365));
                    writer.Write(r % 2 == 0);

                    if (r % 2500 == 0)
                    {
                        writer.Write(longNote);
                    }
                    else
                    {
                        writer.WriteEmpty();
                    }

                    writer.EndRow();
                }
            }

            await writer.CompleteAsync(Token);
        }

        return PartDigests(target.ToArray());
    }

    private static string PartDigests(byte[] file)
    {
        using ZipArchive zip = new(new MemoryStream(file), ZipArchiveMode.Read);
        StringBuilder text = new();

        foreach (ZipArchiveEntry entry in zip.Entries.OrderBy(e => e.FullName, StringComparer.Ordinal))
        {
            using Stream inflated = entry.Open();
            text.Append(entry.FullName).Append('=').Append(Convert.ToHexString(SHA256.HashData(inflated))).Append(';');
        }

        return text.ToString();
    }

    [Fact]
    public async Task XlsxPartsInflateToTheBytesOfTheUnbufferedWriter() => Assert.Equal(XlsxDigests, await Digests(TabularFormat.Xlsx));

    [Fact]
    public async Task OdsPartsInflateToTheBytesOfTheUnbufferedWriter() => Assert.Equal(OdsDigests, await Digests(TabularFormat.Ods));

    [Fact]
    public void CollectsSmallWritesAndHandsThemOverTogether()
    {
        using RecordingStream sink = new();
        RowBytes bytes = new();
        bytes.Begin(sink);

        for (int i = 0; i < 10; i++)
        {
            "<row/>"u8.CopyTo(bytes.GetSpan(6));
            bytes.Advance(6);
        }

        Assert.Empty(sink.Writes);
        bytes.Flush();
        Assert.Equal([60], sink.Writes);
    }

    [Fact]
    public void WritesWhatIsWaitingWhenTheNextRequestWouldNotFit()
    {
        using RecordingStream sink = new();
        RowBytes bytes = new();
        bytes.Begin(sink);
        bytes.GetSpan(RowBytes.BufferSize - 10);
        bytes.Advance(RowBytes.BufferSize - 10);

        bytes.GetSpan(20);

        Assert.Equal([RowBytes.BufferSize - 10], sink.Writes);
        Assert.Equal(0, bytes.Count);
    }

    [Fact]
    public void ABufferGrownForAHugeRequestIsNotKept()
    {
        using RecordingStream sink = new();
        RowBytes bytes = new();
        bytes.Begin(sink);

        Assert.True(bytes.GetSpan(RowBytes.BufferSize * 4).Length >= RowBytes.BufferSize * 4);
        bytes.Advance(RowBytes.BufferSize * 4);
        Assert.True(bytes.Capacity > RowBytes.BufferSize);

        bytes.Flush();

        Assert.Equal(RowBytes.BufferSize, bytes.Capacity);
        Assert.Equal([RowBytes.BufferSize * 4], sink.Writes);
    }

    private sealed class RecordingStream : Stream
    {
        public List<int> Writes { get; } = [];

        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => Writes.Add(count);
    }
}
