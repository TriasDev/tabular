namespace TriasDev.Tabular;

/// <summary>The checks every format makes on a value before writing it.</summary>
internal static class ValueChecks
{
    /// <summary>
    /// Whether a double comes back as itself: finite, and within the 15 significant digits that a
    /// workbook's number cell is read back through.
    /// </summary>
    /// <remarks>
    /// The import reads a workbook number into a decimal by a plain cast, which keeps 15 significant
    /// digits. A double that survives that cast and the cast back reads back exactly; any other would
    /// come back changed — and differently by format, since csv reads its digits as written. One rule
    /// for every format keeps the round trip the same whichever is chosen.
    /// </remarks>
    public static string? Double(double value)
    {
        if (!double.IsFinite(value))
        {
            return ErrorCodes.Write.NotFinite;
        }

        decimal asDecimal;

        try
        {
            asDecimal = (decimal)value;
        }
        catch (OverflowException)
        {
            return ErrorCodes.Write.PrecisionLoss;
        }

        #pragma warning disable S1244 // Comparing a double with exact values is intentional: we test round-trip fidelity through decimal.
        return (double)asDecimal == value ? null : ErrorCodes.Write.PrecisionLoss;
        #pragma warning restore S1244
    }

    /// <summary>The date with anything finer than a millisecond dropped, and no kind.</summary>
    /// <remarks>
    /// Truncated, not rounded: rounding overflows on <see cref="DateTime.MaxValue"/> and moves
    /// 23:59:59.9996 into the next day. A workbook serial holds no more than milliseconds, and the
    /// rule is the same for every format so the round trip is too.
    /// </remarks>
    public static DateTime Truncated(DateTime value) =>
        new(value.Ticks - (value.Ticks % TimeSpan.TicksPerMillisecond), DateTimeKind.Unspecified);
}
