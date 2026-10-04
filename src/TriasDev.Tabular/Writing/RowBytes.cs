using System.Buffers;

namespace TriasDev.Tabular;

/// <summary>
/// The encoded bytes of many rows, collected in one reused buffer and written to a sheet's deflate
/// stream in a single call when the next row would not fit, so the compressor is entered once per
/// buffer rather than once per row.
/// </summary>
/// <remarks>
/// The deflate stream sees the same bytes in the same order, only in larger writes. A request larger
/// than the buffer (a hint of <see cref="IBufferWriter{T}.GetSpan"/> past <see cref="BufferSize"/>)
/// grows it for that request only: <see cref="Flush"/> lets it go.
/// </remarks>
internal sealed class RowBytes : IBufferWriter<byte>
{
    internal const int BufferSize = 64 * 1024;

    private byte[] _buffer = new byte[BufferSize];
    private int _count;
    private Stream? _target;

    /// <summary>The bytes waiting to be written.</summary>
    public int Count => _count;

    /// <summary>The buffer's size, for the test that a huge request does not keep it.</summary>
    public int Capacity => _buffer.Length;

    /// <summary>Starts collecting for a stream; anything still waiting for the previous one must have been flushed.</summary>
    public void Begin(Stream target)
    {
        _count = 0;
        _target = target;
    }

    /// <summary>Writes what is waiting to the stream, and lets a buffer grown past its size go.</summary>
    public void Flush()
    {
        if (_count > 0)
        {
            _target!.Write(_buffer, 0, _count);
            _count = 0;
        }

        if (_buffer.Length > BufferSize)
        {
            _buffer = new byte[BufferSize];
        }
    }

    public void Advance(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(count, _buffer.Length - _count);
        _count += count;
    }

    public Memory<byte> GetMemory(int sizeHint = 0)
    {
        Ensure(sizeHint);
        return _buffer.AsMemory(_count);
    }

    public Span<byte> GetSpan(int sizeHint = 0)
    {
        Ensure(sizeHint);
        return _buffer.AsSpan(_count);
    }

    private void Ensure(int sizeHint)
    {
        int needed = Math.Max(sizeHint, 1);

        if (_buffer.Length - _count >= needed)
        {
            return;
        }

        Flush();

        if (_buffer.Length < needed)
        {
            _buffer = new byte[needed];
        }
    }
}
