namespace TriasDev.Tabular.WriteComparison;

/// <summary>Which kind of file a writer produces.</summary>
internal enum FileKind
{
    Csv,
    Xlsx,
    Ods,
    Zip,
}

/// <summary>
/// One library, writing one scenario's data into a stream the way its documentation recommends for speed.
/// </summary>
/// <remarks>
/// Every writer writes the same data: the scenario's dataset, row by row from <see cref="IDataset.Fill"/>
/// (or column by column from the same generators), one sheet, a header row, every value typed. A
/// library that cannot apply the styled scenarios' styles reports <see cref="Styled"/> false and is
/// left out of them, rather than timed on less work.
/// </remarks>
internal interface IWriter
{
    /// <summary>The name shown in the results.</summary>
    string Name { get; }

    /// <summary>A type from the library, used to report which version was measured.</summary>
    Type Anchor { get; }

    FileKind Kind { get; }

    /// <summary>Whether the writer applies the styles of <see cref="Styles"/>.</summary>
    bool Styled { get; }

    /// <summary>
    /// Writes the scenario's file into <paramref name="target"/> and completes it. Whether the stream is
    /// closed is up to the writer; the harness disposes it after.
    /// </summary>
    void Write(Scenario scenario, Stream target);
}
