using System.Text;

using TriasDev.Tabular.Csv;

using Xunit;

namespace TriasDev.Tabular.Tests.Csv;

/// <summary>
/// Every field and line ending reads the same wherever the reader's 64 KB buffer happens to end.
/// </summary>
/// <remarks>
/// The reader takes runs of text straight from its buffer and may keep a pointer into it until the
/// field ends; a field, a quoted field or a CRLF split across a refill is exactly where that would go
/// wrong. Rows of seeded random widths put the boundary at every kind of position many times over.
/// </remarks>
public sealed class BufferBoundaryTests
{
    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    [InlineData("\r")]
    public void ReadsEveryFieldIntactAcrossBufferRefills(string lineEnding)
    {
        Random random = new(20260926);
        List<string[]> expected = [["id", "text", "quoted", "tail"]];

        while (expected.Count < 12_000)
        {
            int n = expected.Count;
            expected.Add(
            [
                n.ToString(System.Globalization.CultureInfo.InvariantCulture),
                new string('a', random.Next(0, 90)) + "x",
                "q" + new string('b', random.Next(0, 40)) + ";" + new string('c', random.Next(0, 20)),
                new string('d', random.Next(1, 30)),
            ]);
        }

        StringBuilder text = new();

        foreach (string[] row in expected)
        {
            // The third field goes in quotes when it holds the delimiter, as a writer would put it.
            text.Append(row[0]).Append(';').Append(row[1]).Append(';')
                .Append(row[2].Contains(';') ? "\"" + row[2] + "\"" : row[2]).Append(';')
                .Append(row[3]).Append(lineEnding);
        }

        byte[] content = Encoding.UTF8.GetBytes(text.ToString());
        Assert.True(content.Length > 10 * 64 * 1024, "the file must span many buffers");

        using CsvCursor cursor = new(new MemoryStream(content, writable: false), "t.csv");
        int index = 0;

        while (cursor.ReadRow(TestContext.Current.CancellationToken))
        {
            string?[] actual = [.. cursor.CurrentRow.ToArray().Select(c => c.AsText())];

            Assert.True(
                expected[index].SequenceEqual(actual),
                $"row {index}: expected [{string.Join("|", expected[index])}], read [{string.Join("|", actual)}]");
            index++;
        }

        Assert.Equal(expected.Count, index);
        Assert.True(cursor.Diagnostics.IsClean);
    }

    [Theory]
    [InlineData(65_526)]
    [InlineData(65_527)]
    [InlineData(65_528)]
    [InlineData(65_529)]
    [InlineData(65_530)]
    public void ReadsAFieldWhoseLineEndingStraddlesTheBufferEnd(int width)
    {
        // The header is five characters, the field starts at seven, so its CR lands at 7 + width:
        // 65,535 is the buffer's last character, which puts the CR's LF — and the look at it — in the
        // next refill, while the field's text is still only a pointer into the buffer being replaced.
        string field = new('x', width);
        string text = "a;b\r\n" + "1;" + field + "\r\n" + "2;tail\r\n";

        using CsvCursor cursor = new(new MemoryStream(Encoding.UTF8.GetBytes(text), writable: false), "t.csv");

        Assert.True(cursor.ReadRow(TestContext.Current.CancellationToken));
        Assert.True(cursor.ReadRow(TestContext.Current.CancellationToken));
        Assert.Equal(field, cursor.CurrentRow[1].AsText());
        Assert.True(cursor.ReadRow(TestContext.Current.CancellationToken));
        Assert.Equal("tail", cursor.CurrentRow[1].AsText());
        Assert.False(cursor.ReadRow(TestContext.Current.CancellationToken));
    }
}
