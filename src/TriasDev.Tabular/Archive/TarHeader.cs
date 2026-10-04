namespace TriasDev.Tabular.Archive;

/// <summary>Whether bytes are the header of a tar archive — the test detection uses.</summary>
/// <remarks>
/// The magic alone would let a text file holding "ustar" at offset 257 pass for a tar; the header
/// checksum cannot match by chance, so both are required. A v7 tar has no magic and is not known.
/// </remarks>
internal static class TarHeader
{
    public const int BlockSize = 512;

    private const int MagicOffset = 257;
    private const int ChecksumOffset = 148;
    private const int ChecksumLength = 8;

    /// <summary>A POSIX ustar header (magic and version "00") or a GNU one, with a checksum that matches.</summary>
    public static bool IsHeader(ReadOnlySpan<byte> block)
    {
        if (block.Length < BlockSize)
        {
            return false;
        }

        ReadOnlySpan<byte> magic = block.Slice(MagicOffset, 8);

        if (!magic.StartsWith("ustar\0"u8) && !magic.SequenceEqual("ustar  \0"u8))
        {
            return false;
        }

        ReadOnlySpan<byte> header = block[..BlockSize];
        return ReadChecksum(block.Slice(ChecksumOffset, ChecksumLength)) is { } stored
            && (stored == Sum(header, signed: false) || stored == Sum(header, signed: true));
    }

    /// <summary>
    /// Whether a gzip stream's first decompressed block is a tar header; leaves the stream where it was.
    /// A gzip too short or too damaged to give one block is not a tar — the gzip path reports it.
    /// </summary>
    public static bool IsGzippedTar(Stream stream)
    {
        long origin = stream.Position;

        try
        {
            Span<byte> signature = stackalloc byte[3];
            int read = stream.ReadAtLeast(signature, signature.Length, throwOnEndOfStream: false);
            stream.Position = origin;

            if (!GzipHeader.HasSignature(signature[..read]))
            {
                return false;
            }

            using GzipStreamReader reader = new(stream, long.MaxValue);
            byte[] block = new byte[BlockSize];
            return reader.ReadAtLeast(block, BlockSize, throwOnEndOfStream: false) == BlockSize && IsHeader(block);
        }
        catch (TabularException)
        {
            return false;
        }
        finally
        {
            stream.Position = origin;
        }
    }

    /// <summary>Octal digits, after optional spaces, ended by NUL or space; null when it is not a number.</summary>
    private static long? ReadChecksum(ReadOnlySpan<byte> field)
    {
        int at = 0;

        while (at < field.Length && field[at] == (byte)' ')
        {
            at++;
        }

        long value = 0;
        int digits = 0;

        for (; at < field.Length && field[at] is >= (byte)'0' and <= (byte)'7'; at++, digits++)
        {
            value = (value * 8) + (field[at] - '0');
        }

        return digits > 0 && (at == field.Length || field[at] is 0 or (byte)' ') ? value : null;
    }

    /// <summary>The sum of the header's bytes with the checksum field read as spaces; some old writers summed signed bytes.</summary>
    private static long Sum(ReadOnlySpan<byte> header, bool signed)
    {
        long sum = 0;

        for (int i = 0; i < header.Length; i++)
        {
            bool inChecksum = i is >= ChecksumOffset and < ChecksumOffset + ChecksumLength;
            byte b = inChecksum ? (byte)' ' : header[i];
            sum += signed ? (sbyte)b : b;
        }

        return sum;
    }
}
