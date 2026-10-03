using System.IO.Compression;

namespace TriasDev.Tabular.Xlsx;

/// <summary>Knobs for writing an xlsx workbook.</summary>
public sealed record XlsxWriterOptions
{
    /// <summary>The defaults: the fastest compression.</summary>
    public static XlsxWriterOptions Default { get; } = new();

    /// <summary>
    /// How hard each sheet is compressed: <see cref="CompressionLevel.Fastest"/> by default, since
    /// writing speed is the point; <see cref="CompressionLevel.Optimal"/> writes a smaller file more
    /// slowly.
    /// </summary>
    public CompressionLevel CompressionLevel { get; init; } = CompressionLevel.Fastest;

    internal XlsxWriterOptions Checked()
    {
        if (!Enum.IsDefined(CompressionLevel))
        {
#pragma warning disable S3928 // Justification: Parameter names identify the properties being validated, not method parameters
            throw new ArgumentOutOfRangeException(
                nameof(CompressionLevel),
                CompressionLevel,
                $"{nameof(XlsxWriterOptions)}.{nameof(CompressionLevel)} is not a compression level.");
#pragma warning restore S3928
        }

        return this;
    }
}
