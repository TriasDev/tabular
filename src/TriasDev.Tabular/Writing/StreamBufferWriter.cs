using System.Buffers;

namespace TriasDev.Tabular;

/// <summary>
/// An <see cref="IBufferWriter{T}"/> over a stream: lends one reused buffer and writes every
/// committed span to <see cref="Target"/> at once — the way a formatter that writes into a buffer
/// writer can write into a zip entry's deflate stream.
/// </summary>
internal sealed class StreamBufferWriter(int initialSize = 16 * 1024) : IBufferWriter<byte>
{
    private byte[] _buffer = new byte[initialSize];

    /// <summary>Where committed bytes go.</summary>
    public Stream? Target { get; set; }

    public void Advance(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(count, _buffer.Length);
        Target!.Write(_buffer, 0, count);
    }

    public Memory<byte> GetMemory(int sizeHint = 0) => Lend(sizeHint);

    public Span<byte> GetSpan(int sizeHint = 0) => Lend(sizeHint);

    private byte[] Lend(int sizeHint)
    {
        if (sizeHint > _buffer.Length)
        {
            _buffer = new byte[Math.Max(sizeHint, _buffer.Length * 2)];
        }

        return _buffer;
    }
}
