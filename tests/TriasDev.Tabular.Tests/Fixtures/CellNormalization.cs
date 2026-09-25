using System.Globalization;

namespace TriasDev.Tabular.Tests.Fixtures;

/// <summary>
/// Reduces what a parser returns to one comparable text form.
/// </summary>
/// <remarks>
/// Candidates disagree about types before they disagree about values: one hands back a
/// <see cref="double"/> for every number, another the raw string, a third a <see cref="decimal"/>.
/// Comparing them at all requires one agreed form, and this is it. The form is deliberately lossy in
/// exactly one direction — a date is compared to the day, not the tick — because a serial-to-date
/// conversion that differs in the last tick is a rounding difference, while one that differs in the
/// day is a bug.
/// </remarks>
public static class CellNormalization
{
    /// <summary>An absent, empty or whitespace-only cell.</summary>
    public static string? Empty => null;

    public static string? Text(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;

    public static string Number(double value) =>
        value.ToString("R", CultureInfo.InvariantCulture);

    public static string Number(decimal value) =>
        value.ToString(CultureInfo.InvariantCulture);

    public static string Date(DateTime value) =>
        value.TimeOfDay == TimeSpan.Zero
            ? value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            : value.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture);

    public static string Boolean(bool value) => value ? "true" : "false";

    /// <summary>
    /// Normalises a value whose static type is not known, which is what the libraries returning
    /// <see cref="object"/> force on the harness.
    /// </summary>
    public static string? Unknown(object? value) =>
        value switch
        {
            null => Empty,
            string s => Text(s),
            bool b => Boolean(b),
            DateTime d => Date(d),
            DateTimeOffset o => Date(o.DateTime),
            decimal m => Number(m),
            double d => Number(d),
            float f => Number((double)f),
            int i => i.ToString(CultureInfo.InvariantCulture),
            long l => l.ToString(CultureInfo.InvariantCulture),
            _ => value.ToString(),
        };
}
