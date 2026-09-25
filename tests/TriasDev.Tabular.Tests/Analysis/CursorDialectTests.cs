using System.Text;

using TriasDev.Tabular.Csv;

using Xunit;

namespace TriasDev.Tabular.Tests.Analysis;

/// <summary>
/// The dialect travels through the interface, so a cursor that wraps another keeps it.
/// </summary>
public sealed class CursorDialectTests
{
    /// <summary>A decorator of the kind a host writes — to log, to count, to throttle.</summary>
    private sealed class Wrapped(ITabularCursor inner) : ITabularCursor
    {
        public TabularFormat Format => inner.Format;

        public IReadOnlyList<SheetInfo> Sheets => inner.Sheets;

        public int CurrentSheetIndex => inner.CurrentSheetIndex;

        public ReadOnlySpan<RawCell> CurrentRow => inner.CurrentRow;

        public int CurrentRowNumber => inner.CurrentRowNumber;

        public CursorDiagnostics Diagnostics => inner.Diagnostics;

        public CsvDialect? Dialect => inner.Dialect;

        public bool MoveToSheet(int index) => inner.MoveToSheet(index);

        public bool ReadRow(CancellationToken cancellationToken = default) => inner.ReadRow(cancellationToken);

        public void Dispose() => inner.Dispose();
    }

    [Fact]
    public void AWrappedCsvCursorStillReportsItsDialect()
    {
        // The analyzer used to cast to CsvCursor, so a wrapper made the dialect silently null while
        // the format still said csv.
        using Wrapped cursor = new(new CsvCursor(new MemoryStream(Encoding.UTF8.GetBytes("a;b\n1;2\n")), "t.csv"));

        FileProfile profile = new TabularAnalyzer().Analyze(cursor, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(';', profile.Sheets[0].Dialect?.Delimiter);
    }
}
