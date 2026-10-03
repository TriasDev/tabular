using System.Formats.Tar;

namespace TriasDev.Tabular.Archive;

/// <summary>
/// The entries of a tar compressed with gzip, which can only be read as a stream: reaching an entry
/// means decompressing everything before it.
/// </summary>
/// <remarks>
/// One pass lists the entries. Opening an entry afterwards goes on from where the pass stands when
/// the entry lies further on — so reading the sheets in archive order costs one pass — and starts
/// a new pass otherwise. Nothing is held but the pass itself.
/// </remarks>
internal sealed class TarGzContainer(Stream file, long limit) : ArchiveContainer
{
    private readonly long _origin = file.Position;
    private GzipStreamReader? _gzip;
    private TailStream? _tail;
    private TarReader? _tar;

    /// <summary>The ordinal of the entry the pass stands at; -1 before the first, <see cref="int.MaxValue"/> after the last.</summary>
    private int _current = -1;

    private Stream? _currentData;
    private bool _currentOpened;

    public override TabularFormat Format => TabularFormat.Tar;

    public override IReadOnlyList<ArchiveEntry>? Directory => null;

    public override IEnumerable<ArchiveEntry> Entries(CancellationToken cancellationToken)
    {
        Restart();

        while (Advance() is { } entry)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return entry;
        }
    }

    public override Stream Open(ArchiveEntry entry, CancellationToken cancellationToken)
    {
        bool currentAndUnread = entry.Ordinal == _current && !_currentOpened;

        if (!currentAndUnread)
        {
            if (entry.Ordinal <= _current)
            {
                Restart();
            }

            while (_current < entry.Ordinal)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (Advance() is null)
                {
                    // The same bytes gave this entry on the listing pass.
                    throw new TabularFormatException(TabularFormatException.Corrupt, "The tar archive changed while it was read.");
                }
            }
        }

        _currentOpened = true;
        return new EntryStream(_currentData ?? Stream.Null);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _tar?.Dispose();
            _gzip?.Dispose();
        }
    }

    private void Restart()
    {
        _tar?.Dispose();
        _gzip?.Dispose();
        file.Position = _origin;
        _gzip = new GzipStreamReader(file, limit);
        _tail = new TailStream(_gzip);
        _tar = new TarReader(_tail, leaveOpen: true);
        _current = -1;
        _currentData = null;
        _currentOpened = false;
    }

    /// <summary>Moves the pass to the next entry — reading past the rest of the current one — or to the end.</summary>
    private ArchiveEntry? Advance()
    {
        TarEntry? entry;

        try
        {
            entry = _tar!.GetNextEntry(copyData: false);
        }
        catch (Exception e) when (TarEntries.IsFailure(e))
        {
            throw TarEntries.Map(e);
        }

        if (entry is null)
        {
            // TarReader stops at the first zero block. The rest — padding and the gzip trailer — is
            // read too, so the checksum of the whole file is checked and a cut there is not missed.
            _gzip!.CopyTo(Stream.Null);
            _current = int.MaxValue;
            _currentData = null;
            return null;
        }

        _current++;
        _currentData = entry.DataStream;
        _currentOpened = false;
        // The data is not read yet, so the last block the reader took is the entry's own ustar header.
        string path = TarEntries.NeedsHeader(entry) ? TarEntries.PathOf(entry, _tail!.LastBlock) : entry.Name;
        return TarEntries.ToEntry(entry, path, _current, -1);
    }

    /// <summary>A pass-through that remembers the last 512 bytes read through it.</summary>
    private sealed class TailStream(Stream inner) : Stream
    {
        private readonly byte[] _ring = new byte[TarHeader.BlockSize];
        private long _total;

        /// <summary>The last block read, oldest byte first; empty before a whole block has passed.</summary>
        public ReadOnlySpan<byte> LastBlock
        {
            get
            {
                if (_total < TarHeader.BlockSize)
                {
                    return [];
                }

                int start = (int)(_total % TarHeader.BlockSize);
                return start == 0 ? _ring : [.. _ring.AsSpan(start), .. _ring.AsSpan(0, start)];
            }
        }

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
            int read = inner.Read(buffer);
            ReadOnlySpan<byte> taken = buffer[..read];

            if (taken.Length >= TarHeader.BlockSize)
            {
                // The ring then holds the last block in order from its start: any whole number of
                // blocks counted says so, as only the count's remainder and its reaching one block matter.
                taken[^TarHeader.BlockSize..].CopyTo(_ring);
                _total = TarHeader.BlockSize;
            }
            else
            {
                foreach (byte b in taken)
                {
                    _ring[(int)(_total % TarHeader.BlockSize)] = b;
                    _total++;
                }
            }

            return read;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>An entry's data as the pass hands it over, with <see cref="TarReader"/>'s failures in the library's terms; owns nothing.</summary>
    private sealed class EntryStream(Stream data) : Stream
    {
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
            try
            {
                return data.Read(buffer);
            }
            catch (Exception e) when (TarEntries.IsFailure(e))
            {
                throw TarEntries.Map(e);
            }
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
