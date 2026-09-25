using TriasDev.Tabular.Abstractions;

namespace TriasDev.Tabular.Analysis;

/// <summary>
/// Turns a column's measured facts into ranked readings of it.
/// </summary>
/// <remarks>
/// A pure function from facts to suggestions. It never sees a file, never reads a value, and holds
/// no state, which is what lets the whole of it be pinned as a table.
/// </remarks>
public static class HypothesisBuilder
{
    /// <summary>
    /// Ranks the readings a column will bear, most convincing first.
    /// </summary>
    /// <remarks>
    /// Text always fits and always appears, always last among equally confident readings. Ranking by
    /// confidence alone would put it first every time and drown the suggestion worth making.
    /// </remarks>
    /// <param name="facts">What was measured about the column.</param>
    /// <param name="minimumConfidence">
    /// The share of values a reading must account for before it is offered. Text is exempt.
    /// </param>
    public static IReadOnlyList<TypeHypothesis> Build(ColumnFacts facts, double minimumConfidence = 0)
    {
        ArgumentNullException.ThrowIfNull(facts);

        if (facts.NonEmptyCount == 0)
        {
            return [];
        }

        List<TypeHypothesis> hypotheses = [];

        if (DeclaredByTheFile(facts))
        {
            AddDeclared(facts, hypotheses);
        }
        else
        {
            AddParsed(facts, hypotheses);
        }

        hypotheses.RemoveAll(h => h.Confidence < minimumConfidence);

        hypotheses.Add(new TypeHypothesis
        {
            Type = ColumnType.Text,
            Confidence = 1,
            MatchedCount = facts.NonEmptyCount,
            UnmatchedCount = 0,
            Outliers = [],
        });

        // Text last, always, whatever its confidence. It fits every column by definition, so ranking
        // it by confidence puts it first the moment one value in a hundred thousand fails to parse,
        // and buries the reading worth suggesting. Its place is the fallback's place; it is not
        // competing.
        return [.. hypotheses
            .OrderBy(h => h.Type == ColumnType.Text ? 1 : 0)
            .ThenByDescending(h => h.Confidence)
            .ThenByDescending(h => Specificity(h.Type))
            .ThenBy(h => h.Culture, StringComparer.Ordinal)];
    }

    /// <summary>
    /// Whether the file stated what every value is, which a workbook does and a csv file cannot.
    /// </summary>
    /// <remarks>
    /// Where it did, no culture is involved: the number was a number before anyone read it, and
    /// offering a choice of cultures would invent a question the file already answered.
    /// </remarks>
    private static bool DeclaredByTheFile(ColumnFacts facts)
    {
        int declared = Native(facts, RawCellKind.Number)
            + Native(facts, RawCellKind.Date)
            + Native(facts, RawCellKind.Boolean);

        return declared == facts.NonEmptyCount;
    }

    private static int Native(ColumnFacts facts, RawCellKind kind) =>
        facts.NativeKinds.TryGetValue(kind, out int count) ? count : 0;

    private static void AddDeclared(ColumnFacts facts, List<TypeHypothesis> hypotheses)
    {
        CultureParseCounts counts = facts.ParseCounts[0];

        Add(hypotheses, ColumnType.Boolean, null, Native(facts, RawCellKind.Boolean), facts.NonEmptyCount, []);
        Add(hypotheses, ColumnType.Date, null, Native(facts, RawCellKind.Date), facts.NonEmptyCount, []);

        int numbers = Native(facts, RawCellKind.Number);

        if (numbers > 0)
        {
            // A whole number is also a decimal, so the narrower reading is offered only when every
            // value bears it.
            Add(hypotheses, ColumnType.Decimal, null, numbers, facts.NonEmptyCount, []);

            if (counts.Integer == numbers)
            {
                Add(hypotheses, ColumnType.Integer, null, numbers, facts.NonEmptyCount, []);
            }
        }
    }

    private static void AddParsed(ColumnFacts facts, List<TypeHypothesis> hypotheses)
    {
        Add(hypotheses, ColumnType.Boolean, null, facts.BooleanCount, facts.NonEmptyCount, []);

        foreach (CultureParseCounts counts in facts.ParseCounts)
        {
            int numbers = counts.Integer + counts.Decimal;

            if (numbers > 0)
            {
                Add(hypotheses, ColumnType.Decimal, counts.Culture, numbers, facts.NonEmptyCount, counts.NumericOutliers);

                if (counts.Integer == numbers)
                {
                    Add(hypotheses, ColumnType.Integer, counts.Culture, numbers, facts.NonEmptyCount, counts.NumericOutliers);
                }
            }

            Add(hypotheses, ColumnType.Date, counts.Culture, counts.Date, facts.NonEmptyCount, counts.DateOutliers);
        }
    }

    private static void Add(
        List<TypeHypothesis> hypotheses,
        ColumnType type,
        string? culture,
        int matched,
        int nonEmpty,
        IReadOnlyList<ValueLocation> outliers)
    {
        if (matched == 0)
        {
            return;
        }

        hypotheses.Add(new TypeHypothesis
        {
            Type = type,
            Culture = culture,
            Confidence = (double)matched / nonEmpty,
            MatchedCount = matched,
            UnmatchedCount = nonEmpty - matched,
            Outliers = matched == nonEmpty ? [] : outliers,
        });
    }

    /// <summary>
    /// How much a reading claims. Used only to order equally confident ones, narrowest first.
    /// </summary>
    private static int Specificity(ColumnType type) =>
        type switch
        {
            ColumnType.Boolean => 5,
            ColumnType.Date => 4,
            ColumnType.Integer => 3,
            ColumnType.Decimal => 2,
            _ => 0,
        };
}
