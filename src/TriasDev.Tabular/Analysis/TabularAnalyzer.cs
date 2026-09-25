using TriasDev.Tabular.Abstractions;
using TriasDev.Tabular.Csv;

namespace TriasDev.Tabular.Analysis;

/// <summary>
/// Reads a file through and reports what is in it.
/// </summary>
/// <remarks>
/// <para>
/// Stateless and stream-based. It keeps nothing between calls, so analysing a file and later
/// extracting from it are two independent reads of it; a caller that wants to keep the profile while
/// a user builds a mapping stores it itself.
/// </para>
/// <para>
/// The header is the sheet's first row unless <see cref="AnalysisOptions.HeaderRowIndex"/> says
/// otherwise, and nothing goes looking for it: a guess that is right most of the time produces a
/// wrong answer nobody checks. A file whose header is not the first row is analysed again under the
/// right number, because everything above the header was measured as data — and a profile carries
/// the row it used, so a mapping naming a different one can be told that its facts do not apply.
/// </para>
/// </remarks>
public sealed class TabularAnalyzer(AnalysisOptions? options = null)
{
    private readonly AnalysisOptions _options = options ?? AnalysisOptions.Default;

    /// <summary>Reads every sheet of an open cursor and profiles every column of each.</summary>
    /// <param name="cursor">A cursor positioned at the start of the file.</param>
    /// <param name="cancellationToken">Stops the pass.</param>
    public FileProfile Analyze(ITabularCursor cursor, CancellationToken cancellationToken = default) =>
        Analyze(cursor, progress: null, cancellationToken);

    /// <summary>Analyses every sheet of a file, reporting how far it has got as it goes.</summary>
    /// <param name="cursor">The file, opened.</param>
    /// <param name="progress">
    /// Told every <see cref="AnalysisOptions.ProgressInterval"/> data rows and once when done, on the
    /// analysing thread; null to report nothing. <see cref="Progress{T}"/> posts each report to the
    /// context it was created on, which suits a UI; an implementation of its own receives them
    /// synchronously.
    /// </param>
    /// <param name="cancellationToken">Stops the analysis, including inside a single read.</param>
    public FileProfile Analyze(
        ITabularCursor cursor,
        IProgress<AnalysisProgress>? progress,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(cursor);

        ProgressReporter reporter = new(progress, cursor, _options.ProgressInterval, _options.ProgressStep);

        // One budget for the file, not one per column: the alternative multiplies the ceiling by the
        // column count, and a column holding few distinct values would reserve room it never uses.
        DistinctBudget budget = new(_options.DistinctTrackingBudget);

        List<SheetProfile> sheets = [];

        foreach (SheetInfo sheet in cursor.Sheets)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!cursor.MoveToSheet(sheet.Index))
            {
                continue;
            }

            sheets.Add(AnalyzeSheet(cursor, sheet, budget, reporter, cancellationToken));
        }

        reporter.Complete();

        return new FileProfile
        {
            Format = cursor.Format,
            Dialect = (cursor as CsvCursor)?.Dialect,
            Sheets = sheets,
            Diagnostics = cursor.Diagnostics,
        };
    }

    private SheetProfile AnalyzeSheet(
        ITabularCursor cursor,
        SheetInfo sheet,
        DistinctBudget budget,
        ProgressReporter reporter,
        CancellationToken cancellationToken)
    {
        List<ColumnProfiler> profilers = [];
        int rowCount = 0;
        bool headerRead = false;
        int rowsSeen = 0;

        // Handed down, not only checked here. The check between rows cannot interrupt a single read
        // that is loading a shared string table, which is the one that takes the time.
        while (cursor.ReadRow(cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!headerRead)
            {
                // Everything above the header is skipped, not measured. A title line counted as data
                // puts its own text into the column's lengths and types, and the mapping is then
                // judged against a value no row holds.
                if (rowsSeen++ < _options.HeaderRowIndex)
                {
                    continue;
                }

                headerRead = true;
                ReadHeader(cursor.CurrentRow, profilers, budget);
                continue;
            }

            ReadOnlySpan<RawCell> row = cursor.CurrentRow;

            // A row with nothing in it is not a row. A spreadsheet accumulates them below its data as
            // a matter of course, and counting them makes every fact about the file describe the
            // padding as much as the content: a full column reads as mostly empty, a required field
            // looks half unfilled, and a rule about the values is judged against rows holding none.
            //
            // Extraction already skips them, so counting them here made the profile disagree with the
            // run it exists to predict — the more expensive half of the mistake.
            if (IsBlank(row))
            {
                continue;
            }

            rowCount++;
            AcceptRow(row, cursor.CurrentRowNumber, rowCount, profilers, budget);
            reporter.Row(sheet);
        }

        return BuildProfile(sheet, rowCount, profilers);
    }

    /// <summary>Hands one data row's cells to the column profilers, adding columns it is the first to reach.</summary>
    /// <param name="rowCount">The data rows counted so far, this one included.</param>
    private void AcceptRow(
        ReadOnlySpan<RawCell> row,
        int rowNumber,
        int rowCount,
        List<ColumnProfiler> profilers,
        DistinctBudget budget)
    {
        // A row may be wider than the header. Its extra values are still values, and a column that
        // exists only below the header is worth reporting rather than dropping.
        while (profilers.Count < row.Length)
        {
            ColumnProfiler late = new(profilers.Count, string.Empty, budget, _options);

            // Given the rows it missed, as empties. Without this its counts do not add up to the
            // sheet's — a column appearing in the last of a thousand rows reported one value and no
            // empties — and every verdict derived from an empty count silently inherited that: "no
            // row leaves this column empty" was read as "every row carries a value".
            late.AcceptEmpties(rowCount - 1);

            profilers.Add(late);
        }

        for (int i = 0; i < profilers.Count; i++)
        {
            profilers[i].Accept(i < row.Length ? row[i] : RawCell.Empty, rowNumber);
        }
    }

    private SheetProfile BuildProfile(SheetInfo sheet, int rowCount, List<ColumnProfiler> profilers)
    {
        List<ColumnProfile> columns = [];

        foreach (ColumnProfiler profiler in profilers)
        {
            ColumnFacts facts = profiler.ToFacts();
            columns.Add(new ColumnProfile
            {
                Facts = facts,
                Hypotheses = HypothesisBuilder.Build(facts, _options.MinimumHypothesisConfidence),
            });
        }

        return new SheetProfile
        {
            Index = sheet.Index,
            Name = sheet.Name,
            RowCount = rowCount,
            Columns = columns,
            HeaderRowIndex = _options.HeaderRowIndex,
        };
    }

    /// <summary>Whether the row holds no value in any column.</summary>
    private static bool IsBlank(ReadOnlySpan<RawCell> row)
    {
        for (int i = 0; i < row.Length; i++)
        {
            if (!row[i].IsEmpty)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Takes the header row as the column headers.
    /// </summary>
    /// <remarks>
    /// A header cell may be empty and two may be identical, both of which real files contain. Neither
    /// is corrected: the column keeps its position, which is what a mapping addresses it by.
    /// </remarks>
    private void ReadHeader(ReadOnlySpan<RawCell> header, List<ColumnProfiler> profilers, DistinctBudget budget)
    {
        for (int i = 0; i < header.Length; i++)
        {
            profilers.Add(new ColumnProfiler(i, header[i].AsText() ?? string.Empty, budget, _options));
        }
    }

    /// <summary>Counts data rows across sheets and tells the caller on the stride it asked for.</summary>
    /// <remarks>
    /// A report goes out once at least <c>interval</c> rows have passed since the last one and the
    /// read fraction has moved by <c>step</c>. Past the interval the fraction is looked at every tenth
    /// of it rather than on every row. The row path is one null check when nobody is listening.
    /// </remarks>
    private sealed class ProgressReporter(
        IProgress<AnalysisProgress>? progress,
        ITabularCursor cursor,
        int interval,
        double step)
    {
        private readonly int _interval = Math.Max(1, interval);
        private readonly int _checkEvery = Math.Max(1, Math.Max(1, interval) / 10);
        private long _rows;
        private int _sinceReport;
        private double _lastFraction;
        private SheetInfo? _sheet;

        public void Row(SheetInfo sheet)
        {
            if (progress is null)
            {
                return;
            }

            _sheet = sheet;
            _rows++;

            if (++_sinceReport < _interval || (_sinceReport - _interval) % _checkEvery != 0)
            {
                return;
            }

            double? fraction = cursor.ReadFraction;

            // Without a size there is no percentage to step by; the interval alone decides.
            if (step > 0 && fraction is { } known && known - _lastFraction < step)
            {
                return;
            }

            _lastFraction = fraction ?? _lastFraction;
            _sinceReport = 0;
            progress.Report(Build(fraction, isComplete: false));
        }

        public void Complete()
        {
            if (progress is null)
            {
                return;
            }

            _sheet ??= cursor.Sheets.Count > 0 ? cursor.Sheets[^1] : null;
            progress.Report(Build(1d, isComplete: true));
        }

        private AnalysisProgress Build(double? fraction, bool isComplete) => new()
        {
            SheetIndex = _sheet?.Index ?? 0,
            SheetName = _sheet?.Name ?? string.Empty,
            SheetCount = cursor.Sheets.Count,
            RowsRead = _rows,
            Fraction = fraction,
            IsComplete = isComplete,
        };
    }
}
