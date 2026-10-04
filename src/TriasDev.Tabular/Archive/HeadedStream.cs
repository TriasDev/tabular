namespace TriasDev.Tabular.Archive;

/// <summary>
/// The bytes already read from a forward stream, put back in front of the rest of it — so an entry
/// whose head was read to judge it can be copied whole without being opened again, which an entry of
/// a compressed tar cannot be cheaply.
/// </summary>
internal sealed class HeadedStream(byte[] head, Stream rest) : Stream
{
    private int _headRead;

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
        if (_headRead < head.Length)
        {
            int count = Math.Min(buffer.Length, head.Length - _headRead);
            head.AsSpan(_headRead, count).CopyTo(buffer);
            _headRead += count;
            return count;
        }

        return rest.Read(buffer);
    }

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
