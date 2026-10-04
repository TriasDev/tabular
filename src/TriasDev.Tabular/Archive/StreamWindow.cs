namespace TriasDev.Tabular.Archive;

/// <summary>
/// A read-only, seekable view onto part of a seekable stream — an entry of an uncompressed archive,
/// read in place.
/// </summary>
/// <remarks>
/// It keeps its own position and seeks the stream before every read, so two windows onto one file —
/// a workbook being read and a csv being probed — never move each other. The stream stays open.
/// </remarks>
internal sealed class StreamWindow(Stream file, long offset, long length) : Stream
{
    private long _position;

    public override bool CanRead => true;

    public override bool CanSeek => true;

    public override bool CanWrite => false;

    public override long Length => length;

    public override long Position
    {
        get => _position;
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            _position = value;
        }
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        if (_position >= length || buffer.IsEmpty)
        {
            return 0;
        }

        int wanted = (int)Math.Min(buffer.Length, length - _position);
        file.Position = offset + _position;
        int read = file.Read(buffer[..wanted]);

        if (read == 0)
        {
            // The archive's header promised bytes the file does not have.
            throw new TabularFormatException(TabularFormatException.Truncated,
                "The archive is cut off: a file inside it ends past the end of the archive.");
        }

        _position += read;
        return read;
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        Position = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _position + offset,
            SeekOrigin.End => length + offset,
            _ => throw new ArgumentOutOfRangeException(nameof(origin)),
        };

        return _position;
    }

    public override void Flush()
    {
    }

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
