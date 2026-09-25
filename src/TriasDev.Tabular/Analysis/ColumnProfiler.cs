using System.Globalization;

namespace TriasDev.Tabular;

/// <summary>
/// Accumulates what is true about one column as its values go past.
/// </summary>
/// <remarks>
/// <para>
/// Everything here is O(1) in memory per value except distinct tracking, which is bounded by the
/// budget it is given. That is what lets analysis read every row of a file rather than a sample: the
/// questions the profile answers — is every value the same length, does this column ever hold a
/// number, are the values all different — are falsified by one row anywhere, so a sample cannot
/// answer them at all.
/// </para>
/// <para>
/// The profiler infers nothing. It counts how values fare under each culture and leaves the reading
/// of those counts to whoever asks for it.
/// </para>
/// </remarks>
internal sealed class ColumnProfiler
{
    private readonly AnalysisOptions _options;
    private readonly DistinctBudget _budget;
    private readonly CultureAccumulator[] _cultures;

    /// <summary>
    /// For each culture, the first culture that reads numbers exactly as it does — itself, when none
    /// before it does. The invariant culture and en-US write numbers alike, so a value read as a
    /// number under one is read the same under the other, and parsing it twice was a third of the
    /// numeric work for nothing.
    /// </summary>
    private readonly int[] _numberTwin;

    /// <summary>The numeric reading of the current value under each culture, reused across twins.</summary>
    private readonly NumberRead[] _numberReads;
    private readonly UInt64Set _distinctHashes = new();
    private readonly Dictionary<string, int> _frequencies = new(StringComparer.Ordinal);
    private readonly List<string> _firstValues = [];
    private readonly List<string> _distinctValues = [];
    /// <summary>How many cells of each kind, indexed by the kind — counted on every cell, so an array.</summary>
    private readonly int[] _nativeKinds = new int[Enum.GetValues<RawCellKind>().Length];

    /// <summary>The kinds in the order they were first seen, which is the order the facts report them in.</summary>
    private readonly List<RawCellKind> _kindsSeen = [];

    private int _emptyCount;
    private int _nonEmptyCount;
    private int _booleanCount;
    private int? _minLength;
    private int? _maxLength;
    private bool _distinctIsExact = true;
    private bool _distinctValuesComplete = true;

    /// <summary>Creates a profiler for one column.</summary>
    public ColumnProfiler(int index, string header, DistinctBudget budget, AnalysisOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(budget);

        Index = index;
        Header = header ?? string.Empty;
        _budget = budget;
        _options = options ?? AnalysisOptions.Default;
        _cultures = [.. CultureCatalog.Available(_options.Cultures).Select(name => new CultureAccumulator(name, _options.OutlierSampleSize))];
        _numberTwin = new int[_cultures.Length];
        _numberReads = new NumberRead[_cultures.Length];

        for (int i = 0; i < _cultures.Length; i++)
        {
            _numberTwin[i] = Array.FindIndex(_cultures, 0, i + 1, c => c.ReadsNumbersLike(_cultures[i]));
        }
    }

    /// <summary>The column's position, zero-based.</summary>
    public int Index { get; }

    /// <summary>The column's header, as the file holds it.</summary>
    public string Header { get; }

    /// <summary>
    /// Takes a run of rows in which this column held nothing.
    /// </summary>
    /// <remarks>
    /// For a column that first appears below the header: it has to be given the rows it missed, or
    /// its counts do not add up to the sheet's. In one call rather than a loop, because a file whose
    /// rows grow one column at a time would otherwise cost the product of its rows and its columns —
    /// closing one defect by opening a quadratic one.
    /// </remarks>
    public void AcceptEmpties(int count)
    {
        if (count <= 0)
        {
            return;
        }

        _emptyCount += count;
        Count(RawCellKind.Empty, count);
    }

    /// <summary>Takes one row's value for this column.</summary>
    public void Accept(in RawCell cell, int rowNumber)
    {
        Count(cell.Kind, 1);

        if (cell.IsEmpty)
        {
            _emptyCount++;
            return;
        }

        _nonEmptyCount++;

        string text = cell.AsText() ?? string.Empty;

        _minLength = _minLength is null ? text.Length : Math.Min(_minLength.Value, text.Length);
        _maxLength = _maxLength is null ? text.Length : Math.Max(_maxLength.Value, text.Length);

        TrackDistinct(text);
        TrackFrequency(text);

        if (_firstValues.Count < _options.FirstValueSampleSize)
        {
            _firstValues.Add(text);
        }

        // A workbook says what a cell is; a csv file does not. Where the file has already decided,
        // every culture agrees with it, and no parsing is attempted or needed.
        switch (cell.Kind)
        {
            case RawCellKind.Boolean:
                _booleanCount++;
                return;

            case RawCellKind.Number:
                foreach (CultureAccumulator culture in _cultures)
                {
                    culture.AcceptNativeNumber(cell.Number);
                }

                return;

            case RawCellKind.Date:
                foreach (CultureAccumulator culture in _cultures)
                {
                    culture.AcceptNativeDate(cell.Date);
                }

                return;
        }

        if (bool.TryParse(text, out _))
        {
            _booleanCount++;
        }

        // Culture-free questions, asked once here rather than once per culture below: whether the
        // value could be a number at all, and whether it has the shape of a date.
        bool couldBeNumeric = CultureAccumulator.CouldBeNumeric(text);
        bool couldBeDate = DateReading.LooksLikeOne(text);

        for (int i = 0; i < _cultures.Length; i++)
        {
            int twin = _numberTwin[i];

            _numberReads[i] = twin == i ? _cultures[i].ReadNumber(text, couldBeNumeric) : _numberReads[twin];
            _cultures[i].AcceptText(text, rowNumber, _numberReads[i], couldBeDate);
        }
    }

    /// <summary>What a value read as under one culture's number rules.</summary>
    private enum NumberKind : byte
    {
        None,
        Integer,
        Decimal,
    }

    /// <summary>The outcome of reading one value as a number under one culture.</summary>
    private readonly record struct NumberRead(NumberKind Kind, decimal Value);

    /// <summary>Renders what has been measured so far.</summary>
    public ColumnFacts ToFacts()
    {
        CultureAccumulator best = BestCulture();

        return new ColumnFacts
        {
            Index = Index,
            Header = Header,
            EmptyCount = _emptyCount,
            NonEmptyCount = _nonEmptyCount,
            MinLength = _minLength,
            MaxLength = _maxLength,
            BooleanCount = _booleanCount,
            ParseCounts = [.. _cultures.Select(c => c.ToCounts())],
            MinNumeric = best.MinNumeric,
            MaxNumeric = best.MaxNumeric,
            MinDate = best.MinDate,
            MaxDate = best.MaxDate,
            NativeKinds = _kindsSeen.ToDictionary(kind => kind, kind => _nativeKinds[(int)kind]),
            DistinctCount = _distinctHashes.Count,
            DistinctCountIsExact = _distinctIsExact,
            IsUnique = Unique(),
            DistinctSamples = TopFrequencies(),
            Samples = [.. _firstValues],
            DistinctValues = _distinctValuesComplete ? [.. _distinctValues] : [],
            DistinctValuesAreComplete = _distinctValuesComplete,
        };
    }

    /// <summary>
    /// The culture that read the most values as numbers, or as dates where none read as numbers.
    /// </summary>
    /// <remarks>
    /// Only used to pick which culture's extremes to publish. It is not a verdict on the column, and
    /// the per-culture counts stay available so that a caller can disagree.
    /// </remarks>
    private CultureAccumulator BestCulture()
    {
        CultureAccumulator best = _cultures[0];

        foreach (CultureAccumulator culture in _cultures)
        {
            if (culture.NumericCount > best.NumericCount
                || (culture.NumericCount == best.NumericCount && culture.DateCount > best.DateCount))
            {
                best = culture;
            }
        }

        return best;
    }

    /// <summary>
    /// Whether this column can identify its rows.
    /// </summary>
    /// <remarks>
    /// <para>
    /// An empty cell disqualifies the column, and that is a decision rather than an inevitability. A
    /// column that identifies a record must do so for every record; a row with nothing in it is a row
    /// the column cannot name, whatever the other values do. Counting only the non-empty values would
    /// call such a column unique and let it be mapped to a field that identifies a record.
    /// </para>
    /// <para>
    /// Null rather than false where nothing was measured. A column with no values at all is not a
    /// column that failed to be unique — there was no question to answer, and an explicit "not
    /// determined" is worth more than either answer.
    /// </para>
    /// </remarks>
    private bool? Unique()
    {
        if (!_distinctIsExact)
        {
            return null;
        }

        if (_nonEmptyCount == 0)
        {
            return _emptyCount == 0 ? null : false;
        }

        return _emptyCount == 0 && _distinctHashes.Count == _nonEmptyCount;
    }

    private void TrackDistinct(string text)
    {
        ulong hash = ValueHash.Of(text);

        if (_distinctHashes.Contains(hash))
        {
            return;
        }

        if (!_budget.TryReserve())
        {
            // Out of allowance. The count stops rising and says so, rather than drifting quietly
            // towards a number nobody can trust.
            _distinctIsExact = false;
            _distinctValuesComplete = false;
            return;
        }

        _distinctHashes.Add(hash);

        // Asked before the count, and that order is the whole of it. Giving up is permanent, so the
        // emptied list must not read as room to start again: gating on the count instead refilled it
        // once per thousand distinct values, and on a five-million-row identifier column that churn
        // allocated 33 MB doing nothing.
        if (!_distinctValuesComplete)
        {
            return;
        }

        // Reached only by a value never seen before, so the cost falls on a column's variety rather
        // than on its length. In a column of codes the branch is taken a few hundred times and never
        // again, however many million rows follow.
        if (_distinctValues.Count < _options.RetainedDistinctValues)
        {
            _distinctValues.Add(text);
            return;
        }

        // Past the cap the set stops being the column's values and becomes a subset of them, which
        // is a different thing and must not be read as the first. Released rather than carried: an
        // identifier column in a five-million-row file would otherwise hold a thousand strings
        // nothing will ever read.
        _distinctValuesComplete = false;
        _distinctValues.Clear();
        _distinctValues.TrimExcess();
    }

    private void TrackFrequency(string text)
    {
        if (_frequencies.TryGetValue(text, out int seen))
        {
            _frequencies[text] = seen + 1;
            return;
        }

        if (_frequencies.Count < _options.FrequencySampleSize)
        {
            _frequencies[text] = 1;
        }
    }

    private List<ValueFrequency> TopFrequencies() =>
        [.. _frequencies
            .OrderByDescending(pair => pair.Value)
            .ThenBy(pair => pair.Key, StringComparer.Ordinal)
            .Take(_options.ReportedSampleSize)
            .Select(pair => new ValueFrequency { Value = pair.Key, Count = pair.Value })];

    private void Count(RawCellKind kind, int count)
    {
        if (_nativeKinds[(int)kind] == 0)
        {
            _kindsSeen.Add(kind);
        }

        _nativeKinds[(int)kind] += count;
    }

    /// <summary>One culture's running totals for a column.</summary>
    private sealed class CultureAccumulator(string name, int outlierLimit)
    {
        private readonly CultureInfo _culture = name.Length == 0
            ? CultureInfo.InvariantCulture
            : CultureInfo.GetCultureInfo(name);

        private readonly List<ValueLocation> _numericOutliers = [];
        private readonly List<ValueLocation> _dateOutliers = [];

        private int _integer;
        private int _decimal;
        private int _date;

        public int NumericCount => _integer + _decimal;

        public int DateCount => _date;

        public decimal? MinNumeric { get; private set; }

        public decimal? MaxNumeric { get; private set; }

        public DateTime? MinDate { get; private set; }

        public DateTime? MaxDate { get; private set; }

        public void AcceptNativeNumber(double value)
        {
            decimal converted;

            try
            {
                converted = (decimal)value;
            }
            catch (OverflowException)
            {
                // Beyond decimal's range. The value is still a number and still counted; only its
                // contribution to the extremes is dropped, which is better than failing the file.
                _decimal++;
                return;
            }

            if (converted == Math.Truncate(converted))
            {
                _integer++;
            }
            else
            {
                _decimal++;
            }

            Widen(converted);
        }

        public void AcceptNativeDate(DateTime value)
        {
            _date++;
            Widen(value);
        }

        /// <summary>Whether this culture reads numbers exactly as <paramref name="other"/> does.</summary>
        /// <remarks>
        /// Everything the two parses below and the grouping rule consult: the separators, the group
        /// sizes, the signs. Cultures equal in all of them give the same answer for every value.
        /// </remarks>
        public bool ReadsNumbersLike(CultureAccumulator other)
        {
            NumberFormatInfo a = _culture.NumberFormat;
            NumberFormatInfo b = other._culture.NumberFormat;

            return a.NumberDecimalSeparator == b.NumberDecimalSeparator
                && a.NumberGroupSeparator == b.NumberGroupSeparator
                && a.NumberGroupSizes.AsSpan().SequenceEqual(b.NumberGroupSizes)
                && a.NegativeSign == b.NegativeSign
                && a.PositiveSign == b.PositiveSign;
        }

        /// <param name="text">The value.</param>
        /// <param name="couldBeNumeric">
        /// Whether the value could be a number under any culture, asked once by the caller: a value
        /// holding a letter is not, and most columns of a real export are exactly that — streets,
        /// cities, descriptions. Skipping the attempt loses nothing, because such a value contributes
        /// zero to every numeric count either way.
        /// </param>
        public NumberRead ReadNumber(string text, bool couldBeNumeric)
        {
            bool grouped = couldBeNumeric && NumberReading.HasWellFormedGroups(text, _culture.NumberFormat);

            if (grouped
                && long.TryParse(text, NumberStyles.Integer | NumberStyles.AllowThousands, _culture, out long whole))
            {
                return new NumberRead(NumberKind.Integer, whole);
            }

            if (grouped && decimal.TryParse(text, NumberStyles.Number, _culture, out decimal fraction))
            {
                return new NumberRead(NumberKind.Decimal, fraction);
            }

            return default;
        }

        /// <param name="text">The value.</param>
        /// <param name="rowNumber">Where it stands, for an outlier.</param>
        /// <param name="number">What <see cref="ReadNumber"/> made of it, here or under a twin culture.</param>
        /// <param name="couldBeDate">Whether the value has the shape of a date, asked once by the caller.</param>
        public void AcceptText(string text, int rowNumber, NumberRead number, bool couldBeDate)
        {
            switch (number.Kind)
            {
                case NumberKind.Integer:
                    _integer++;
                    Widen(number.Value);
                    break;

                case NumberKind.Decimal:
                    _decimal++;
                    Widen(number.Value);
                    break;

                case NumberKind.None when _numericOutliers.Count < outlierLimit:
                    _numericOutliers.Add(new ValueLocation { RowNumber = rowNumber, RawValue = text });
                    break;
            }

            if (couldBeDate && DateReading.TryReadShaped(text, _culture, out DateTime date))
            {
                _date++;
                Widen(date);
            }
            else if (_dateOutliers.Count < outlierLimit)
            {
                // Recorded whatever else the value turned out to be. Skipping it when the value also
                // read as a number left a date column with a stray number saying that one value did
                // not fit while being unable to say which — the one thing a located outlier is for.
                _dateOutliers.Add(new ValueLocation { RowNumber = rowNumber, RawValue = text });
            }
        }

        /// <summary>
        /// Whether a value could be a number under any of the cultures being tried.
        /// </summary>
        /// <remarks>
        /// A filter, not a parser: it rejects only what no culture could accept, so a value it passes
        /// still has to be parsed. What it buys is skipping the parse attempts on the majority of
        /// cells in a text-heavy file, which is where the cost of analysing a csv sits — every value
        /// there is text, while a workbook's numbers arrive already typed and are never parsed at all.
        /// </remarks>
        public static bool CouldBeNumeric(string text)
        {
            bool digit = false;

            foreach (char c in text)
            {
                if (char.IsAsciiDigit(c))
                {
                    digit = true;
                    continue;
                }

                // The separators, sign and exponent the tried cultures use, and the two spaces that
                // some of them group with.
                if (c is '.' or ',' or '-' or '+' or 'e' or 'E' or ' ' or '\u00A0' or '\u202F')
                {
                    continue;
                }

                return false;
            }

            return digit;
        }

        private void Widen(decimal value)
        {
            MinNumeric = MinNumeric is null ? value : Math.Min(MinNumeric.Value, value);
            MaxNumeric = MaxNumeric is null ? value : Math.Max(MaxNumeric.Value, value);
        }

        private void Widen(DateTime value)
        {
            if (MinDate is null || value < MinDate.Value)
            {
                MinDate = value;
            }

            if (MaxDate is null || value > MaxDate.Value)
            {
                MaxDate = value;
            }
        }

        public CultureParseCounts ToCounts() =>
            new()
            {
                Culture = name,
                Integer = _integer,
                Decimal = _decimal,
                Date = _date,
                NumericOutliers = [.. _numericOutliers],
                DateOutliers = [.. _dateOutliers],
            };
    }
}
