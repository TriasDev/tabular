using System.Buffers;

namespace TriasDev.Tabular;

/// <summary>
/// Memory the format writers write into synchronously, emptied into the target asynchronously.
/// </summary>
/// <remarks>
/// <para>
/// The split is what lets a writer serve an ASP.NET Core response body, which refuses synchronous
/// writes: the csv text, the sheet XML and the zip writer are synchronous code, and only
/// <see cref="DrainToAsync"/> ever touches the target.
/// </para>
/// <para>
/// Pooled segments rather than one growing array, so a buffer that grows never copies what it holds
/// and never lands on the large object heap. Bounded by the caller: a writer drains it whenever it
/// passes the flush threshold.
/// </para>
/// </remarks>
internal sealed class SpillBuffer : Stream, IBufferWriter<byte>
{
    private const int SegmentSize = 64 * 1024;

    private readonly List<byte[]> _segments = [];
    private readonly List<int> _used = [];

    /// <summary>Bytes written and not yet drained.</summary>
    public long Pending { get; private set; }

    /// <summary>Bytes written over the buffer's life, drained or not — the offsets a zip writer records.</summary>
    public long TotalWritten { get; private set; }

    public override bool CanRead => false;

    public override bool CanSeek => false;

    public override bool CanWrite => true;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public Span<byte> GetSpan(int sizeHint = 0) => GetMemory(sizeHint).Span;

    public Memory<byte> GetMemory(int sizeHint = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(sizeHint);
        int needed = Math.Max(sizeHint, 1);
        int last = _segments.Count - 1;

        if (last < 0 || _segments[last].Length - _used[last] < needed)
        {
            _segments.Add(ArrayPool<byte>.Shared.Rent(Math.Max(needed, SegmentSize)));
            _used.Add(0);
            last++;
        }

        return _segments[last].AsMemory(_used[last]);
    }

    public void Advance(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        int last = _segments.Count - 1;

        if (last < 0 || _used[last] + count > _segments[last].Length)
        {
            throw new InvalidOperationException("Advanced past the span the buffer handed out.");
        }

        _used[last] += count;
        Pending += count;
        TotalWritten += count;
    }

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        while (!buffer.IsEmpty)
        {
            Span<byte> target = GetSpan();
            int count = Math.Min(target.Length, buffer.Length);
            buffer[..count].CopyTo(target);
            Advance(count);
            buffer = buffer[count..];
        }
    }

    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

    public override void WriteByte(byte value)
    {
        GetSpan(1)[0] = value;
        Advance(1);
    }

    public override void Flush()
    {
        // Nothing to do: draining is DrainToAsync's job, and only the writer decides when.
    }

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    /// <summary>Writes everything pending into the target, in order, and empties the buffer.</summary>
    public async ValueTask DrainToAsync(Stream target, CancellationToken cancellationToken)
    {
        for (int i = 0; i < _segments.Count; i++)
        {
            await target.WriteAsync(_segments[i].AsMemory(0, _used[i]), cancellationToken).ConfigureAwait(false);
        }

        Release();
    }

    /// <summary>Drops everything pending, unwritten.</summary>
    public void Discard() => Release();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Release();
        }

        base.Dispose(disposing);
    }

    private void Release()
    {
        foreach (byte[] segment in _segments)
        {
            ArrayPool<byte>.Shared.Return(segment);
        }

        _segments.Clear();
        _used.Clear();
        Pending = 0;
    }
}
