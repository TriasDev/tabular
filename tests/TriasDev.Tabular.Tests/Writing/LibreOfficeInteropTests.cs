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
}
