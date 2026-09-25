namespace TriasDev.Tabular.Extraction;

/// <summary>
/// The file cannot be read the way the plan says it should be.
/// </summary>
/// <remarks>
/// A separate channel from a row's errors, deliberately. A bad value in row 812 and "this is not the
/// file you mapped" call for opposite responses — fix a cell, or start over — and a caller that
/// received both through one channel would have to sort them out itself.
/// </remarks>
public sealed class TabularStructureException : Exception
{
    /// <summary>Creates the exception.</summary>
    public TabularStructureException()
    {
    }

    /// <summary>Creates the exception with a message.</summary>
    public TabularStructureException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with a message and a cause.</summary>
    public TabularStructureException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
