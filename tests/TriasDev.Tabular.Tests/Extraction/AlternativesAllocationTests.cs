using System.Text;

using Xunit;

namespace TriasDev.Tabular.Tests.Extraction;

/// <summary>What judging alternatives costs per row, in memory.</summary>
[Collection(AllocationMeasurementCollection.Name)]
public sealed class AlternativesAllocationTests
{
    private const int Rows = 20_000;

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private static readonly DecimalImportField Lat = ImportField.Decimal("lat");
    private static readonly DecimalImportField Lon = ImportField.Decimal("lon");
    private static readonly TextImportField Country = ImportField.Text("country").AllowedValues(["DEU"]);

    private static readonly ImportSchema Schema = new()
    {
        Fields = [Lat, Lon, Country],
        Alternatives =
        [
            new FieldAlternatives(
                "location",
                [AlternativeGroup.AllOf("coordinates", Lat, Lon), AlternativeGroup.Ladder("address", 1, new AlternativeLevel("country", Country))]),
        ],
    };

    private static readonly MappingPlan Plan = new()
    {
        Bindings =
        [
            new ColumnBinding { ColumnIndex = 0, Header = "lat", FieldName = "lat" },
            new ColumnBinding { ColumnIndex = 1, Header = "lon", FieldName = "lon" },
            new ColumnBinding { ColumnIndex = 2, Header = "country", FieldName = "country" },
        ],
    };

    [Fact]
    public void AnInvalidValueNobodyNeedsCostsNothingPerRow()
    {
        // Every row is located by its coordinates, and every country breaks its rule. The rule is
        // never the row's problem, so nothing about it may be built per row — a common file, where
        // a country is spelled out against a list of codes, would pay for it on every line.
        byte[] valid = Csv("DEU");
        byte[] invalid = Csv("XXX");

        Read(valid);   // warm-up: the JIT and the pools allocate once
        long baseline = Read(valid);
        long ignored = Read(invalid);

        Assert.True(ignored - baseline < Rows, $"{ignored - baseline} bytes more for {Rows} rows");
    }

    private static byte[] Csv(string country) =>
        Utf8NoBom.GetBytes("lat;lon;country\n" + string.Concat(Enumerable.Repeat($"48.1;11.5;{country}\n", Rows)));

    private static long Read(byte[] csv)
    {
        using ITabularCursor cursor = TabularFile.Open(new MemoryStream(csv, writable: false), "test.csv");
        ExtractionRun run = TabularExtractor.Extract(cursor, Plan, Schema, cancellationToken: TestContext.Current.CancellationToken);
        long before = GC.GetAllocatedBytesForCurrentThread();
        int rows = 0;

        while (run.ReadRow(TestContext.Current.CancellationToken))
        {
            rows++;
        }

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(Rows, rows);
        return allocated;
    }
}
