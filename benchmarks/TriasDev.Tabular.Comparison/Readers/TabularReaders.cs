namespace TriasDev.Tabular.Comparison.Readers;

/// <summary>TriasDev.Tabular reading a csv file. It detects the dialect itself, so the delimiter is unused.</summary>
internal sealed class TabularCsv : IReader
{
    public string Name => "TriasDev.Tabular";

    public Type Anchor => typeof(TabularFile);

    public FileKind Kind => FileKind.Csv;

    public ReadCount Read(string path, char delimiter)
    {
        using FileStream stream = File.OpenRead(path);
        using ITabularCursor cursor = TabularFile.Open(stream, Path.GetFileName(path));

        return ReadAll(cursor);
    }

    internal static ReadCount ReadAll(ITabularCursor cursor)
    {
        Tally tally = default;

        while (cursor.ReadRow())
        {
            tally.Row();

            ReadOnlySpan<RawCell> row = cursor.CurrentRow;

            for (int i = 0; i < row.Length; i++)
            {
                tally.Value(row[i].AsText());
            }
        }

        return tally.Result;
    }
}

/// <summary>TriasDev.Tabular reading an xlsx file.</summary>
internal sealed class TabularXlsx : IReader
{
    public string Name => "TriasDev.Tabular";

    public Type Anchor => typeof(TabularFile);

    public FileKind Kind => FileKind.Xlsx;

    public ReadCount Read(string path, char delimiter)
    {
        using FileStream stream = File.OpenRead(path);
        using ITabularCursor cursor = TabularFile.Open(stream, Path.GetFileName(path));

        return TabularCsv.ReadAll(cursor);
    }
}
