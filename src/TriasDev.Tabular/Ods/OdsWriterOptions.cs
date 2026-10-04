using System.IO.Compression;

namespace TriasDev.Tabular.Ods;

/// <summary>Knobs for writing an OpenDocument spreadsheet.</summary>
public sealed record OdsWriterOptions
{
    /// <summary>The defaults: the fastest compression.</summary>
    public static OdsWriterOptions Default { get; } = new();

    /// <summary>
    /// How hard <c>content.xml</c> is compressed: <see cref="CompressionLevel.Fastest"/> by default,
    /// since writing speed is the point; <see cref="CompressionLevel.Optimal"/> writes a smaller file
    /// more slowly.
    /// </summary>
    public CompressionLevel CompressionLevel { get; init; } = CompressionLevel.Fastest;

    internal OdsWriterOptions Checked()
    {
        if (!Enum.IsDefined(CompressionLevel))
        {
#pragma warning disable S3928 // Justification: Parameter names identify the properties being validated, not method parameters
            throw new ArgumentOutOfRangeException(
                nameof(CompressionLevel),
                CompressionLevel,
                $"{nameof(OdsWriterOptions)}.{nameof(CompressionLevel)} is not a compression level.");
#pragma warning restore S3928
        }

        return this;
    }
}
