using System.Buffers.Binary;
using System.IO.Compression;

namespace TriasDev.Tabular.Archive;

/// <summary>
/// The content of a gzip file, decompressed, as a forward-only stream — every member's trailer
/// checked, so a file cut off or damaged is refused rather than read as a shorter one.
/// </summary>
/// <remarks>
/// <para>
/// Not <see cref="GZipStream"/>: on .NET 8 and 10 it reads a file cut off anywhere in its compressed
/// data as a prefix of the content, and reports success — an upload cut short would be imported as
/// a smaller file, its last row half there. Raw <see cref="DeflateStream"/> does the same, but leaves
/// the gzip framing to us, and the trailer is where the truth is: the CRC-32 and the size of what
/// the member holds.
/// </para>
/// <para>
/// A <see cref="DeflateStream"/> does not say where its data ended, and reads past it: the read that
/// finally returns nothing goes on reading the file to its end, while every read that returns data
/// stops as soon as it has some. So the end lies in the last input read made before that final
/// read, or in the first made during it, and the trailer is looked for there. A false match needs
/// 64 bits to agree by chance.
/// </para>
/// </remarks>
internal sealed class GzipStreamReader : Stream
{
    /// <summary>The shortest deflate stream, an empty fixed block: <c>03 00</c>.</summary>
    private const int MinDeflateBytes = 2;

    private readonly Stream _file;
    private readonly long _limit;
    private readonly Feed _feed;
    private DeflateStream _deflate;
    private uint _crc;
    private long _memberLength;
    private long _total;
    private bool _ended;
    private bool _disposed;

    /// <param name="file">The gzip file, seekable, positioned at its first header. Left open.</param>
    /// <param name="limit">How many decompressed bytes may be read, all members together.</param>
    public GzipStreamReader(Stream file, long limit)
    {
        _file = file;
        _limit = limit;
        _feed = new Feed(file);
        Name = GzipHeader.Read(file);
        _deflate = StartMember();
    }

    /// <summary>The original file name the first member's header stores, or null.</summary>
    public string? Name { get; }

    /// <summary>How far into the file the compressed data has been read, for progress.</summary>
    public long FilePosition => _feed.End;

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
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (buffer.IsEmpty)
        {
            return 0;
        }

        while (!_ended)
        {
            _feed.Mark();
            int read;

            try
            {
                read = _deflate.Read(buffer);
            }
            catch (InvalidDataException e)
            {
                // The decompressor's own message names an "unsupported compression method"; the
                // truth is plainer.
                throw new TabularFormatException(TabularFormatException.Corrupt,
                    "The gzip file is damaged: its compressed data cannot be decoded.", e);
            }

            if (read > 0)
            {
                _total += read;

                if (_total > _limit)
                {
                    throw new TabularLimitException(nameof(ArchiveCursorOptions.MaxUncompressedBytes), _limit,
                        $"The compressed file expands to more than the {_limit} bytes allowed.");
                }

                _crc = Crc32.Append(_crc, buffer[..read]);
                _memberLength += read;
                return read;
            }

            FinishMember();
        }

        return 0;
    }

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            _disposed = true;
            _deflate.Dispose();
        }

        base.Dispose(disposing);
    }

    private static int FindTrailer(ReadOnlySpan<byte> window, int earliest, uint crc, uint length)
    {
        for (int at = earliest; at + 8 <= window.Length; at++)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(window[at..]) == crc
                && BinaryPrimitives.ReadUInt32LittleEndian(window[(at + 4)..]) == length)
            {
                return at;
            }
        }

        return -1;
    }

    private DeflateStream StartMember()
    {
        _feed.Begin();
        _crc = 0;
        _memberLength = 0;
        return new DeflateStream(_feed, CompressionMode.Decompress, leaveOpen: true);
    }

    /// <summary>Finds and checks the trailer, then goes on to the next member or ends.</summary>
    private void FinishMember()
    {
        _deflate.Dispose();

        (long from, long to) = _feed.Window;
        byte[] window = new byte[checked((int)(to - from)) + 8];
        _file.Position = from;
        int available = _file.ReadAtLeast(window, window.Length, throwOnEndOfStream: false);

        // Never inside the shortest deflate stream: an empty member's trailer is eight zero bytes,
        // and its own 03 00 would otherwise lend it the first one.
        int earliest = (int)Math.Max(0, _feed.Start + MinDeflateBytes - from);
        int trailer = FindTrailer(window.AsSpan(0, available), earliest, _crc, unchecked((uint)_memberLength));

        if (trailer < 0)
        {
            // Data that ran to the end of the file was cut off there; data that ended before it had
            // a trailer that does not match. A wrong trailer that is the file's last eight bytes
            // reads as the first — both are refused.
            throw to + 8 > _file.Length
                ? GzipHeader.Truncated()
                : GzipHeader.Corrupt("its content does not match the checksum and size its trailer records");
        }

        long next = from + trailer + 8;
        _file.Position = next;
        int lead = _file.ReadByte();
        _file.Position = next;

        // 1F starts a member — judged by one byte, not two, so a file cut one byte into its next
        // member is refused as cut, not read as complete. Anything else is trailing bytes, which the
        // gzip tool and GZipStream both ignore.
        if (lead == 0x1F)
        {
            _ = GzipHeader.Read(_file);
            _deflate = StartMember();
            return;
        }

        _ended = true;
    }

    /// <summary>
    /// What the <see cref="DeflateStream"/> reads from: the file, at most 64 KB a read, remembering
    /// where the reads around the end of the data were.
    /// </summary>
    private sealed class Feed(Stream file) : Stream
    {
        private const int MaxRead = 64 * 1024;

        private long _lastRead = -1;
        private long _lastReadBeforeMark = -1;
        private long _firstReadAfterMarkEnd = -1;

        /// <summary>Where the current member's deflate data begins.</summary>
        public long Start { get; private set; }

        /// <summary>The file position after the last read.</summary>
        public long End { get; private set; }

        /// <summary>
        /// Where the trailer may start: from the last read before the final decompressor call — or
        /// the member's start, if there was none — to the end of the first read during it, or of
        /// everything read when it made none.
        /// </summary>
        public (long From, long To) Window =>
            (_lastReadBeforeMark >= 0 ? _lastReadBeforeMark : Start, _firstReadAfterMarkEnd >= 0 ? _firstReadAfterMarkEnd : End);

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public void Begin()
        {
            Start = End = file.Position;
            _lastRead = _lastReadBeforeMark = _firstReadAfterMarkEnd = -1;
        }

        /// <summary>Called before each decompressor call: the reads it makes are told apart from those before.</summary>
        public void Mark()
        {
            _lastReadBeforeMark = _lastRead;
            _firstReadAfterMarkEnd = -1;
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            int read = file.Read(buffer[..Math.Min(buffer.Length, MaxRead)]);

            if (read > 0)
            {
                _lastRead = End;
                End += read;

                if (_firstReadAfterMarkEnd < 0)
                {
                    _firstReadAfterMarkEnd = End;
                }
            }

            return read;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
