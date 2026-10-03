using System.IO.Compression;
using System.Text;

using TriasDev.Tabular.Tests.Fixtures;

using Xunit;

namespace TriasDev.Tabular.Tests.Fuzz;

/// <summary>
/// Files broken at random — bytes cut, doubled or dropped in, inside the package parts of a workbook
/// as much as in a csv — are either read or refused with a <see cref="TabularException"/>, and
/// always soon: nothing else escapes, and nothing loops.
/// </summary>
public sealed class MangledInputFuzzTests
{
    private static readonly string[] CsvTokens =
        ["a", "1", ";", ",", "\t", "|", "\"", "\"\"", "\n", "\r\n", "\r", " ", "ä", "😀", "\0", "﻿", "k.A.", "2024-01-15"];

    private static readonly byte[][] CsvBytes = [[0xFF], [0xFE], [0xC3], [0xEF, 0xBB, 0xBF], [0xFF, 0xFE], [0x80], [0xE2, 0x82]];

    private static readonly string[] XmlTokens =
    [
        "<", ">", "&", "\"", "'", "</c>", "<c>", "<row>", "</row>", "<v>", "</v>", "<v>-1</v>", "<v>99999999</v>", "]]>", "<![CDATA[", "<!--", "-->",
        "<?x", "&#0;", "&#xFFFFFFFF;", "&#xD800;", "&bogus;", "\0", " r=\"0\"", " r=\"XFD1048577\"", " r=\"A2147483648\"", " t=\"s\"", " t=\"b\"", " t=\"e\"", " s=\"99\"",
        " table:number-columns-repeated=\"99999999\"", " table:number-rows-repeated=\"2147483647\"", " table:number-columns-repeated=\"-1\"",
        "<table:table-row>", "</table:table-row>", "<table:table-cell>", "</table:table-cell>", "<text:s text:c=\"999999999\"/>", "<text:p>", "</text:p>",
        " office:value-type=\"date\" office:date-value=\"-0001-13-40\"", " office:value-type=\"time\" office:time-value=\"PT99999999H\"",
    ];

    [Fact]
    public void ReadsOrRefusesMangledCsv()
    {
        foreach ((int seed, Random random) in FuzzCases.Generate(500))
        {
            List<byte> file = [];

            for (int i = random.Next(0, 200); i > 0; i--)
            {
                file.AddRange(random.Chance(0.05) ? random.Pick(CsvBytes) : Encoding.UTF8.GetBytes(random.Pick(CsvTokens)));
            }

            ReadsOrRefuses([.. file], "fuzz.csv", seed);
        }
    }

    [Fact]
    public void ReadsOrRefusesMangledGzip()
    {
        foreach ((int seed, Random random) in FuzzCases.Generate(300))
        {
            List<byte> csv = [];

            for (int i = random.Next(0, 400); i > 0; i--)
            {
                csv.AddRange(Encoding.UTF8.GetBytes(random.Pick(CsvTokens)));
            }

            // Cut, flip and insert past the ten fixed header bytes, so the file stays gzip to the
            // detector and the damage lands in the framing, the deflate data and the trailer.
            List<byte> file = [.. GzipFile.Of([.. csv])];

            for (int m = random.Next(1, 4); m > 0 && file.Count > 10; m--)
            {
                int at = random.Next(10, file.Count);

                switch (random.Next(3))
                {
                    case 0:
                        file.RemoveRange(at, file.Count - at);
                        break;
                    case 1:
                        file[at] ^= (byte)random.Next(1, 256);
                        break;
                    default:
                        file.Insert(at, (byte)random.Next(256));
                        break;
                }
            }

            ReadsOrRefuses([.. file], "fuzz.csv.gz", seed);
        }
    }

    [Fact]
    public void ReadsOrRefusesMangledXlsx()
    {
        foreach ((int seed, Random random) in FuzzCases.Generate(300))
        {
            ReadsOrRefuses(Mangle(XlsxFuzzTests.Write(FuzzSheets.Workbook(random), random), random), "fuzz.xlsx", seed);
        }
    }

    [Fact]
    public void ReadsOrRefusesMangledOds()
    {
        foreach ((int seed, Random random) in FuzzCases.Generate(300))
        {
            ReadsOrRefuses(Mangle(OdsFuzzTests.Write(FuzzSheets.Workbook(random), random), random), "fuzz.ods", seed);
        }
    }

    private static void ReadsOrRefuses(byte[] file, string name, int seed)
    {
        FuzzCases.Keep(seed, Path.GetExtension(name), file);

        // Seconds for a file of a few kilobytes: past that the reader is looping, and the token says so.
        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));

        try
        {
            using ITabularCursor cursor = TabularFile.Open(new MemoryStream(file), name, cancellationToken: deadline.Token);

            for (int s = 0; s < cursor.Sheets.Count && cursor.MoveToSheet(s, deadline.Token); s++)
            {
                while (cursor.ReadRow(deadline.Token))
                {
                    _ = cursor.CurrentRow.ToArray();
                }
            }
        }
        catch (TabularException)
        {
            // Refused, as a broken file should be.
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested && !TestContext.Current.CancellationToken.IsCancellationRequested)
        {
            Assert.Fail($"seed {seed}: reading {name} did not finish");
        }
        catch (Exception error)
        {
            Assert.Fail($"seed {seed}: reading {name} threw {error.GetType().Name}: {error.Message}");
        }
    }

    /// <summary>Breaks the XML of one or more parts of a package, and packs it again so the zip itself is sound.</summary>
    private static byte[] Mangle(byte[] package, Random random)
    {
        List<(string Name, byte[] Content)> entries = [];

        using (ZipArchive zip = new(new MemoryStream(package), ZipArchiveMode.Read))
        {
            foreach (ZipArchiveEntry entry in zip.Entries)
            {
                using MemoryStream content = new();
                using (Stream stream = entry.Open())
                {
                    stream.CopyTo(content);
                }

                entries.Add((entry.FullName, content.ToArray()));
            }
        }

        for (int m = random.Next(1, 4); m > 0; m--)
        {
            int target = random.Next(entries.Count);
            entries[target] = (entries[target].Name, Mangle(Encoding.UTF8.GetString(entries[target].Content), random));
        }

        using MemoryStream buffer = new();

        using (ZipArchive zip = new(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach ((string name, byte[] content) in entries)
            {
                using Stream stream = zip.CreateEntry(name).Open();
                stream.Write(content);
            }
        }

        return buffer.ToArray();
    }

    private static byte[] Mangle(string xml, Random random)
    {
        StringBuilder text = new(xml);
        int at = text.Length == 0 ? 0 : random.Next(text.Length);

        switch (random.Next(4))
        {
            case 0:
                text.Length = at;
                break;

            case 1:
                text.Remove(at, Math.Min(random.Next(1, 40), text.Length - at));
                break;

            case 2:
                text.Insert(at, text.ToString(at, Math.Min(random.Next(1, 200), text.Length - at)));
                break;

            default:
                text.Insert(at, random.Pick(XmlTokens));
                break;
        }

        return Encoding.UTF8.GetBytes(text.ToString());
    }
}
