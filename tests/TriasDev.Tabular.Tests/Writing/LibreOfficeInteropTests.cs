using TriasDev.Tabular.Tests.Fixtures;

using Xunit;

namespace TriasDev.Tabular.Tests.Writing;

/// <summary>LibreOffice opens the workbooks we write and shows what we wrote.</summary>
public sealed class LibreOfficeInteropTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static async Task<byte[]> Write(TabularFormat format)
    {
        WriteTarget target = new();

        await using (TabularWriter writer = TabularWriter.Create(target, format))
        {
            writer.BeginSheet("R&D <2026>", [new("Name", 20), new("Count"), new("Amount"), new("Start"), new("Active")]);
            writer.BeginRow();
            writer.Write("Grüße  two spaces");
            writer.Write(42L);
            writer.Write(1234m);
            writer.Write(new DateTime(2026, 10, 3, 9, 0, 0, DateTimeKind.Unspecified));
            writer.Write(true);
            writer.EndRow();
            writer.BeginRow();
            writer.Write("tab\there");
            writer.Write(-7L);
            writer.Write(-25m);
            writer.Write(new DateTime(2026, 10, 3, 14, 5, 6, DateTimeKind.Unspecified));
            writer.Write(false);
            writer.EndRow();
            foreach (string text in new[] { "  lead", "\tz", "x\n\ny" })
            {
                writer.BeginRow();
                writer.Write(text);
                writer.Write(1L);
                writer.Write(1m);
                writer.Write(new DateTime(2026, 1, 1, 0, 0, 1, DateTimeKind.Unspecified));
                writer.Write(true);
                writer.EndRow();
            }

            writer.BeginSheet("Second", [new("x")]);
            await writer.CompleteAsync(Token);
        }

        return target.ToArray();
    }

    [Theory]
    [InlineData(TabularFormat.Ods, "ods")]
    [InlineData(TabularFormat.Xlsx, "xlsx")]
    public async Task OpensWhatWeWriteAndShowsTheValues(TabularFormat format, string extension)
    {
        string[] lines = LibreOffice.ConvertToCsv(await Write(format), extension);

        Assert.True(lines.Length >= 3, $"LibreOffice produced {lines.Length} lines.");
        Assert.Equal("\"Name\",\"Count\",\"Amount\",\"Start\",\"Active\"", lines[0]);
        Assert.StartsWith("\"Grüße  two spaces\",42,1234,2026-10-03 09:00:00,", lines[1], StringComparison.Ordinal);
        Assert.StartsWith("\"tab\there\",-7,-25,2026-10-03 14:05:06,", lines[2], StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(TabularFormat.Ods, "ods")]
    [InlineData(TabularFormat.Xlsx, "xlsx")]
    public async Task ShowsLeadingWhitespaceAndBlankLinesAsWritten(TabularFormat format, string extension)
    {
        string all = string.Join('\n', LibreOffice.ConvertToCsv(await Write(format), extension));

        Assert.Contains("\n\"  lead\",1,1,", all, StringComparison.Ordinal);
        Assert.Contains("\n\"\tz\",1,1,", all, StringComparison.Ordinal);
        Assert.Contains("\n\"x\n\ny\",1,1,", all, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(TabularFormat.Ods, "ods")]
    [InlineData(TabularFormat.Xlsx, "xlsx")]
    public async Task ShowsBooleansAsBooleans(TabularFormat format, string extension)
    {
        string[] lines = LibreOffice.ConvertToCsv(await Write(format), extension);

        foreach (int row in new[] { 1, 2 })
        {
            string last = lines[row][(lines[row].LastIndexOf(',') + 1)..];

            Assert.False(last.Length == 0 || last.StartsWith('"'), $"Not an unquoted boolean: {lines[row]}");
            Assert.False(double.TryParse(last, System.Globalization.CultureInfo.InvariantCulture, out _), $"Boolean shown as a number: {lines[row]}");
        }
    }
}
