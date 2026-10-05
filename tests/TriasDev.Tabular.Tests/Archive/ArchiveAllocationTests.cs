using TriasDev.Tabular.Archive;
using TriasDev.Tabular.Tests.Fixtures;

using Xunit;

namespace TriasDev.Tabular.Tests.Archive;

/// <summary>What a workbook inside an archive costs in memory.</summary>
[Collection(AllocationMeasurementCollection.Name)]
public sealed class ArchiveAllocationTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static long OpenAndRead(byte[] archive)
    {
        long before = GC.GetAllocatedBytesForCurrentThread();

        using (ArchiveCursor cursor = new(new MemoryStream(archive, writable: false), null, Token))
        {
            while (cursor.ReadRow(Token))
            {
                // Reading is the point.
            }
        }

        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    [Fact]
    public void ASmallWorkbookInsideAnArchiveIsNotCopiedIntoAMegabytePiece()
    {
        // The workbook is deflated, so it is copied into memory twice: once to list its sheets, once
        // to read them. A copy that filled its first piece exactly to the declared size used to
        // allocate a whole megabyte more just to learn that the file had ended — two megabytes in all,
        // above the 1.1 MB that opening and reading cost besides (3.2 MB then, 1.1 MB since).
        byte[] workbook = new XlsxPackage()
            .WithSheet("S", """<row r="1"><c t="inlineStr"><is><t>a</t></is></c></row>""")
            .Build();
        byte[] archive = new ZipArchiveBuilder().With("book.xlsx", workbook).Build();

        OpenAndRead(archive);   // warm-up: the JIT and the pools allocate once
        long allocated = OpenAndRead(archive);

        Assert.True(allocated < 2 * ChunkedBuffer.PieceSize, $"allocated {allocated} bytes");
    }
}
