namespace TriasDev.Tabular;

/// <summary>
/// Refuses an option that cannot work where it is handed over, naming it.
/// </summary>
/// <remarks>
/// Otherwise a ceiling of zero is found out deep inside a read, as a limit every file exceeds, and a
/// negative size as an exception from a collection's constructor.
/// </remarks>
internal static class OptionChecks
{
    /// <param name="options">The parameter the options arrived through, as the exception names it.</param>
    public static void AtLeast(long value, long minimum, string owner, string option, string options = "options")
    {
        if (value < minimum)
        {
            throw new ArgumentOutOfRangeException(
                options,
                value,
                $"{owner}.{option} must be at least {minimum}.");
        }
    }
}
