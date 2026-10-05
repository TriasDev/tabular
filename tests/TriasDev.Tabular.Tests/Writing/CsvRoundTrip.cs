using TriasDev.Tabular.Tests.Fixtures;

namespace TriasDev.Tabular.Tests.Writing;

/// <summary>Writes csv with any columns and imports it back, field by field, through the public import.</summary>
internal static class CsvRoundTrip
{
    /// <summary>Writes a sheet with these headers; <paramref name="rows"/> writes the rows.</summary>
    public static async Task<byte[]> WriteAsync(
        string culture,
        IEnumerable<string> headers,
        Action<TabularWriter> rows,
        CancellationToken cancellationToken,
        bool formulaGuard = false,
        bool byteOrderMark = true)
    {
        WriteTarget target = new();

        await using (TabularWriter writer = TabularWriter.Create(
            target,
            TabularFormat.Csv,
            new TabularWriterOptions { Csv = new CsvWriterOptions { Culture = culture, FormulaGuard = formulaGuard, ByteOrderMark = byteOrderMark } }))
        {
            writer.BeginSheet("data", [.. headers.Select(header => new WriteColumn(header))]);
            rows(writer);
            await writer.CompleteAsync(cancellationToken);
        }

        return target.ToArray();
    }

    /// <summary>
    /// Imports a file whose columns are these fields, in order, bound by the fields' names; each row
    /// comes back as its values, or the codes of its errors.
    /// </summary>
    public static (List<object?[]> Rows, List<string> Errors, int Skipped) Import(
        byte[] file,
        string culture,
        IReadOnlyList<ImportField> fields,
        CancellationToken cancellationToken)
    {
        ImportSchema schema = new() { Fields = [.. fields] };
        MappingPlan plan = new()
        {
            Culture = culture,
            Bindings = [.. fields.Select((f, i) => new ColumnBinding { ColumnIndex = i, Header = f.Name, FieldName = f.Name })],
        };

        using ImportRun<object?[]> run = TabularImporter.Import(
            new MemoryStream(file, writable: false),
            "t.csv",
            plan,
            schema,
            row =>
            {
                object?[] values = new object?[fields.Count];

                for (int i = 0; i < fields.Count; i++)
                {
                    values[i] = fields[i] switch
                    {
                        TextImportField text => row[text],
                        IntegerImportField integer => row[integer],
                        DecimalImportField number => row[number],
                        DateImportField date => row[date],
                        BooleanImportField boolean => row[boolean],
                        _ => throw new NotSupportedException(fields[i].GetType().Name),
                    };
                }

                return values;
            },
            cancellationToken: cancellationToken);

        List<object?[]> rows = [];
        List<string> errors = [];

        foreach (ImportOutcome<object?[]> outcome in run.ReadRows(cancellationToken))
        {
            if (outcome.HasErrors)
            {
                errors.Add(string.Join(", ", outcome.Errors.Select(e => e.Code)));
                rows.Add([]);
            }
            else
            {
                rows.Add(outcome.Value!);
            }
        }

        return (rows, errors, run.Summary.RowsSkipped);
    }
}
