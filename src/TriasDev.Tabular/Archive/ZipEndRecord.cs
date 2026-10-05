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
            long? zip64 = Zip64Entries(stream, end);

            // A saturated count is the sentinel that sends a reader to the zip64 record, as the format
            // and ZipArchive have it — and many writers, ours and Go's among them, saturate every field
            // once anything in the zip passes four gigabytes, so the sentinel says nothing about the
            // real count. Only a count that is not the sentinel and still contradicts the zip64 record
            // is bounded as its worse half.
            if (count == ushort.MaxValue)
            {
                return zip64 ?? count;
            }

            return zip64 is { } stated ? Math.Max(count, stated) : count;
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
    /// <remarks>
    /// A record and a locator take 76 bytes before the end record; an end record that starts sooner
    /// has no room for them, and its locator's offset — read from whatever bytes stand there — is not
    /// looked at. The offset is checked against the room before the locator in unsigned arithmetic, so
    /// a forged one never sets a negative position or one past the end.
    /// </remarks>
    private static long? Zip64Entries(Stream stream, long end)
    {
        if (end < Zip64LocatorLength + Zip64EndLength)
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
