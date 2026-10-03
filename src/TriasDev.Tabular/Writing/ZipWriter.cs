using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace TriasDev.Tabular;

/// <summary>
/// Writes a zip package forward into the spill buffer: the container of xlsx and ods.
/// </summary>
/// <remarks>
/// <para>
/// Our own because <see cref="ZipArchive"/> does not fit, measured while planning the write side:
/// written to a stream that cannot seek it puts a data descriptor on every entry, the stored ODF
/// <c>mimetype</c> too, and LibreOffice refuses that file; given one that can seek, it seeks back to
/// patch each local header, so a sheet of a million rows stays in memory until it closes.
/// </para>
/// <para>
/// Two kinds of entry. A small part is stored with its checksum and sizes in the local header and no
/// descriptor — what the ODF <c>mimetype</c> requires. A large part is deflated as it is written, its
/// checksum and sizes following it in a data descriptor. Zip64 records are written once a size, an
/// offset or the directory passes <c>zip64Threshold</c>, four gigabytes unless a test lowers it.
/// </para>
/// </remarks>
internal sealed class ZipWriter
{
    private const uint LocalHeaderSignature = 0x04034b50;
    private const uint DescriptorSignature = 0x08074b50;
    private const uint CentralHeaderSignature = 0x02014b50;
    private const uint EndSignature = 0x06054b50;
    private const uint Zip64EndSignature = 0x06064b50;
    private const uint Zip64LocatorSignature = 0x07064b50;
    private const ushort Stored = 0;
    private const ushort Deflated = 8;
    private const ushort DescriptorFlag = 0x0008;
    private const ushort Version20 = 20;
    private const ushort Version45 = 45;
    private const ushort DosTime = 0;
    private const ushort DosDate = 0x0021;    // 1980-01-01: every package is byte-for-byte reproducible
    private const ushort Zip64ExtraId = 0x0001;

    private readonly SpillBuffer _out;
    private readonly CompressionLevel _level;
    private readonly long _zip64Threshold;
    private readonly List<Entry> _entries = [];
    private EntryStream? _open;

    public ZipWriter(SpillBuffer output, CompressionLevel level, long zip64Threshold = uint.MaxValue)
    {
        _out = output;
        _level = level;
        _zip64Threshold = zip64Threshold;
    }

    /// <summary>Writes a small part stored, its checksum and sizes in the local header.</summary>
    public void AddStored(string name, ReadOnlySpan<byte> content)
    {
        ExpectNoOpenEntry();
        byte[] encodedName = Encoding.ASCII.GetBytes(name);
        uint crc = Crc32.Compute(content);
        long offset = _out.TotalWritten;

        WriteLocalHeader(encodedName, Stored, flags: 0, crc, content.Length);
        _out.Write(content);
        _entries.Add(new Entry(encodedName, Stored, 0, crc, content.Length, content.Length, offset));
    }

    /// <summary>Begins a large part: write its bytes into the stream returned, then call <see cref="EndEntry"/>.</summary>
    public Stream BeginDeflated(string name)
    {
        ExpectNoOpenEntry();
        byte[] encodedName = Encoding.ASCII.GetBytes(name);
        long offset = _out.TotalWritten;

        WriteLocalHeader(encodedName, Deflated, DescriptorFlag, crc: 0, size: 0);
        _open = new EntryStream(encodedName, offset, _out, _level);
        return _open;
    }

    /// <summary>Ends the open large part: the last deflate block, then its data descriptor.</summary>
    public void EndEntry()
    {
        EntryStream entry = _open ?? throw new InvalidOperationException("No entry is open.");
        entry.FinishDeflate();

        long compressed = _out.TotalWritten - entry.DataStart;
        bool zip64 = compressed >= _zip64Threshold || entry.Size >= _zip64Threshold;
        Span<byte> descriptor = stackalloc byte[24];
        BinaryPrimitives.WriteUInt32LittleEndian(descriptor, DescriptorSignature);
        BinaryPrimitives.WriteUInt32LittleEndian(descriptor[4..], entry.Crc);

        if (zip64)
        {
            BinaryPrimitives.WriteInt64LittleEndian(descriptor[8..], compressed);
            BinaryPrimitives.WriteInt64LittleEndian(descriptor[16..], entry.Size);
            _out.Write(descriptor);
        }
        else
        {
            BinaryPrimitives.WriteUInt32LittleEndian(descriptor[8..], (uint)compressed);
            BinaryPrimitives.WriteUInt32LittleEndian(descriptor[12..], (uint)entry.Size);
            _out.Write(descriptor[..16]);
        }

        _entries.Add(new Entry(entry.Name, Deflated, DescriptorFlag, entry.Crc, compressed, entry.Size, entry.Offset));
        _open = null;
    }

    /// <summary>Writes the central directory and the end records. Nothing may follow.</summary>
    public void Complete()
    {
        ExpectNoOpenEntry();
        long directoryStart = _out.TotalWritten;

        foreach (Entry entry in _entries)
        {
            WriteCentralHeader(entry);
        }

        long directorySize = _out.TotalWritten - directoryStart;
        bool zip64 = _entries.Count >= ushort.MaxValue
            || directoryStart >= _zip64Threshold
            || directorySize >= _zip64Threshold
            || _entries.Exists(NeedsZip64);

        if (zip64)
        {
            WriteZip64End(directoryStart, directorySize);
        }

        Span<byte> end = stackalloc byte[22];
        BinaryPrimitives.WriteUInt32LittleEndian(end, EndSignature);
        BinaryPrimitives.WriteUInt16LittleEndian(end[4..], 0);                                    // this disk
        BinaryPrimitives.WriteUInt16LittleEndian(end[6..], 0);                                    // directory's disk
        BinaryPrimitives.WriteUInt16LittleEndian(end[8..], zip64 ? ushort.MaxValue : (ushort)_entries.Count);
        BinaryPrimitives.WriteUInt16LittleEndian(end[10..], zip64 ? ushort.MaxValue : (ushort)_entries.Count);
        BinaryPrimitives.WriteUInt32LittleEndian(end[12..], zip64 ? uint.MaxValue : (uint)directorySize);
        BinaryPrimitives.WriteUInt32LittleEndian(end[16..], zip64 ? uint.MaxValue : (uint)directoryStart);
        BinaryPrimitives.WriteUInt16LittleEndian(end[20..], 0);                                   // comment length
        _out.Write(end);
    }

    private bool NeedsZip64(Entry entry) =>
        entry.CompressedSize >= _zip64Threshold || entry.Size >= _zip64Threshold || entry.Offset >= _zip64Threshold;

    private void ExpectNoOpenEntry()
    {
        if (_open is not null)
        {
            throw new InvalidOperationException("An entry is still open; end it first.");
        }
    }

    private void WriteLocalHeader(byte[] name, ushort method, ushort flags, uint crc, long size)
    {
        Span<byte> header = stackalloc byte[30];
        BinaryPrimitives.WriteUInt32LittleEndian(header, LocalHeaderSignature);
        BinaryPrimitives.WriteUInt16LittleEndian(header[4..], Version20);
        BinaryPrimitives.WriteUInt16LittleEndian(header[6..], flags);
        BinaryPrimitives.WriteUInt16LittleEndian(header[8..], method);
        BinaryPrimitives.WriteUInt16LittleEndian(header[10..], DosTime);
        BinaryPrimitives.WriteUInt16LittleEndian(header[12..], DosDate);
        BinaryPrimitives.WriteUInt32LittleEndian(header[14..], crc);
        BinaryPrimitives.WriteUInt32LittleEndian(header[18..], (uint)size);                       // compressed = size when stored
        BinaryPrimitives.WriteUInt32LittleEndian(header[22..], (uint)size);
        BinaryPrimitives.WriteUInt16LittleEndian(header[26..], (ushort)name.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(header[28..], 0);                                // extra length
        _out.Write(header);
        _out.Write(name);
    }

    private void WriteCentralHeader(Entry entry)
    {
        bool zip64 = NeedsZip64(entry);
        Span<byte> header = stackalloc byte[46];
        BinaryPrimitives.WriteUInt32LittleEndian(header, CentralHeaderSignature);
        BinaryPrimitives.WriteUInt16LittleEndian(header[4..], zip64 ? Version45 : Version20);    // made by: MS-DOS host
        BinaryPrimitives.WriteUInt16LittleEndian(header[6..], zip64 ? Version45 : Version20);    // needed to extract
        BinaryPrimitives.WriteUInt16LittleEndian(header[8..], entry.Flags);
        BinaryPrimitives.WriteUInt16LittleEndian(header[10..], entry.Method);
        BinaryPrimitives.WriteUInt16LittleEndian(header[12..], DosTime);
        BinaryPrimitives.WriteUInt16LittleEndian(header[14..], DosDate);
        BinaryPrimitives.WriteUInt32LittleEndian(header[16..], entry.Crc);
        BinaryPrimitives.WriteUInt32LittleEndian(header[20..], zip64 ? uint.MaxValue : (uint)entry.CompressedSize);
        BinaryPrimitives.WriteUInt32LittleEndian(header[24..], zip64 ? uint.MaxValue : (uint)entry.Size);
        BinaryPrimitives.WriteUInt16LittleEndian(header[28..], (ushort)entry.Name.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(header[30..], zip64 ? (ushort)28 : (ushort)0);  // extra length
        BinaryPrimitives.WriteUInt16LittleEndian(header[32..], 0);                                // comment length
        BinaryPrimitives.WriteUInt16LittleEndian(header[34..], 0);                                // disk
        BinaryPrimitives.WriteUInt16LittleEndian(header[36..], 0);                                // internal attributes
        BinaryPrimitives.WriteUInt32LittleEndian(header[38..], 0);                                // external attributes
        BinaryPrimitives.WriteUInt32LittleEndian(header[42..], zip64 ? uint.MaxValue : (uint)entry.Offset);
        _out.Write(header);
        _out.Write(entry.Name);

        if (zip64)
        {
            Span<byte> extra = stackalloc byte[28];
            BinaryPrimitives.WriteUInt16LittleEndian(extra, Zip64ExtraId);
            BinaryPrimitives.WriteUInt16LittleEndian(extra[2..], 24);
            BinaryPrimitives.WriteInt64LittleEndian(extra[4..], entry.Size);
            BinaryPrimitives.WriteInt64LittleEndian(extra[12..], entry.CompressedSize);
            BinaryPrimitives.WriteInt64LittleEndian(extra[20..], entry.Offset);
            _out.Write(extra);
        }
    }

    private void WriteZip64End(long directoryStart, long directorySize)
    {
        long recordStart = _out.TotalWritten;
        Span<byte> record = stackalloc byte[56];
        BinaryPrimitives.WriteUInt32LittleEndian(record, Zip64EndSignature);
        BinaryPrimitives.WriteInt64LittleEndian(record[4..], 44);                                 // size of the rest of the record
        BinaryPrimitives.WriteUInt16LittleEndian(record[12..], Version45);
        BinaryPrimitives.WriteUInt16LittleEndian(record[14..], Version45);
        BinaryPrimitives.WriteUInt32LittleEndian(record[16..], 0);                                // this disk
        BinaryPrimitives.WriteUInt32LittleEndian(record[20..], 0);                                // directory's disk
        BinaryPrimitives.WriteInt64LittleEndian(record[24..], _entries.Count);
        BinaryPrimitives.WriteInt64LittleEndian(record[32..], _entries.Count);
        BinaryPrimitives.WriteInt64LittleEndian(record[40..], directorySize);
        BinaryPrimitives.WriteInt64LittleEndian(record[48..], directoryStart);
        _out.Write(record);

        Span<byte> locator = stackalloc byte[20];
        BinaryPrimitives.WriteUInt32LittleEndian(locator, Zip64LocatorSignature);
        BinaryPrimitives.WriteUInt32LittleEndian(locator[4..], 0);                                // the record's disk
        BinaryPrimitives.WriteInt64LittleEndian(locator[8..], recordStart);
        BinaryPrimitives.WriteUInt32LittleEndian(locator[16..], 1);                               // total disks
        _out.Write(locator);
    }

    private sealed record Entry(byte[] Name, ushort Method, ushort Flags, uint Crc, long CompressedSize, long Size, long Offset);

    /// <summary>The open large part: checksums and counts what is written, and deflates it into the buffer.</summary>
    private sealed class EntryStream : Stream
    {
        private readonly DeflateStream _deflate;

        public EntryStream(byte[] name, long offset, SpillBuffer output, CompressionLevel level)
        {
            Name = name;
            Offset = offset;
            DataStart = output.TotalWritten;
            _deflate = new DeflateStream(output, level, leaveOpen: true);
        }

        public byte[] Name { get; }

        public long Offset { get; }

        public long DataStart { get; }

        public uint Crc { get; private set; }

        public long Size { get; private set; }

        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            Crc = Crc32.Update(Crc, buffer);
            Size += buffer.Length;
            _deflate.Write(buffer);
        }

        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

        public override void Flush()
        {
            // Deliberately nothing: a deflate flush ends a block early and costs compression. The
            // entry's last block is written once, by FinishDeflate.
        }

        public void FinishDeflate() => _deflate.Dispose();

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _deflate.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
