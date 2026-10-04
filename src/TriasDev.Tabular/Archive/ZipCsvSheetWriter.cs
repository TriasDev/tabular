using System.Buffers;

using TriasDev.Tabular.Csv;

namespace TriasDev.Tabular.Archive;

/// <summary>
/// Writes a zip of csv files: each sheet one deflated entry, <c>&lt;sheet name&gt;.csv</c>, holding
/// exactly the csv file a single-sheet export of it would be.
/// </summary>
/// <remarks>
/// The csv writer formats into a buffer writer whose committed bytes collect in one working buffer
/// and are written into the open entry's deflate stream when it fills and when the entry ends, so
/// nothing is buffered per sheet. The archive reader reads such a zip back
/// as a workbook, a sheet per entry, named after the entry without <c>.csv</c>.
/// </remarks>
internal sealed class ZipCsvSheetWriter : ISheetWriter
{
    private static readonly SearchValues<char> FileNameForbidden = SearchValues.Create("<>\"|");

    private readonly ZipWriter _zip;
    private readonly StreamBufferWriter _entryBuffer = new();
    private readonly CsvSheetWriter _csv;
    private Stream? _entry;

    public ZipCsvSheetWriter(SpillBuffer output, ZipWriterOptions options, CsvFormat format)
    {
        _zip = new ZipWriter(output, options.CompressionLevel);
        _csv = new CsvSheetWriter(_entryBuffer, format);
    }

    /// <summary>The entry buffer's current size, for the test that a huge row does not keep it.</summary>
    internal int EntryBufferLength => _entryBuffer.BufferLength;

    public long MaxRows => long.MaxValue;

    public bool AllowsSeveralSheets => true;

    public bool NamesSheets => true;

    public int MaxMerges => int.MaxValue;

    public string? NameProblem(string name)
    {
        if (name.AsSpan().IndexOfAny(FileNameForbidden) >= 0)
        {
            return $"The sheet name \"{name}\" holds one of < > \" |, which a file system refuses in the entry's file name.";
        }

        if (name[^1] is '.' or ' ')
        {
            return $"The sheet name \"{name}\" ends with a dot or a space, which a file system drops from the entry's file name.";
        }

        return IsWindowsDeviceName(name)
            ? $"The sheet name \"{name}\" is a reserved device name on Windows, where \"{name}.csv\" cannot be unpacked."
            : null;
    }

    /// <summary>CON, PRN, AUX, NUL, COM1-COM9 and LPT1-LPT9, in any case: Windows refuses them as file names.</summary>
    private static bool IsWindowsDeviceName(string name)
    {
        ReadOnlySpan<char> span = name;

        if (span.Length == 3)
        {
            return span.Equals("CON", StringComparison.OrdinalIgnoreCase)
                || span.Equals("PRN", StringComparison.OrdinalIgnoreCase)
                || span.Equals("AUX", StringComparison.OrdinalIgnoreCase)
                || span.Equals("NUL", StringComparison.OrdinalIgnoreCase);
        }

        return span.Length == 4
            && span[3] is >= '1' and <= '9'
            && (span[..3].Equals("COM", StringComparison.OrdinalIgnoreCase) || span[..3].Equals("LPT", StringComparison.OrdinalIgnoreCase));
    }

    public void BeginSheet(string name, ReadOnlySpan<WriteColumn> columns, SheetOptions options)
    {
        CloseEntry();
        _entry = _zip.BeginDeflated(name + ".csv");
        _entryBuffer.Target = _entry;
        _csv.BeginSheet(name, columns, options);
    }

    public void BeginRow() => _csv.BeginRow();

    public string? WriteHeader(string value, int style) => _csv.WriteHeader(value, style);

    public string? WriteText(string value, int column, int style) => _csv.WriteText(value, column, style);

    public string? WriteLong(long value, int style) => _csv.WriteLong(value, style);

    public string? WriteDecimal(decimal value, int style) => _csv.WriteDecimal(value, style);

    public string? WriteDouble(double value, int style) => _csv.WriteDouble(value, style);

    public string? WriteDate(DateTime value, bool hasTime, int style) => _csv.WriteDate(value, hasTime, style);

    public void WriteBoolean(bool value, int style) => _csv.WriteBoolean(value, style);

    public void WriteEmpty(int style) => _csv.WriteEmpty(style);

    public void Merge(int rows, int columns) => _csv.Merge(rows, columns);

    public void WriteCovered() => _csv.WriteCovered();

    public void EndRow() => _csv.EndRow();

    public void Complete()
    {
        CloseEntry();
        _zip.Complete();
    }

    /// <summary>
    /// Releases the open entry's deflate state: the file is abandoned, so whatever disposing the
    /// stream writes (its final block) stays in the abandoned spill buffer, which is never drained.
    /// </summary>
    public void Dispose()
    {
        _entry?.Dispose();
        _entry = null;
        _entryBuffer.Target = null;
    }

    private void CloseEntry()
    {
        if (_entry is null)
        {
            return;
        }

        _entryBuffer.Flush();
        _zip.EndEntry();
        _entry = null;
        _entryBuffer.Target = null;
    }
}
