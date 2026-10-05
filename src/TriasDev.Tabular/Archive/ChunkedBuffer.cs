namespace TriasDev.Tabular.Archive;

/// <summary>
/// A file copied into memory in pieces and read back with random access — a workbook inside an
/// archive, which is itself a zip and cannot be read from a forward-only entry stream.
/// </summary>
/// <remarks>
/// In pieces rather than one array because an array stops short of two gigabytes, and a caller with
/// memory to spare may allow a larger workbook. A piece is one megabyte, or the file's declared size
/// when that is smaller, so a small workbook does not cost a megabyte: a piece that fills is followed
/// by a one-byte probe, and the next piece is allocated only when the source goes on.
/// </remarks>
internal sealed class ChunkedBuffer : Stream
{
    internal const int PieceSize = 1024 * 1024;

    private readonly List<byte[]> _pieces = [];
    private long _length;
    private long _position;
    private bool _disposed;

    private ChunkedBuffer()
    {
    }

    /// <summary>Copies a stream to its end, refusing it once it passes the limit.</summary>
    /// <param name="source">What to copy.</param>
    /// <param name="sizeHint">The size the source declares, for the first piece; it is not trusted.</param>
    /// <param name="limit">How many bytes may be held.</param>
    /// <param name="cancellationToken">Checked between pieces.</param>
    public static ChunkedBuffer CopyOf(Stream source, long sizeHint, long limit, CancellationToken cancellationToken)
    {
        ChunkedBuffer buffer = new();

        try
        {
            // A byte read ahead to learn whether the source goes on; it leads the next piece.
            int lead = -1;

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                long wanted = buffer._pieces.Count == 0 && sizeHint > 0 ? Math.Min(sizeHint, PieceSize) : PieceSize;
                byte[] piece = new byte[wanted];
                int filled = 0;

                if (lead >= 0)
                {
                    piece[0] = (byte)lead;
                    filled = 1;
                }

                filled += source.ReadAtLeast(piece.AsSpan(filled), piece.Length - filled, throwOnEndOfStream: false);
                buffer.Append(piece, filled, limit);

                // A short read is the end: ReadAtLeast stops short only there. A full piece may be the
                // end too — the declared size is the commonest one — so one byte is probed before a
                // whole piece is allocated to find out.
                if (filled < piece.Length || (lead = ReadOneByte(source)) < 0)
                {
                    return buffer;
                }
            }
        }
        catch
        {
            buffer.Dispose();
            throw;
        }
    }

    /// <summary>The next byte, or -1 at the end; without the array <see cref="Stream.ReadByte"/> allocates.</summary>
    private static int ReadOneByte(Stream source)
    {
        Span<byte> one = stackalloc byte[1];
        return source.Read(one) == 1 ? one[0] : -1;
    }

    private void Append(byte[] piece, int filled, long limit)
    {
        if (filled == 0)
        {
            return;
        }

        if (_length + filled > limit)
        {
            throw new TabularLimitException(nameof(ArchiveCursorOptions.MaxEmbeddedWorkbookBytes), limit,
                $"A workbook inside the archive or compressed file is larger than the {limit} bytes allowed.");
        }

        _pieces.Add(filled == piece.Length ? piece : piece[..filled]);
        _length += filled;
    }

    public override bool CanRead => !_disposed;

    public override bool CanSeek => !_disposed;

    public override bool CanWrite => false;

    public override long Length => _length;

    public override long Position
    {
        get => _position;
        set => _position = value >= 0 ? value : throw new ArgumentOutOfRangeException(nameof(value));
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        int total = 0;

        while (buffer.Length > 0 && _position < _length)
        {
            (byte[] piece, int at) = Locate(_position);
            int take = Math.Min(buffer.Length, piece.Length - at);
            piece.AsSpan(at, take).CopyTo(buffer);
            buffer = buffer[take..];
            _position += take;
            total += take;
        }

        return total;
    }

    /// <remarks>
    /// A seek before the start is an <see cref="IOException"/>, as <see cref="FileStream"/> and
    /// <see cref="MemoryStream"/> make it, so a caller written against those catches it here too;
    /// setting <see cref="Position"/> to a negative value stays an argument error, as it is there.
    /// </remarks>
    public override long Seek(long offset, SeekOrigin origin)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        long target = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _position + offset,
            SeekOrigin.End => _length + offset,
            _ => throw new ArgumentOutOfRangeException(nameof(origin)),
        };

        if (target < 0)
        {
            throw new IOException("An attempt was made to move the position before the beginning of the buffer.");
        }

        _position = target;
        return _position;
    }

    public override void Flush()
    {
        // Read-only: nothing to flush.
    }

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        _disposed = true;
        _pieces.Clear();
        _length = 0;
        base.Dispose(disposing);
    }

    /// <summary>The piece holding a position, and where in it.</summary>
    /// <remarks>
    /// Every piece is full size but the first (sized to the hint) and the last, so the piece is found
    /// by arithmetic past the first one rather than by walking the list.
    /// </remarks>
    private (byte[] Piece, int At) Locate(long position)
    {
        int first = _pieces[0].Length;

        if (position < first)
        {
            return (_pieces[0], (int)position);
        }

        long past = position - first;
        return (_pieces[1 + (int)(past / PieceSize)], (int)(past % PieceSize));
    }
}
