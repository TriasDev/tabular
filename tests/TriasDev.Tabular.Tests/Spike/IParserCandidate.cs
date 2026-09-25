namespace TriasDev.Tabular.Tests.Spike;

/// <summary>Which formats a candidate is in the race for.</summary>
[Flags]
public enum CandidateFormats
{
    None = 0,
    Xlsx = 1,
    Csv = 2,
}

/// <summary>
/// One library under evaluation, reduced to the only thing asked of it: turn this stream into rows.
/// </summary>
/// <remarks>
/// <para>
/// The contract is a <see cref="Stream"/> rather than a <c>byte[]</c> because the fixtures that
/// decide the speed gate are hundreds of megabytes. Handing a candidate the whole file as an array —
/// and, for csv, as a decoded string on top of it — would measure the allocator rather than the
/// parser, and would measure it identically for everyone.
/// </para>
/// <para>
/// A candidate may hand back the same list instance on every row and overwrite it in place. Consumers
/// must copy anything they intend to keep. This is deliberate: whether a reader allocates per row is
/// one of the things being compared, and forcing everyone to allocate would hide it.
/// </para>
/// </remarks>
public interface IParserCandidate
{
    /// <summary>Name as it appears in test output and in the ADR.</summary>
    string Name { get; }

    /// <summary>Formats this candidate competes in.</summary>
    CandidateFormats Formats { get; }

    /// <summary>Enumerates the first sheet's rows. The stream must be seekable and positioned at zero.</summary>
    IEnumerable<IReadOnlyList<string?>> Rows(Stream stream);
}
