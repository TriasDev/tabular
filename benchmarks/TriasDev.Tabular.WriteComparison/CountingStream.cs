namespace TriasDev.Tabular.WriteComparison;

/// <summary>
/// A write-only, forward-only stream that counts what it is given and discards it: the target of the timed
/// write, so that no library's time includes the file system's. Given an inner stream, it passes the bytes on
/// instead, so the untimed write for the check sees the same non-seekable target and writes the same bytes
/// (a zip written to a stream that cannot seek carries data descriptors, one written to a file does not).
/// </summary>
internal sealed class CountingStream(Stream? inner = null) : Stream
{
    /// <summary>The bytes written so far.</summary>
    public long Count { get; private set; }

    public override bool CanRead => false;

    public override bool CanSeek => false;

    public override bool CanWrite => true;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => Count;
        set => throw new NotSupportedException();
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        ValidateBufferArguments(buffer, offset, count);
        Write(buffer.AsSpan(offset, count));
    }

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        inner?.Write(buffer);
        Count += buffer.Length;
    }

    public override void WriteByte(byte value) => Write([value]);

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        Write(buffer, offset, count);
        return Task.CompletedTask;
    }

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        Write(buffer.Span);
        return ValueTask.CompletedTask;
    }

    public override void Flush() => inner?.Flush();

    public override Task FlushAsync(CancellationToken cancellationToken)
    {
        Flush();
        return Task.CompletedTask;
    }

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();
}
