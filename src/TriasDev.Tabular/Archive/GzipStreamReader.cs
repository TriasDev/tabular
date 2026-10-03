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
/// finally returns nothing goes on reading past the end of the data — measured on a single member,
/// to the end of the file — while every read that returns data stops as soon as it has some. So the
/// end usually lies in the last input read made before that final read, or in the first made during
/// it, and the trailer is looked for there. A long run of output-free blocks can push the end
/// further, into a later read of that final call; when the narrow window holds no trailer the reader
/// falls back to every byte that call read before refusing. A false match needs 64 bits to agree by
/// chance.
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
    public long FilePosition => _feed.Furthest;

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

    /// <summary>
    /// The first place the trailer fits that is also followed by a member, or by the end of the file;
    /// failing that, the first place it fits.
    /// </summary>
    /// <remarks>
    /// A trailer of zeros — an empty member's — also fits one byte early when the deflate data ends
    /// in a zero, as a flushed empty member's <c>00 00 00 FF FF 03 00</c> does. Taking that first
    /// fit would leave the reader on the trailer's last byte, ending the file and dropping every
    /// member after it. A true end is followed by <c>1F</c> or by nothing; an early one by a zero.
    /// </remarks>
    private static int FindTrailer(ReadOnlySpan<byte> window, int earliest, uint crc, uint length, long windowStart, long fileLength,
        int until = int.MaxValue)
    {
        int firstFit = -1;

        for (int at = earliest; at + 8 <= window.Length && at < until; at++)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(window[at..]) != crc
                || BinaryPrimitives.ReadUInt32LittleEndian(window[(at + 4)..]) != length)
            {
                continue;
            }

            firstFit = firstFit < 0 ? at : firstFit;
            long after = windowStart + at + 8;

            if (after == fileLength || (at + 8 < window.Length && window[at + 8] == 0x1F))
            {
                return at;
            }
        }

        return firstFit;
    }

    /// <summary>
    /// The same choice over everything the final decompressor call read, in chunks — a long run of
    /// output-free blocks can leave the end of the data far behind the narrow window. Each chunk
    /// reads the 8 bytes of a trailer and the one after it past the starts it judges, so a match
    /// straddling two chunks is seen whole. Returns the file position, or -1.
    /// </summary>
    private long FindTrailerWide(long start, long end, uint crc, uint length)
    {
        const int chunk = 64 * 1024;
        const int overlap = 8 + 1;

        byte[] buffer = new byte[chunk + overlap];
        long fileLength = _file.Length;
        long firstFit = -1;

        for (long at = start; at <= end; at += chunk)
        {
            _file.Position = at;
            int available = _file.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
            int until = (int)Math.Min(chunk, end - at + 1);
            int hit = FindTrailer(buffer.AsSpan(0, available), 0, crc, length, at, fileLength, until);

            if (hit < 0)
            {
                continue;
            }

            // FindTrailer answers its preferred match, else its first fit; only the preferred one
            // ends the search, any other waits for a later chunk to offer one.
            long position = at + hit;

            if (position + 8 == fileLength || (hit + 8 < available && buffer[hit + 8] == 0x1F))
            {
                return position;
            }

            firstFit = firstFit < 0 ? position : firstFit;
        }

        return firstFit;
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
        byte[] window = new byte[checked((int)(to - from)) + 9];
        _file.Position = from;
        int available = _file.ReadAtLeast(window, window.Length, throwOnEndOfStream: false);

        // Never inside the shortest deflate stream: an empty member's trailer is eight zero bytes,
        // and its own 03 00 would otherwise lend it the first one.
        int earliest = (int)Math.Max(0, _feed.Start + MinDeflateBytes - from);
        int trailer = FindTrailer(window.AsSpan(0, available), earliest, _crc, unchecked((uint)_memberLength), from, _file.Length);

        long found = trailer < 0 ? -1 : from + trailer;

        if (found < 0)
        {
            found = FindTrailerWide(from + earliest, _feed.End, _crc, unchecked((uint)_memberLength));
        }

        if (found < 0)
        {
            // Data that ran to the end of the file was cut off there; data that ended before it had
            // a trailer that does not match. A wrong trailer that is the file's last eight bytes
            // reads as the first — both are refused.
            throw to + 8 > _file.Length
                ? GzipHeader.Truncated()
                : GzipHeader.Corrupt("its content does not match the checksum and size its trailer records");
        }

        long next = found + 8;
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

        /// <summary>The file position after the last read of the current member.</summary>
        public long End { get; private set; }

        /// <summary>The furthest the file has been read: never behind an earlier answer, though a member starts back at its own beginning.</summary>
        public long Furthest { get; private set; }

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
            Furthest = Math.Max(Furthest, End);
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
                Furthest = Math.Max(Furthest, End);

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
