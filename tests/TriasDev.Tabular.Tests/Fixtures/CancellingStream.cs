

namespace TriasDev.Tabular.Tests.Fixtures;

/// <summary>
/// A stream that cancels a token once it has served a given number of bytes, counted from when it is
/// told to — so a read can be interrupted at a point of the test's choosing, not before it begins.
/// </summary>
public sealed class CancellingStream(Stream inner, CancellationTokenSource source) : Stream
{
    private long _served;
    private long _after = long.MaxValue;

    public CancellingStream(byte[] content, CancellationTokenSource source)
        : this(new MemoryStream(content, writable: false), source)
    {
    }

    /// <summary>
    /// Cancels once this many bytes have been served from now on; anything read before — opening a
    /// package, listing its sheets — does not count.
    /// </summary>
    public void CancelAfter(long bytes)
    {
        _served = 0;
        _after = bytes;
    }

    public override bool CanRead => true;

    public override bool CanSeek => inner.CanSeek;

    public override bool CanWrite => false;

    public override long Length => inner.Length;

    public override long Position
    {
        get => inner.Position;
        set => inner.Position = value;
    }

    public override int Read(byte[] buffer, int offset, int count) => Count(inner.Read(buffer, offset, count));

    public override int Read(Span<byte> buffer) => Count(inner.Read(buffer));

    public override int ReadByte() => Count(inner.ReadByte());

    public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);

    public override void Flush()
    {
        // Read-only: nothing to flush.
    }

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            inner.Dispose();
        }

        base.Dispose(disposing);
    }

    private int Count(int read)
    {
        if (_after != long.MaxValue)
        {
            _served += Math.Max(read, 1);

            if (_served >= _after)
            {
                source.Cancel();
            }
        }

        return read;
    }
}
