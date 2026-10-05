using System.IO.Compression;

namespace TriasDev.Tabular;

/// <summary>How a zip of csv sheets is written.</summary>
public sealed record ZipWriterOptions
{
    /// <summary>The defaults: the fastest compression.</summary>
    public static ZipWriterOptions Default { get; } = new();

    /// <summary>
    /// How hard each entry is compressed; <see cref="CompressionLevel.Fastest"/> by default — a zip is
    /// written for its size, but most of a csv's size goes at the fastest level already.
    /// </summary>
    public CompressionLevel CompressionLevel { get; init; } = CompressionLevel.Fastest;

    internal ZipWriterOptions Checked()
    {
        if (!Enum.IsDefined(CompressionLevel))
        {
#pragma warning disable S3928 // Justification: Parameter names identify the properties being validated, not method parameters
            throw new ArgumentOutOfRangeException(
                nameof(CompressionLevel),
                CompressionLevel,
                $"{nameof(ZipWriterOptions)}.{nameof(CompressionLevel)} is not a compression level.");
#pragma warning restore S3928
        }

        return this;
    }
}
