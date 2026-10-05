namespace TriasDev.Tabular.Ods;

/// <summary>
/// Reads an ISO 8601 duration, <c>office:time-value</c>'s type, from a span: the value
/// <see cref="System.Xml.XmlConvert.ToTimeSpan"/> gives, without its string and without its exceptions.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="System.Xml.XmlConvert.ToTimeSpan"/> takes a string, so every time cell cost one, and it
/// reports a malformed value by throwing — about 6 µs and 800 bytes per cell, caught and turned into
/// "no time" by the reader. A file of malformed time cells is cheap to write and was expensive to read.
/// </para>
/// <para>
/// It follows the XSD parser step for step rather than the ISO grammar, because the reader's answer
/// must not change: <c>-PnYnMnDTnHnMn.nS</c>, every part optional but one; whitespace around it
/// trimmed; whole numbers up to <see cref="int.MaxValue"/>, an overflow refused; a fraction of a
/// second cut to nine digits and then to ticks; a year counted as 365 days and a month as 30. The
/// oddities come along — a fraction with no whole seconds (<c>PT.5S</c>), or no digits at all
/// (<c>PT1.S</c>), is accepted there and so here. <c>IsoDurationTests</c> holds the two to the same
/// answer on a table of inputs and on random ones.
/// </para>
/// </remarks>
internal static class IsoDuration
{
    /// <summary>
    /// The duration the text spells, or false where <see cref="System.Xml.XmlConvert.ToTimeSpan"/>
    /// would throw — a malformed duration, or one beyond a <see cref="TimeSpan"/>.
    /// </summary>
    public static bool TryParse(ReadOnlySpan<char> text, out TimeSpan value)
    {
        Reader reader = new(text.Trim());

        if (!reader.TryRead())
        {
            value = default;
            return false;
        }

        return reader.TryToTimeSpan(out value);
    }

    private enum Ending
    {
        /// <summary>The text ended right after a designator: read, since a part has just been named.</summary>
        Done,

        /// <summary>The text goes on; what follows decides.</summary>
        More,

        /// <summary>Malformed.</summary>
        Malformed,
    }

    /// <summary>The parse, step for step as the XSD parser takes it, and the parts it has named.</summary>
    private ref struct Reader
    {
        private const int Years = 0;
        private const int Months = 1;
        private const int Days = 2;
        private const int Hours = 3;
        private const int Minutes = 4;
        private const int Seconds = 5;

        private readonly ReadOnlySpan<char> _s;
        private int _pos;

        /// <summary>The number read last, and how many digits it had: zero when none stood there.</summary>
        private int _number;
        private int _digits;

        private bool _negative;
        private bool _any;
        private int _years;
        private int _months;
        private int _days;
        private int _hours;
        private int _minutes;
        private int _seconds;
        private int _nanoseconds;

        public Reader(ReadOnlySpan<char> s)
        {
            _s = s;
        }

        /// <summary>Takes the duration apart; false where the XSD parser reports a malformed one.</summary>
        public bool TryRead()
        {
            if (_s.IsEmpty)
            {
                return false;
            }

            if (_s[0] == '-')
            {
                _negative = true;
                _pos++;
            }

            if (_pos >= _s.Length || _s[_pos++] != 'P' || !TryReadNumberBeforeMore())
            {
                return false;
            }

            Ending ending = ReadDesignated("YMD", Years);

            if (ending != Ending.More)
            {
                return ending == Ending.Done;
            }

            if (_s[_pos] == 'T')
            {
                // A number before the T belongs to no date part.
                if (_digits != 0)
                {
                    return false;
                }

                _pos++;

                if (!TryReadNumberBeforeMore())
                {
                    return false;
                }

                ending = ReadDesignated("HM", Hours);

                if (ending == Ending.More)
                {
                    ending = ReadSeconds();
                }

                if (ending != Ending.More)
                {
                    return ending == Ending.Done;
                }
            }

            // Nothing may follow, and a duration cannot end in digits.
            return _digits == 0 && _pos == _s.Length && _any;
        }

        /// <summary>
        /// Takes the number read last as the part its designator names, for each designator in turn
        /// that stands at the position — each at most once, in this order — reading the next number
        /// after it.
        /// </summary>
        private Ending ReadDesignated(ReadOnlySpan<char> designators, int firstPart)
        {
            for (int i = 0; i < designators.Length; i++)
            {
                if (_s[_pos] != designators[i])
                {
                    continue;
                }

                if (_digits == 0)
                {
                    return Ending.Malformed;
                }

                Set(firstPart + i, _number);

                if (++_pos == _s.Length)
                {
                    return Ending.Done;
                }

                if (!TryReadNumberBeforeMore())
                {
                    return Ending.Malformed;
                }
            }

            return Ending.More;
        }

        /// <summary>The seconds: whole, or with a fraction cut to nanoseconds.</summary>
        private Ending ReadSeconds()
        {
            if (_s[_pos] == 'S')
            {
                if (_digits == 0)
                {
                    return Ending.Malformed;
                }

                Set(Seconds, _number);
                return ++_pos == _s.Length ? Ending.Done : Ending.More;
            }

            if (_s[_pos] != '.')
            {
                return Ending.More;
            }

            // The XSD parser asks for no digit before the point, nor after it.
            _pos++;
            Set(Seconds, _number);
            TryReadNumber(eatOverflow: true);

            int fraction = _digits == 0 ? 0 : _number;

            // To nanoseconds: digits past the ninth dropped, short fractions scaled up. The count
            // ends at nine either way, and a count that is not zero is what refuses anything after
            // the S — as the XSD parser's does.
            for (; _digits > 9; _digits--)
            {
                fraction /= 10;
            }

            for (; _digits < 9; _digits++)
            {
                fraction *= 10;
            }

            _nanoseconds = fraction;

            if (_pos >= _s.Length || _s[_pos] != 'S')
            {
                return Ending.Malformed;
            }

            return ++_pos == _s.Length ? Ending.Done : Ending.More;
        }

        private void Set(int part, int value)
        {
            _any = true;

            switch (part)
            {
                case Years: _years = value; break;
                case Months: _months = value; break;
                case Days: _days = value; break;
                case Hours: _hours = value; break;
                case Minutes: _minutes = value; break;
                default: _seconds = value; break;
            }
        }

        /// <summary>Reads a number, which must not end the text: something has to say what it counts.</summary>
        private bool TryReadNumberBeforeMore() => TryReadNumber(eatOverflow: false) && _pos < _s.Length;

        /// <summary>
        /// Reads ASCII digits as a whole number; false when it passes <see cref="int.MaxValue"/>, unless
        /// <paramref name="eatOverflow"/> — a fraction — keeps the digits that fit and skips the rest.
        /// </summary>
        private bool TryReadNumber(bool eatOverflow)
        {
            int start = _pos;
            _number = 0;

            while (_pos < _s.Length && char.IsAsciiDigit(_s[_pos]))
            {
                int digit = _s[_pos] - '0';

                if (_number > (int.MaxValue - digit) / 10)
                {
                    _digits = _pos - start;

                    while (eatOverflow && _pos < _s.Length && char.IsAsciiDigit(_s[_pos]))
                    {
                        _pos++;
                    }

                    return eatOverflow;
                }

                _number = (_number * 10) + digit;
                _pos++;
            }

            _digits = _pos - start;
            return true;
        }

        /// <summary>
        /// The parts as ticks, years at 365 days and months at 30, as the XSD parser counts them; false
        /// beyond a <see cref="TimeSpan"/>. Counted in 128 bits, where no product of the parts can wrap,
        /// so the one comparison at the end stands in for the parser's checked arithmetic.
        /// </summary>
        public readonly bool TryToTimeSpan(out TimeSpan value)
        {
            UInt128 ticks = (((UInt128)(uint)_years + ((uint)_months / 12)) * 365) + (((uint)_months % 12) * 30);
            ticks += (uint)_days;
            ticks = (ticks * 24) + (uint)_hours;
            ticks = (ticks * 60) + (uint)_minutes;
            ticks = (ticks * 60) + (uint)_seconds;
            ticks = (ticks * TimeSpan.TicksPerSecond) + ((uint)_nanoseconds / 100);

            const ulong MostPositive = long.MaxValue;
            value = default;

            if (ticks > (_negative ? MostPositive + 1 : MostPositive))
            {
                return false;
            }

            if (!_negative)
            {
                value = new TimeSpan((long)(ulong)ticks);
            }
            else
            {
                // The one negative count whose magnitude a long does not hold.
                value = ticks == MostPositive + 1 ? TimeSpan.MinValue : new TimeSpan(-(long)(ulong)ticks);
            }

            return true;
        }
    }
}
