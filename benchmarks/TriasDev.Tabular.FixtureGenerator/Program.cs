using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Text;

namespace TriasDev.Tabular.FixtureGenerator;

/// <summary>
/// Writes synthetic large files of the shape the published benchmarks were measured on, so anyone
/// can reproduce them without the real-world files, which are not public.
/// </summary>
/// <remarks>
/// <para>
/// Same shape, not the same bytes: 17 columns of the kinds a location export carries (decimal ids,
/// codes, names, coordinates, amounts, an always-empty column), the same row counts, and — in the
/// malformed csv — the same kinds of quoting defect in similar proportions. Expect the numbers to be
/// close to the published ones, not equal to them.
/// </para>
/// <para>
/// Deterministic: a fixed seed, so every run writes the same files.
/// </para>
/// </remarks>
public static class Program
{
    private static readonly CultureInfo German = CultureInfo.GetCultureInfo("de-DE");

    private static readonly string[] Countries = ["DEU", "AUT", "CHE", "FRA", "ITA", "NLD", "POL", "ESP"];
    private static readonly string[] Regions = ["BY", "BW", "NW", "HE", "SN", "TH", "HH", "BE", "NI", "RP"];
    private static readonly string[] Cities =
        ["Musterstadt", "Beispielhausen", "Neudorf", "Altstadt am See", "Bergheim", "Talwinkel", "Seebrück", "Waldau"];
    private static readonly string[] Streets = ["Hauptstraße", "Bahnhofstraße", "Gartenweg", "Am Markt", "Schulstraße", "Lindenallee"];
    private static readonly string[] Categories = ["office", "warehouse", "retail", "residential", "plant"];
    private static readonly string[] Statuses = ["active", "planned", "closed"];

    public static int Main(string[] args)
    {
        if (args.Length < 1)
        {
            Console.Error.WriteLine("usage: FixtureGenerator <output folder> [scale, default 1.0]");
            return 2;
        }

        string folder = args[0];
        double scale = args.Length > 1 ? double.Parse(args[1], CultureInfo.InvariantCulture) : 1.0;
        Directory.CreateDirectory(folder);

        Write(folder, "workbook-100k.xlsx", path => WriteWorkbook(path, Rows(100_000, scale)));
        Write(folder, "workbook-1m.xlsx", path => WriteWorkbook(path, Rows(1_000_000, scale)));
        Write(folder, "clean-3m.csv", path => WriteCsv(path, Rows(3_000_000, scale), malformed: false));
        Write(folder, "malformed-5m.csv", path => WriteCsv(path, Rows(5_127_968, scale), malformed: true));

        return 0;
    }

    private static int Rows(int full, double scale) => Math.Max(1, (int)(full * scale));

    private static void Write(string folder, string name, Action<string> write)
    {
        string path = Path.Combine(folder, name);
        Stopwatch clock = Stopwatch.StartNew();
        write(path);
        Console.WriteLine($"{name}: {new FileInfo(path).Length / 1_048_576.0:F1} MB in {clock.Elapsed.TotalSeconds:F1} s");
    }

    private static readonly string[] Header =
    [
        "id", "site", "country", "region", "name", "postal_code", "street", "note", "latitude", "longitude",
        "floors", "category", "insured_value", "employees", "occupancy", "status", "owner",
    ];

    /// <summary>One row's values, as text in the csv's German-style number format.</summary>
    private static string[] Row(Random random, int i)
    {
        return
        [
            (1_000_000 + i).ToString(CultureInfo.InvariantCulture) + "," + random.Next(0, 100).ToString("00", CultureInfo.InvariantCulture),
            random.Next(1000, 9999).ToString(CultureInfo.InvariantCulture),
            Countries[random.Next(Countries.Length)],
            Regions[random.Next(Regions.Length)],
            Cities[random.Next(Cities.Length)] + " " + random.Next(1, 500).ToString(CultureInfo.InvariantCulture),
            random.Next(10_000, 99_999).ToString(CultureInfo.InvariantCulture),
            Streets[random.Next(Streets.Length)] + " " + random.Next(1, 200).ToString(CultureInfo.InvariantCulture),
            string.Empty,
            (47 + (random.NextDouble() * 8)).ToString("F2", German),
            (6 + (random.NextDouble() * 9)).ToString("F2", German),
            random.Next(1, 9).ToString(CultureInfo.InvariantCulture),
            Categories[random.Next(Categories.Length)],
            (random.Next(10_000, 99_999_999) / 100.0).ToString("F2", German),
            random.Next(0, 2).ToString(CultureInfo.InvariantCulture),
            (random.NextDouble() * 100).ToString("F2", German),
            Statuses[random.Next(Statuses.Length)],
            "Owner " + random.Next(1, 5_000).ToString(CultureInfo.InvariantCulture),
        ];
    }

    private static void WriteCsv(string path, int rows, bool malformed)
    {
        Random random = new(20260925);
        using StreamWriter writer = new(path, append: false, new UTF8Encoding(false), 1 << 16);
        writer.WriteLine(string.Join(';', Header));

        for (int i = 1; i <= rows; i++)
        {
            string[] values = Row(random, i);

            if (malformed)
            {
                Damage(values, random);
            }

            writer.WriteLine(string.Join(';', values));
        }
    }

    /// <summary>
    /// The quoting defects of a real-world export, in roughly its proportions per five million rows.
    /// </summary>
    /// <remarks>
    /// Quotes inside unquoted fields (tens of thousands), text after a closing quote (thousands),
    /// quotes that are never closed (about three hundred), and lone quotes standing as a whole field
    /// in the same column of nearby rows (a handful) — the case of issue #20.
    /// </remarks>
    private static void Damage(string[] values, Random random)
    {
        int roll = random.Next(1_000_000);

        if (roll < 8_000)
        {
            values[6] += " 12\" Einfahrt";                  // a quote inside an unquoted field
        }
        else if (roll < 8_900)
        {
            values[4] = "\"" + values[4] + "\" (alt)";    // text after the closing quote
        }
        else if (roll < 8_960)
        {
            values[16] = "\"" + values[16];                // a quote that is never closed
        }
        else if (roll < 8_962)
        {
            values[6] = "\"";                              // a lone quote as the whole field
        }
    }

    private static void WriteWorkbook(string path, int rows)
    {
        // Every text a sheet cell can hold, known up front, so the shared string table is written
        // once and cells refer to it by index as Excel does.
        List<string> strings = [.. Header, .. Countries, .. Regions, .. Categories, .. Statuses];
        Dictionary<string, int> index = [];

        for (int i = 0; i < strings.Count; i++)
        {
            index.TryAdd(strings[i], i);
        }

        Random random = new(20260925);

        using FileStream file = File.Create(path);
        using ZipArchive zip = new(file, ZipArchiveMode.Create);

        Entry(zip, "[Content_Types].xml", """<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/><Default Extension="xml" ContentType="application/xml"/><Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/><Override PartName="/xl/worksheets/sheet1.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/><Override PartName="/xl/sharedStrings.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sharedStrings+xml"/><Override PartName="/xl/styles.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml"/></Types>""");
        Entry(zip, "_rels/.rels", """<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/></Relationships>""");
        Entry(zip, "xl/workbook.xml", """<?xml version="1.0" encoding="UTF-8" standalone="yes"?><workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships"><sheets><sheet name="Locations" sheetId="1" r:id="rId1"/></sheets></workbook>""");
        Entry(zip, "xl/_rels/workbook.xml.rels", """<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet1.xml"/><Relationship Id="rId2" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/sharedStrings" Target="sharedStrings.xml"/><Relationship Id="rId3" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles" Target="styles.xml"/></Relationships>""");
        Entry(zip, "xl/styles.xml", """<?xml version="1.0" encoding="UTF-8" standalone="yes"?><styleSheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><cellXfs count="1"><xf numFmtId="0"/></cellXfs></styleSheet>""");

        using (StreamWriter sheet = new(zip.CreateEntry("xl/worksheets/sheet1.xml", CompressionLevel.Optimal).Open(), new UTF8Encoding(false), 1 << 16))
        {
            sheet.Write("""<?xml version="1.0" encoding="UTF-8" standalone="yes"?><worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><sheetData>""");
            sheet.Write("<row r=\"1\">");

            for (int c = 0; c < Header.Length; c++)
            {
                sheet.Write($"<c r=\"{Column(c)}1\" t=\"s\"><v>{index[Header[c]]}</v></c>");
            }

            sheet.Write("</row>");

            for (int i = 1; i <= rows; i++)
            {
                string[] values = Row(random, i);
                sheet.Write($"<row r=\"{i + 1}\">");

                for (int c = 0; c < values.Length; c++)
                {
                    string v = values[c];
                    string at = Column(c) + (i + 1).ToString(CultureInfo.InvariantCulture);

                    if (v.Length == 0)
                    {
                        continue;
                    }

                    if (index.TryGetValue(v, out int s))
                    {
                        sheet.Write($"<c r=\"{at}\" t=\"s\"><v>{s}</v></c>");
                    }
                    else if (double.TryParse(v.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out double number) && !v.Contains(' '))
                    {
                        sheet.Write($"<c r=\"{at}\"><v>{number.ToString("R", CultureInfo.InvariantCulture)}</v></c>");
                    }
                    else
                    {
                        sheet.Write($"<c r=\"{at}\" t=\"inlineStr\"><is><t>{System.Security.SecurityElement.Escape(v)}</t></is></c>");
                    }
                }

                sheet.Write("</row>");
            }

            sheet.Write("</sheetData></worksheet>");
        }

        StringBuilder table = new("""<?xml version="1.0" encoding="UTF-8" standalone="yes"?><sst xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main">""");

        foreach (string s in strings)
        {
            table.Append("<si><t>").Append(System.Security.SecurityElement.Escape(s)).Append("</t></si>");
        }

        Entry(zip, "xl/sharedStrings.xml", table.Append("</sst>").ToString());
    }

    /// <summary>A column's letter; the sheet has 17 columns, so one letter always does.</summary>
    /// <remarks>
    /// Written on every cell, as Excel does: the always-empty column is left out, and without a
    /// reference the cells after it would be read one column to the left.
    /// </remarks>
    private static string Column(int index) => ((char)('A' + index)).ToString();

    private static void Entry(ZipArchive zip, string name, string content)
    {
        using StreamWriter writer = new(zip.CreateEntry(name, CompressionLevel.Optimal).Open(), new UTF8Encoding(false));
        writer.Write(content);
    }
}
