using System.IO.Compression;
using System.Text;

using TriasDev.Tabular.Ods;
using TriasDev.Tabular.Tests.Fixtures;
using TriasDev.Tabular.Xlsx;

using Xunit;

namespace TriasDev.Tabular.Tests;

/// <summary>
/// A workbook whose part is damaged in a way only the zip's CRC-32 can tell: one digit of a stored
/// part changed. The BCL's zip reader checks no CRC, so the changed number read as though the file
/// held it.
/// </summary>
public sealed class DamagedEntryTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>The same package with every entry stored, and the first <paramref name="text"/> in it changed to <paramref name="damaged"/>.</summary>
    private static byte[] StoredAndDamaged(byte[] package, string text, string damaged)
    {
        using MemoryStream stored = new();

        using (ZipArchive source = new(new MemoryStream(package), ZipArchiveMode.Read))
        using (ZipArchive target = new(stored, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (ZipArchiveEntry entry in source.Entries)
            {
                using Stream from = entry.Open();
                using Stream to = target.CreateEntry(entry.FullName, CompressionLevel.NoCompression).Open();
                from.CopyTo(to);
            }
        }

        byte[] bytes = stored.ToArray();
        byte[] find = Encoding.UTF8.GetBytes(text);
        int at = bytes.AsSpan().IndexOf(find);
        Assert.True(at >= 0);
        Encoding.UTF8.GetBytes(damaged).CopyTo(bytes, at);
        return bytes;
    }

    private static void ReadEveryRow(ITabularCursor cursor)
    {
        int rows = 0;

        do
        {
            while (cursor.ReadRow(Token))
            {
                rows++;
            }
        }
        while (cursor.MoveToSheet(cursor.CurrentSheetIndex + 1, Token));

        Assert.Fail($"{rows} rows read from a damaged file.");
    }

    [Theory]
    [InlineData("<v>4711</v>", "<v>4712</v>")]
    [InlineData("<t>Köln</t>", "<t>Kiln</t>")]
    public void RefusesAWorkbookPartWhoseDataFailsItsChecksum(string text, string damaged)
    {
        byte[] workbook = StoredAndDamaged(
            new XlsxPackage()
                .WithSheet("S", """<row r="1"><c r="A1"><v>4711</v></c><c r="B1" t="s"><v>0</v></c></row>""")
                .WithSharedStrings("<si><t>Köln</t></si>")
                .Build(),
            text,
            damaged);

        using XlsxCursor cursor = new(new MemoryStream(workbook), cancellationToken: Token);
        TabularFormatException error = Assert.Throws<TabularFormatException>(() => ReadEveryRow(cursor));

        Assert.Equal(TabularFormatException.Corrupt, error.Code);
    }

    [Fact]
    public void RefusesAnOpenDocumentContentPartWhoseDataFailsItsChecksum()
    {
        byte[] spreadsheet = StoredAndDamaged(
            new OdsPackage().WithTable("S", """<table:table-row><table:table-cell office:value-type="float" office:value="4711"><text:p>4711</text:p></table:table-cell></table:table-row>""").Build(),
            "office:value=\"4711\"",
            "office:value=\"4712\"");

        TabularFormatException error = Assert.Throws<TabularFormatException>(() =>
        {
            using OdsCursor cursor = new(new MemoryStream(spreadsheet), cancellationToken: Token);
            ReadEveryRow(cursor);
        });

        Assert.Equal(TabularFormatException.Corrupt, error.Code);
    }
}
