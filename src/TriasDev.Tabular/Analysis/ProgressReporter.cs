namespace TriasDev.Tabular;

/// <summary>Counts data rows across sheets and tells the caller on the stride it asked for.</summary>
/// <remarks>
/// A report goes out once at least <c>interval</c> rows have passed since the last one and the
/// read fraction has moved by <c>step</c>. Past the interval the fraction is looked at every tenth
/// of it rather than on every row. The row path is one null check when nobody is listening.
/// </remarks>
internal sealed class ProgressReporter(
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
        // Counted as a long across the sheets of an archive, reported as the int every other row count is.
        RowsRead = (int)Math.Min(_rows, int.MaxValue),
        Fraction = fraction,
        IsComplete = isComplete,
    };
}
