using System.Text;

using TriasDev.Tabular.Tests.Fixtures;

using Xunit;

namespace TriasDev.Tabular.Tests.Writing;

/// <summary>
/// Random rows, seeded and so repeatable: every value the writer accepts, the import reads back as
/// written — or the writer refused it with a code. Nothing in between.
/// </summary>
/// <remarks>
/// The documented exceptions are the import's own: text comes back trimmed, and empty or
/// whitespace-only text comes back as no value (a row of nothing is skipped).
/// </remarks>
public sealed class CsvWriteFuzzTests
{
    private const int Seed = 67;

    private const int Rows = 300;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>The pieces text is drawn from: letters, digits, space, every delimiter candidate, quotes, line breaks, umlauts, emoji.</summary>
    private static readonly string[] Alphabet =
    [
        "a", "b", "Z", "x", "0", "7", "9", " ", " ", ",", ",", ";", ";", "\t", "|", "|", "\"", "\"", "\n", "\r\n", "ä", "Ö", "ß", "👍", "🎉",
    ];

    /// <summary>
    /// The same pieces, crowded with delimiters and line breaks: what it takes, now and then, to reach
    /// a record's worth of delimiters in a field that spans lines.
    /// </summary>
    private static readonly string[] Dense = [",", ",", ";", ";", "\t", "|", "\n", "\r\n", "\"", "a", " "];

    private enum Kind
    {
        Text,
        Integer,
        Decimal,
        Double,
        Date,
        DateTime,
        Boolean,
    }

    /// <summary>Column layouts: single columns, which the reader detects no delimiter in, and six, past the stray-quote threshold.</summary>
    private static readonly Dictionary<string, Kind[]> Layouts = new()
    {
        ["text"] = [Kind.Text],
        ["decimal"] = [Kind.Decimal],
        ["double"] = [Kind.Double],
        ["date-time"] = [Kind.DateTime],
        ["mixed"] = [Kind.Text, Kind.Integer, Kind.Decimal, Kind.Date, Kind.Boolean, Kind.Text],
        ["all text"] = [Kind.Text, Kind.Text, Kind.Text, Kind.Text, Kind.Text, Kind.Text],
    };

    public static TheoryData<string, string> Runs
    {
        get
        {
            TheoryData<string, string> runs = [];

            foreach (string culture in new[] { "", "de-DE", "en-US" })
            {
                foreach (string layout in Layouts.Keys)
                {
                    runs.Add(culture, layout);
                }
            }

            return runs;
        }
    }

    [Theory]
    [MemberData(nameof(Runs))]
    public async Task WhatTheWriterAcceptsTheImportReadsBack(string culture, string layout)
    {
        Kind[] kinds = Layouts[layout];
        string[] headers = [.. kinds.Select((k, i) => $"{k}{i}")];
        // HashCode is randomised per process; this is not.
        int seed = Seed + $"{culture}/{layout}".Sum(c => c);
        Random random = new(seed);

        List<object?[]> accepted = [];
        List<string> lines = [];
        List<string> refused = [];

        for (int r = 0; r < Rows; r++)
        {
            object?[] row = [.. kinds.Select(kind => Draw(random, kind))];

            (string? line, string? code) = await WrittenAlone(culture, headers, row);

            if (line is not null)
            {
                accepted.Add(row);
                lines.Add(line);
            }
            else
            {
                refused.Add(code!);
            }
        }

        byte[] file = await CsvRoundTrip.WriteAsync(culture, headers, writer =>
        {
            foreach (object?[] row in accepted)
            {
                WriteRow(writer, row);
            }
        }, Token);

        ImportField[] fields = [.. kinds.Select((kind, i) => Field(kind, headers[i]))];
        (List<object?[]> read, List<string> errors, int skipped) = CsvRoundTrip.Import(file, culture, fields, Token);

        List<(object?[] Expected, string Line)> expected = [];

        for (int i = 0; i < accepted.Count; i++)
        {
            object?[] values = [.. accepted[i].Select(Expected)];

            if (values.Any(v => v is not null))
            {
                expected.Add((values, lines[i]));
            }
        }

        Assert.True(accepted.Count >= Rows / 2, $"Only {accepted.Count} of {Rows} rows accepted ({string.Join(", ", refused)} refused).");

        if (kinds.Count(kind => kind == Kind.Text) >= 2)
        {
            // Proof the draw reaches the reader's stray-quote rule, rather than passing by never meeting it.
            Assert.Contains(ErrorCodes.Write.AmbiguousLineBreaks, refused);
        }
        Assert.Empty(errors);

        for (int i = 0; i < Math.Min(expected.Count, read.Count); i++)
        {
            Assert.True(
                expected[i].Expected.SequenceEqual(read[i]),
                $"seed {seed}, culture \"{culture}\", layout \"{layout}\", row {i + 2}: written {Escape(expected[i].Line)}, "
                + $"expected [{string.Join(" | ", expected[i].Expected.Select(Show))}], read [{string.Join(" | ", read[i].Select(Show))}]");
        }

        Assert.Equal(expected.Count, read.Count);
        Assert.Equal(accepted.Count - expected.Count, skipped);
    }

    /// <summary>
    /// Writes a row alone, as the file's only row: its line, or the code the writer refused it with. A refused value faults its writer, so it is found here and left out of the file.
    /// </summary>
    private static async Task<(string? Line, string? Code)> WrittenAlone(string culture, string[] headers, object?[] row)
    {
        try
        {
            byte[] alone = await CsvRoundTrip.WriteAsync(culture, headers, writer => WriteRow(writer, row), Token, byteOrderMark: false);
            string text = Encoding.UTF8.GetString(alone);
            return (text[(text.IndexOf("\r\n", StringComparison.Ordinal) + 2)..], null);
        }
        catch (TabularWriteException refused)
        {
            Assert.StartsWith("write.", refused.Code, StringComparison.Ordinal);
            return (null, refused.Code);
        }
    }

    private static void WriteRow(TabularWriter writer, object?[] row)
    {
        writer.BeginRow();

        foreach (object? value in row)
        {
            switch (value)
            {
                case string text:
                    writer.Write(text);
                    break;
                case long integer:
                    writer.Write(integer);
                    break;
                case decimal number:
                    writer.Write(number);
                    break;
                case double number:
                    writer.Write(number);
                    break;
                case DateTime date:
                    writer.Write(date);
                    break;
                case bool flag:
                    writer.Write(flag);
                    break;
                default:
                    writer.WriteEmpty();
                    break;
            }
        }

        writer.EndRow();
    }

    private static object? Draw(Random random, Kind kind)
    {
        if (random.Next(12) == 0)
        {
            return null;
        }

        return kind switch
        {
            Kind.Text => Text(random, random.Next(3) == 0 ? Dense : Alphabet),
            Kind.Integer => random.Next(4) == 0 ? random.NextInt64(long.MinValue, long.MaxValue) : random.NextInt64(-1000, 1000),
            Kind.Decimal => new decimal(random.NextInt64(-10_000_000, 10_000_000)) / Pow10(random.Next(0, 6)),
            Kind.Double => random.NextInt64(-10_000_000, 10_000_000) / Math.Pow(10, random.Next(0, 6)),
            Kind.Date => new DateTime(random.Next(2, 10_000), random.Next(1, 13), random.Next(1, 29), 0, 0, 0, DateTimeKind.Unspecified),
            Kind.DateTime => new DateTime(random.Next(1800, 2200), random.Next(1, 13), random.Next(1, 29), random.Next(24), random.Next(60), random.Next(60), random.Next(1000), DateTimeKind.Unspecified),
            Kind.Boolean => random.Next(2) == 0,
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
    }

    private static string Text(Random random, string[] pieces) =>
        string.Concat(Enumerable.Range(0, random.Next(0, 20)).Select(_ => pieces[random.Next(pieces.Length)]));

    private static decimal Pow10(int exponent)
    {
        decimal result = 1m;

        for (int i = 0; i < exponent; i++)
        {
            result *= 10m;
        }

        return result;
    }

    /// <summary>What the import returns for a value written: text trimmed, blank text absent, a double as the decimal its digits say.</summary>
    private static object? Expected(object? value) => value switch
    {
        string text => string.IsNullOrWhiteSpace(text) ? null : text.Trim(),
        double number => (decimal)number,
        _ => value,
    };

    private static ImportField Field(Kind kind, string name) => kind switch
    {
        Kind.Text => ImportField.Text(name),
        Kind.Integer => ImportField.Integer(name),
        Kind.Decimal or Kind.Double => ImportField.Decimal(name),
        Kind.Date or Kind.DateTime => ImportField.Date(name),
        Kind.Boolean => ImportField.Boolean(name),
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    private static string Show(object? value) => value is null ? "null" : Escape(Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? "");

    private static string Escape(string text) => text.Replace("\r", "\\r", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal).Replace("\t", "\\t", StringComparison.Ordinal);
}
