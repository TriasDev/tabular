using System.Globalization;

using CsvHelper;
using CsvHelper.Configuration;

using nietras.SeparatedValues;

using SylvanCsvOptions = Sylvan.Data.Csv.CsvDataReaderOptions;
using SylvanCsvReader = Sylvan.Data.Csv.CsvDataReader;

namespace TriasDev.Tabular.Comparison.Readers;

/// <summary>Sylvan.Data.Csv, through its data reader.</summary>
internal sealed class SylvanCsv : IReader
{
    public string Name => "Sylvan.Data.Csv";

    public Type Anchor => typeof(SylvanCsvReader);

    public FileKind Kind => FileKind.Csv;

    public ReadCount Read(string path, char delimiter)
    {
        using SylvanCsvReader csv = SylvanCsvReader.Create(
            path,
            new SylvanCsvOptions { HasHeaders = false, Delimiter = delimiter });

        Tally tally = default;

        while (csv.Read())
        {
            tally.Row();

            for (int i = 0; i < csv.RowFieldCount; i++)
            {
                tally.Value(csv.GetString(i));
            }
        }

        return tally.Result;
    }
}

/// <summary>
/// Sep, with unescaping and ragged rows switched on — without them it returns quoted text verbatim
/// and refuses a row whose width differs from the first.
/// </summary>
internal sealed class SepCsv : IReader
{
    public string Name => "Sep";

    public Type Anchor => typeof(Sep);

    public FileKind Kind => FileKind.Csv;

    public ReadCount Read(string path, char delimiter)
    {
        using SepReader reader = Sep.New(delimiter)
            .Reader(o => o with { HasHeader = false, Unescape = true, DisableColCountCheck = true })
            .FromFile(path);

        Tally tally = default;

        foreach (SepReader.Row row in reader)
        {
            tally.Row();

            for (int i = 0; i < row.ColCount; i++)
            {
                tally.Value(row[i].ToString());
            }
        }

        return tally.Result;
    }
}

/// <summary>CsvHelper's parser, the layer beneath its record mapping.</summary>
internal sealed class CsvHelperCsv : IReader
{
    public string Name => "CsvHelper";

    public Type Anchor => typeof(CsvParser);

    public FileKind Kind => FileKind.Csv;

    public ReadCount Read(string path, char delimiter)
    {
        CsvConfiguration configuration = new(CultureInfo.InvariantCulture)
        {
            Delimiter = delimiter.ToString(),
            HasHeaderRecord = false,
            BadDataFound = null,
            MissingFieldFound = null,
        };

        using StreamReader text = new(path);
        using CsvParser parser = new(text, configuration);

        Tally tally = default;

        while (parser.Read())
        {
            tally.Row();

            for (int i = 0; i < parser.Count; i++)
            {
                tally.Value(parser[i]);
            }
        }

        return tally.Result;
    }
}
