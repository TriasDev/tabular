namespace TriasDev.Tabular.Archive;

/// <summary>
/// A file copied into memory in pieces and read back with random access — a workbook inside an
/// archive, which is itself a zip and cannot be read from a forward-only entry stream.
/// </summary>
/// <remarks>
/// In pieces rather than one array because an array stops short of two gigabytes, and a caller with
/// memory to spare may allow a larger workbook. A piece is one megabyte, or the file's declared size
/// when that is smaller, so a small workbook does not cost a megabyte.
/// </remarks>
internal sealed class ChunkedBuffer : Stream
{
    internal const int PieceSize = 1024 * 1024;

    private readonly List<byte[]> _pieces = [];
    private long _length;
    private long _position;

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
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                long wanted = buffer._pieces.Count == 0 && sizeHint > 0 ? Math.Min(sizeHint, PieceSize) : PieceSize;
                byte[] piece = new byte[wanted];
                int filled = source.ReadAtLeast(piece, piece.Length, throwOnEndOfStream: false);

                if (filled == 0)
                {
                    return buffer;
                }

                if (buffer._length + filled > limit)
                {
                    throw new TabularLimitException(nameof(ArchiveCursorOptions.MaxEmbeddedWorkbookBytes), limit,
                        $"A workbook inside the archive is larger than the {limit} bytes allowed.");
                }

                buffer._pieces.Add(filled == piece.Length ? piece : piece[..filled]);
                buffer._length += filled;

                if (filled < piece.Length && buffer._pieces.Count > 1)
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

    public override bool CanRead => true;

    public override bool CanSeek => true;

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

    public override long Seek(long offset, SeekOrigin origin)
    {
        Position = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _position + offset,
            SeekOrigin.End => _length + offset,
            _ => throw new ArgumentOutOfRangeException(nameof(origin)),
        };

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
