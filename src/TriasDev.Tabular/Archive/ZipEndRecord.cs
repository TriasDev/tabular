using System.Buffers;
using System.Buffers.Binary;

namespace TriasDev.Tabular.Archive;

/// <summary>
/// How many entries a zip's end-of-central-directory record declares — read from the record alone,
/// before anything walks the directory.
/// </summary>
/// <remarks>
/// <see cref="System.IO.Compression.ZipArchive"/> reads the whole directory the first time its
/// entries are asked for, so a bound counted while walking them comes after that cost, not before
/// it. The record at the end states the count in a few bytes: a zip whose record declares more
/// entries than any reader would accept is refused without a walk. A record that cannot be found or
/// read gives no count, and the directory is left to the zip reader, which reports the damage.
/// </remarks>
internal static class ZipEndRecord
{
    private const uint EndSignature = 0x06054b50;
    private const uint Zip64LocatorSignature = 0x07064b50;
    private const uint Zip64EndSignature = 0x06064b50;
    private const int EndLength = 22;
    private const int Zip64LocatorLength = 20;
    private const int Zip64EndLength = 56;

    /// <summary>The record stands at most this far from the end: itself and a comment of up to 65,535 bytes.</summary>
    private const int MaxTail = EndLength + ushort.MaxValue;

    /// <summary>
    /// The total entry count the record declares — the zip64 record's when the zip has one — or null
    /// when no record can be read. Leaves the stream's position where it was.
    /// </summary>
    public static long? DeclaredEntries(Stream stream)
    {
        long origin = stream.Position;

        try
        {
            long length = stream.Length;

            if (length < EndLength)
            {
                return null;
            }

            long? at = FindEnd(stream, length);

            if (at is not { } end)
            {
                return null;
            }

            Span<byte> record = stackalloc byte[EndLength];
            stream.Position = end;
            stream.ReadExactly(record);
            long count = BinaryPrimitives.ReadUInt16LittleEndian(record[10..]);

            // A zip64 writer saturates the count; the zip64 record then holds the real one. Taking the
            // larger of the two bounds a file that contradicts itself as its worse half.
            return Zip64Entries(stream, end) is { } zip64 ? Math.Max(count, zip64) : count;
        }
        catch (IOException)
        {
            return null;
        }
        finally
        {
            stream.Position = origin;
        }
    }

    /// <summary>
    /// Where the record starts: at the very end when the zip has no comment, which is nearly always,
    /// else found by searching back through the comment.
    /// </summary>
    private static long? FindEnd(Stream stream, long length)
    {
        Span<byte> last = stackalloc byte[EndLength];
        stream.Position = length - EndLength;
        stream.ReadExactly(last);

        if (BinaryPrimitives.ReadUInt32LittleEndian(last) == EndSignature)
        {
            return length - EndLength;
        }

        int tail = (int)Math.Min(length, MaxTail);
        byte[] rented = ArrayPool<byte>.Shared.Rent(tail);

        try
        {
            Span<byte> buffer = rented.AsSpan(0, tail);
            stream.Position = length - tail;
            stream.ReadExactly(buffer);

            for (int i = tail - EndLength; i >= 0; i--)
            {
                if (BinaryPrimitives.ReadUInt32LittleEndian(buffer[i..]) == EndSignature)
                {
                    return length - tail + i;
                }
            }

            return null;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    /// <summary>The zip64 record's total entry count, when a locator stands right before the record and points at one.</summary>
    private static long? Zip64Entries(Stream stream, long end)
    {
        if (end < Zip64LocatorLength)
        {
            return null;
        }

        Span<byte> locator = stackalloc byte[Zip64LocatorLength];
        stream.Position = end - Zip64LocatorLength;
        stream.ReadExactly(locator);

        if (BinaryPrimitives.ReadUInt32LittleEndian(locator) != Zip64LocatorSignature)
        {
            return null;
        }

        ulong recordAt = BinaryPrimitives.ReadUInt64LittleEndian(locator[8..]);

        if (recordAt > (ulong)(end - Zip64LocatorLength - Zip64EndLength))
        {
            return null;
        }

        Span<byte> record = stackalloc byte[Zip64EndLength];
        stream.Position = (long)recordAt;
        stream.ReadExactly(record);

        if (BinaryPrimitives.ReadUInt32LittleEndian(record) != Zip64EndSignature)
        {
            return null;
        }

        ulong count = BinaryPrimitives.ReadUInt64LittleEndian(record[32..]);
        return count > long.MaxValue ? long.MaxValue : (long)count;
    }
}
