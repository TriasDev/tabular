namespace TriasDev.Tabular;

/// <summary>A workbook's number cell — a double — as the decimal its 15 significant digits say.</summary>
/// <remarks>
/// <para>
/// Up to .NET 10 the runtime's <c>(decimal)double</c> cast rounded to 15 significant digits, the
/// precision a double holds, so a cell holding <c>0.1</c> read as <c>0.1</c>. From .NET 11 the cast
/// converts the double's exact binary value: <c>0.1000000000000000055511151231</c>. The library ships
/// a net10 build that runs on .NET 11 too, so it cannot rely on the cast (#133).
/// </para>
/// <para>
/// This is the runtime's former algorithm (<c>DecCalc.VarDecFromR8</c>), bit for bit: the same
/// digits, the same scale (trailing zeros dropped), zero below decimal's smallest step, and an
/// <see cref="OverflowException"/> where the cast threw one. Reading, analysis and the write side's
/// precision check all go through it, so what is written reads back the same on every runtime.
/// </para>
/// </remarks>
internal static class WorkbookNumber
{
    private const int MaxScale = 28;

    /// <summary>The powers of ten the scaling multiplies or divides by, as doubles: 10^0 to 10^28.</summary>
    /// <remarks>
    /// An array, not a <c>ReadOnlySpan&lt;double&gt;</c> property over constant data: on .NET 8 such a
    /// property allocates on every call until the method is compiled with optimizations, and this runs
    /// for every number cell — a write of a million rows allocated megabytes before tiering caught up.
    /// </remarks>
    private static readonly double[] DoublePowers10 =
    [
        1, 1e1, 1e2, 1e3, 1e4, 1e5, 1e6, 1e7, 1e8, 1e9, 1e10, 1e11, 1e12, 1e13, 1e14, 1e15, 1e16, 1e17,
        1e18, 1e19, 1e20, 1e21, 1e22, 1e23, 1e24, 1e25, 1e26, 1e27, 1e28,
    ];

    /// <summary>The double rounded to 15 significant digits, as a decimal.</summary>
    /// <exception cref="OverflowException">The value is not finite or is beyond decimal's range.</exception>
    public static decimal ToDecimal(double value)
    {
        // The largest scale, 10^28, is just over 2^93: a double with a binary exponent below -94
        // cannot reach half of decimal's smallest step and rounds to zero.
        int exponent = (int)((BitConverter.DoubleToUInt64Bits(value) >> 52) & 0x7FF) - 1022;
        if (exponent < -94)
        {
            return 0m;
        }

        if (exponent > 96)
        {
            throw new OverflowException("The value is beyond the range of a decimal.");
        }

        bool negative = value < 0;
        double scaled = Math.Abs(value);

        // The power of ten that brings the value to 15 digits, from its binary exponent times
        // log10(2) in fixed point: log10(2) * 2^16 ≈ 19728.3. Between -14 and 43.
        int power = 14 - ((exponent * 19728) >> 16);

        if (power >= 0)
        {
            power = Math.Min(power, MaxScale);
            scaled *= DoublePowers10[power];
        }
        else if (power != -1 || scaled >= 1e15)
        {
            scaled /= DoublePowers10[-power];
        }
        else
        {
            power = 0;
        }

        if (scaled < 1e14 && power < MaxScale)
        {
            scaled *= 10;
            power++;
        }

        // Half to even, as the cast rounded.
        ulong mantissa = (ulong)Math.Round(scaled);
        if (mantissa == 0)
        {
            return 0m;
        }

        if (power < 0)
        {
            // A whole number of 16 to 29 digits: the 15 digits times 10^-power, at most 10^14.
            UInt128 whole = (UInt128)mantissa * Pow10(-power);
            if (whole >> 96 != 0)
            {
                throw new OverflowException("The value is beyond the range of a decimal.");
            }

            return new decimal((int)(uint)whole, (int)(uint)(whole >> 32), (int)(uint)(whole >> 64), negative, 0);
        }

        // Drop trailing zeros, no more than the scale holds and no more than 14: the leading digit
        // of the 15 is never zero.
        int removable = Math.Min(power, 14);
        removable = DropZeros(ref mantissa, ref power, removable, 8, 100_000_000);
        removable = DropZeros(ref mantissa, ref power, removable, 4, 10_000);
        removable = DropZeros(ref mantissa, ref power, removable, 2, 100);
        DropZeros(ref mantissa, ref power, removable, 1, 10);

        return new decimal((int)(uint)mantissa, (int)(uint)(mantissa >> 32), 0, negative, (byte)power);
    }

    private static int DropZeros(ref ulong mantissa, ref int power, int removable, int zeros, ulong divisor)
    {
        if (removable < zeros)
        {
            return removable;
        }

        ulong quotient = mantissa / divisor;
        if (quotient * divisor != mantissa)
        {
            return removable;
        }

        mantissa = quotient;
        power -= zeros;
        return removable - zeros;
    }

    private static ulong Pow10(int power)
    {
        ulong result = 1;
        for (int i = 0; i < power; i++)
        {
            result *= 10;
        }

        return result;
    }
}
