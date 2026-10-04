using System.Buffers;
using System.Text.Unicode;

namespace TriasDev.Tabular;

/// <summary>
/// The characters of one row as a sheet writer builds it, before it is encoded: reused from row to
/// row, so a sheet of millions of rows allocates nothing per row.
/// </summary>
/// <remarks>
/// Numbers and dates are formatted straight into it with <see cref="ISpanFormattable.TryFormat"/>.
/// A row larger than <see cref="RetainedChars"/> grows the buffer for its own sake only:
/// <see cref="Clear"/> lets it go, rather than keep a one-off note's buffer for the rest of the file.
/// </remarks>
internal sealed class RowText
{
    private const int InitialChars = 4 * 1024;

    internal const int RetainedChars = 1024 * 1024;

    /// <summary>The bytes asked of the output at a time when the row is encoded.</summary>
    private const int EncodePiece = 8 * 1024;

    private char[] _chars = new char[InitialChars];

    /// <summary>How many characters the row holds.</summary>
    public int Length { get; private set; }

    /// <summary>The row so far.</summary>
    public ReadOnlySpan<char> Written => _chars.AsSpan(0, Length);

    /// <summary>The buffer's size, for the test that a huge row does not keep it.</summary>
    public int Capacity => _chars.Length;

    /// <summary>Empties the row, and lets a buffer grown past the retained size go.</summary>
    public void Clear()
    {
        Length = 0;

        if (_chars.Length > RetainedChars)
        {
            _chars = new char[InitialChars];
        }
    }

    public void Append(char value)
    {
        Reserve(1);
        _chars[Length++] = value;
    }

    public void Append(ReadOnlySpan<char> value)
    {
        Reserve(value.Length);
        value.CopyTo(_chars.AsSpan(Length));
        Length += value.Length;
    }

    /// <summary>Formats a value into the row, growing it until the value fits; returns where it starts.</summary>
    public int AppendFormatted<T>(T value, ReadOnlySpan<char> format, IFormatProvider? provider)
        where T : ISpanFormattable
    {
        Reserve(64);
        int start = Length;
        int written;

        while (!value.TryFormat(_chars.AsSpan(start), out written, format, provider))
        {
            Array.Resize(ref _chars, _chars.Length * 2);
        }

        Length += written;
        return start;
    }

    /// <summary>Drops everything from <paramref name="length"/> on.</summary>
    public void Truncate(int length)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(length, Length);
        Length = length;
    }

    /// <summary>
    /// Encodes the row as UTF-8 into the output, in pieces: never asks the output for more than
    /// <see cref="EncodePiece"/> bytes at once, however long the row is (the worst case for the
    /// whole row is three bytes a character).
    /// </summary>
    public void WriteUtf8To(IBufferWriter<byte> output)
    {
        ReadOnlySpan<char> rest = Written;

        while (true)
        {
            OperationStatus status = Utf8.FromUtf16(rest, output.GetSpan(Math.Min(EncodePiece, (rest.Length + 1) * 3)), out int read, out int written, replaceInvalidSequences: true, isFinalBlock: true);
            output.Advance(written);
            rest = rest[read..];

            if (status != OperationStatus.DestinationTooSmall)
            {
                return;
            }
        }
    }

    private void Reserve(int extra)
    {
        if (Length + extra > _chars.Length)
        {
            Array.Resize(ref _chars, Math.Max(_chars.Length * 2, Length + extra));
        }
    }
}
