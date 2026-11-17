using System.Buffers;
using System.IO.Compression;

using TriasDev.Tabular.Archive;

namespace TriasDev.Tabular;

/// <summary>
/// A zip entry's data, held to the CRC-32 and size the zip's directory states for it once it has
/// been read to its end.
/// </summary>
/// <remarks>
/// <see cref="ZipArchiveEntry.Open"/> checks neither, so a damaged entry read as data: a digit
/// changed in a stored csv read as another number, and sixteen bytes overwritten in a deflated one
/// inflated into more rows than the file held — all without a word. The check costs a CRC over
/// every byte read, eight bytes a step (one instruction on ARM64), and nothing else.
///
/// It can only judge an entry read to its end. A reader that stops early — at the end of a
/// worksheet's cell data, or at the end of an XML part's root — calls <see cref="ReadToEnd"/> where
/// it stops, which reads what is left (a small tail in every real file) and judges the whole. A
/// reader abandoned part-way is not judged; what it returned before stopping is still checked
/// against nothing, as it was before.
/// </remarks>
internal sealed class CheckedEntryStream : Stream
{
    private readonly Stream _inner;
    private readonly string _name;
    private readonly uint _statedCrc;
    private readonly long _statedLength;
    private uint _crc;
    private long _length;
    private bool _judged;

    private CheckedEntryStream(Stream inner, string name, uint statedCrc, long statedLength)
    {
        _inner = inner;
        _name = name;
        _statedCrc = statedCrc;
        _statedLength = statedLength;
    }

    /// <summary>Opens the entry's data, to be checked when it has been read to its end.</summary>
    public static CheckedEntryStream Open(ZipArchiveEntry entry) =>
        new(entry.Open(), entry.FullName, entry.Crc32, entry.Length);

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        int read = _inner.Read(buffer);
        Take(buffer[..read], asked: buffer.Length);
        return read;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        int read = await _inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        Take(buffer.Span[..read], asked: buffer.Length);
        return read;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    /// <summary>Reads what is left of the entry and judges it; throws when it is damaged.</summary>
    /// <exception cref="InvalidDataException">The data does not match the CRC-32 or size its directory states.</exception>
    public void ReadToEnd()
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(16 * 1024);

        try
        {
            int read;

            do
            {
                read = Read(buffer);
            }
            while (read > 0);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _inner.Dispose();
        }

        base.Dispose(disposing);
    }

    private void Take(ReadOnlySpan<byte> read, int asked)
    {
        if (!read.IsEmpty)
        {
            _crc = Crc32.Append(_crc, read);
            _length += read.Length;
        }
        else if (asked > 0 && !_judged)
        {
            // The end of the entry: judged once, however often the end is read again.
            _judged = true;

            if (_length != _statedLength || _crc != _statedCrc)
            {
                throw new InvalidDataException(
                    $"The zip entry {_name} is damaged: its data does not match the checksum and size the zip's directory states for it.");
            }
        }
    }
}
