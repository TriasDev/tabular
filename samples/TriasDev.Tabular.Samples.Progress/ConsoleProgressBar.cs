namespace TriasDev.Tabular.Samples.Progress;

/// <summary>Draws analysis progress on one console line.</summary>
/// <remarks>
/// Its own <see cref="IProgress{T}"/> rather than <see cref="Progress{T}"/>: that one posts each report
/// to the thread pool, so in a console the reports arrive out of order and after the analysis ended.
/// This runs on the analysing thread, in order — which is what a console, or a test, wants. A UI
/// with a synchronisation context wants <see cref="Progress{T}"/> instead.
/// </remarks>
internal sealed class ConsoleProgressBar : IProgress<AnalysisProgress>
{
    private const int Width = 40;

    public void Report(AnalysisProgress value)
    {
        double fraction = value.IsComplete ? 1 : value.Fraction ?? 0;
        int filled = (int)(fraction * Width);

        Console.Write($"\r[{new string('#', filled)}{new string('.', Width - filled)}] {fraction,4:P0}  {value.RowsRead:N0} rows");
    }
}
