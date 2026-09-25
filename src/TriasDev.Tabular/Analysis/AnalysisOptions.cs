namespace TriasDev.Tabular.Analysis;

/// <summary>Knobs for profiling a file.</summary>
public sealed record AnalysisOptions
{
    /// <summary>The defaults.</summary>
    public static AnalysisOptions Default { get; } = new();

    /// <summary>
    /// The cultures every text value is tried under.
    /// </summary>
    /// <remarks>
    /// Three, because they cover what actually arrives: files written by German software, files
    /// written by English-speaking software, and files written by a program that used neither. A
    /// caller who knows better can say so.
    /// </remarks>
    public IReadOnlyList<string> Cultures { get; init; } = ["", "de-DE", "en-US"];

    /// <summary>
    /// How many values may be tracked for exact distinct counting across the whole file.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A budget for the file rather than for each column, because the alternative multiplies it by
    /// the column count and a seventeen-column export would then reserve seventeen times what it
    /// needs. Columns that hold few distinct values barely touch it; the one identifier column that
    /// holds millions spends most of it, which is the right distribution.
    /// </para>
    /// <para>
    /// Two million hashes is about sixteen megabytes of payload. A file of a few tens of megabytes
    /// does not reach it; it exists for files an order of magnitude larger, and as the guarantee
    /// that memory has an upper bound at all.
    /// </para>
    /// </remarks>
    public int DistinctTrackingBudget { get; init; } = 2_000_000;

    /// <summary>
    /// How many of a column's distinct values to keep, rather than only counting them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The count alone answers whether a column can identify its rows. It cannot answer whether the
    /// column holds country codes, because that question is about the values themselves — and asking
    /// it of a caller's reference set costs one lookup per <em>distinct</em> value rather than one
    /// per row. A column of codes is low-cardinality by nature: ISO 3166 has some 250 members, the
    /// ELF list of legal forms some 2,600. So the work is bounded by this number whether the file
    /// holds ten thousand rows or five million.
    /// </para>
    /// <para>
    /// Keeping them is close to free because the deduplication already happens: every value is
    /// hashed to count distinct values, so a string is retained only when that hash proves new and
    /// the cap is not yet reached. A repeated value — which is every value in a column of codes —
    /// costs nothing beyond the lookup that was already being done.
    /// </para>
    /// <para>
    /// A column that runs past the cap is not a column of codes, and the facts say so rather than
    /// leaving a partial set to be mistaken for the whole. Zero disables retention.
    /// </para>
    /// </remarks>
    public int RetainedDistinctValues { get; init; } = 1_000;

    /// <summary>
    /// The share of values a reading must account for before it is offered at all.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Without one, every reading that fits even a single value is proposed, and since a reading that
    /// claims more is ranked above one that claims less, the proposal is often nonsense. Measured on
    /// a file of ten thousand real companies: a column of twenty-character identifiers was offered as
    /// a decimal on the strength of 1% of its values, and columns of names and street addresses were
    /// offered as whole numbers on 0%.
    /// </para>
    /// <para>
    /// A half is deliberately blunt. It removes readings that fit a rounding error of the column
    /// while keeping ones that genuinely describe it — postal codes, which are numeric in most
    /// countries and not in Britain, come through at 71% and are worth offering.
    /// </para>
    /// <para>
    /// Text is exempt: it always fits, and it is the fallback rather than a competitor.
    /// </para>
    /// </remarks>
    public double MinimumHypothesisConfidence { get; init; } = 0.5;

    /// <summary>How many distinct values a column keeps frequencies for.</summary>
    public int FrequencySampleSize { get; init; } = 64;

    /// <summary>How many values with frequencies a column reports.</summary>
    public int ReportedSampleSize { get; init; } = 20;

    /// <summary>How many first values a column reports verbatim.</summary>
    public int FirstValueSampleSize { get; init; } = 10;

    /// <summary>How many located outliers a column reports per reading.</summary>
    public int OutlierSampleSize { get; init; } = 20;

    /// <summary>
    /// Which spreadsheet row holds the header, zero-based: 2 is the row a person calls row 3.
    /// </summary>
    /// <remarks>
    /// It names a row by its number, the same numbering every reported row uses — not by how many
    /// rows the file writes above it. A workbook leaves empty rows out; where the named row is one of
    /// them, the header is the first row below it that has content.
    /// </remarks>
    /// <remarks>
    /// Zero is the guess a file is analysed under before anybody has looked at it, and it is right
    /// nearly always. When it is not — a title line, a note, a blank spacer above the real header —
    /// the file has to be analysed again under the right number, because everything above the header
    /// was measured as data. A precheck cannot repair that from the outside; it can only notice that
    /// the profile and the mapping disagree.
    /// </remarks>
    public int HeaderRowIndex { get; init; }

    /// <summary>The fewest data rows between two progress reports.</summary>
    /// <remarks>
    /// A floor under <see cref="ProgressStep"/>: a percent of a twenty-thousand-row file is two
    /// hundred rows, and a hundred reports for a file read in milliseconds tell nobody anything. Where
    /// the file's size is unknown, this alone decides. Only used when a caller asks for progress.
    /// </remarks>
    public int ProgressInterval { get; init; } = 10_000;

    /// <summary>How far the read fraction must move before the next progress report.</summary>
    /// <remarks>
    /// One percent by default, so a file of five million rows reports about a hundred times rather
    /// than five hundred. Zero reports on the row interval alone.
    /// </remarks>
    public double ProgressStep { get; init; } = 0.01;
}
