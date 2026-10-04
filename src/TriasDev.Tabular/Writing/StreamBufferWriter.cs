using System.Buffers;

namespace TriasDev.Tabular;

/// <summary>
/// An <see cref="IBufferWriter{T}"/> over a stream: committed bytes collect in one reused working
/// buffer and are written to <see cref="Target"/> when it nears full and on <see cref="Flush"/> — the
/// way a formatter that writes into a buffer writer can write into a zip entry's deflate stream
/// without a stream call per row.
/// </summary>
/// <remarks>
/// A request larger than the working buffer is lent a one-off larger one, whose bytes are written as
/// soon as they are committed and which is then dropped, so a huge row does not stay in memory for
/// the rest of the file.
/// </remarks>
internal sealed class StreamBufferWriter : IBufferWriter<byte>
{
    private readonly byte[] _working;
    private byte[] _buffer;
    private int _filled;

    public StreamBufferWriter(int workingSize = 64 * 1024)
    {
        _working = new byte[workingSize];
        _buffer = _working;
    }

    /// <summary>Where committed bytes go.</summary>
    public Stream? Target { get; set; }

    /// <summary>The buffer's current size, for the test that a huge row does not keep it.</summary>
    internal int BufferLength => _buffer.Length;

    public void Advance(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(count, _buffer.Length - _filled);
        _filled += count;

        if (!ReferenceEquals(_buffer, _working))
        {
            Flush();
        }
    }

    public Memory<byte> GetMemory(int sizeHint = 0)
    {
        Reserve(sizeHint);
        return _buffer.AsMemory(_filled);
    }

    public Span<byte> GetSpan(int sizeHint = 0)
    {
        Reserve(sizeHint);
        return _buffer.AsSpan(_filled);
    }

    /// <summary>Writes the committed bytes to <see cref="Target"/> and returns to the working buffer.</summary>
    public void Flush()
    {
        if (_filled > 0)
        {
            if (Target is null)
            {
                throw new InvalidOperationException("The buffer writer has no target stream to write to.");
            }

            Target.Write(_buffer, 0, _filled);
            _filled = 0;
        }

        _buffer = _working;
    }

    private void Reserve(int sizeHint)
    {
        int needed = Math.Max(sizeHint, 1);

        if (needed <= _buffer.Length - _filled)
        {
            return;
        }

        Flush();

        if (needed > _working.Length)
        {
            _buffer = new byte[needed];
        }
    }
}
