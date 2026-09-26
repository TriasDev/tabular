using System.Text;

using TriasDev.Tabular.Archive;
using TriasDev.Tabular.Csv;

using Xunit;

namespace TriasDev.Tabular.Tests.Archive;

/// <summary>The archive's own bounds, and what a file that is no archive reports about skipped entries.</summary>
public sealed class ArchiveCursorOptionsTests
{
    [Fact]
    public void BoundsAnArchiveByDefaultAsTheGuideSays()
    {
        ArchiveCursorOptions options = ArchiveCursorOptions.Default;

        Assert.Equal(16_384, options.MaxEntries);
        Assert.Equal(8L * 1024 * 1024 * 1024, options.MaxUncompressedBytes);
        Assert.Equal(256L * 1024 * 1024, options.MaxEmbeddedWorkbookBytes);
        Assert.Same(ArchiveCursorOptions.Default, TabularOpenOptions.Default.Archive);
    }

    [Fact]
    public void ReportsNoSkippedEntriesForAFileThatIsNoArchive()
    {
        using CsvCursor cursor = new(new MemoryStream(Encoding.UTF8.GetBytes("a;b\n1;2\n"), writable: false), "t.csv");

        Assert.Empty(((ITabularCursor)cursor).SkippedEntries);
        Assert.Empty(new TabularAnalyzer().Analyze(cursor, cancellationToken: TestContext.Current.CancellationToken).SkippedEntries);
    }

    [Fact]
    public void NamesAnEntryAndWhyItWasSkipped()
    {
        SkippedEntry entry = new() { Path = "scans/invoice.pdf", Reason = SkippedEntryReason.Binary };

        Assert.Equal("scans/invoice.pdf", entry.Path);
        Assert.Equal(SkippedEntryReason.Binary, entry.Reason);
        Assert.Empty(new FileProfile { Format = TabularFormat.Zip, Sheets = [], Diagnostics = new CursorDiagnostics() }.SkippedEntries);
    }
}
