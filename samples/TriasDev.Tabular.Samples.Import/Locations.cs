namespace TriasDev.Tabular.Samples.Import;

/// <summary>
/// Fields that stand in for one another: a row is located by its coordinates, or where it has none,
/// by an address. Compiled with the sample and shown in the documentation; not run by it.
/// </summary>
internal static class Locations
{
    private static readonly string[] CountryCodes = ["DEU", "FRA", "ITA", "AUT", "CHE"];

    public static void Review(string path)
    {
        // --8<-- [start:declare]
        DecimalImportField latitude = ImportField.Decimal("latitude").AtLeast(-90).AtMost(90);
        DecimalImportField longitude = ImportField.Decimal("longitude").AtLeast(-180).AtMost(180);
        TextImportField country = ImportField.Text("country").AllowedValues(CountryCodes, ignoreCase: true);
        TextImportField postalCode = ImportField.Text("postalCode");
        TextImportField city = ImportField.Text("city");
        TextImportField street = ImportField.Text("street");
        TextImportField house = ImportField.Text("house");

        FieldAlternatives location = new(
            "location",
            [
                // Usable only with both values.
                AlternativeGroup.AllOf("coordinates", latitude, longitude),

                // Usable from the country on; each further rung refines it, and a gap ends the ladder.
                AlternativeGroup.Ladder(
                    "address",
                    requiredLevels: 1,
                    new AlternativeLevel("country", country),
                    new AlternativeLevel("locality", postalCode, city),
                    new AlternativeLevel("street", street),
                    new AlternativeLevel("house", house)),
            ]);

        ImportSchema schema = new()
        {
            Fields = [latitude, longitude, country, postalCode, city, street, house],
            Alternatives = [location],
        };
        // --8<-- [end:declare]

        FileProfile profile;
        using (FileStream file = File.OpenRead(path))
        using (ITabularCursor cursor = TabularFile.Open(file, Path.GetFileName(path)))
        {
            profile = TabularAnalyzer.Analyze(cursor);
        }

        MappingPlan plan = MappingPlan.ByHeader(profile.Sheets[0], schema);

        // --8<-- [start:review]
        // A second pass through the mapping, keeping nothing: exact counts before anything is written.
        using FileStream again = File.OpenRead(path);
        using ITabularCursor reading = TabularFile.Open(again, Path.GetFileName(path));
        ImportReview review = TabularExtractor.Review(reading, plan, schema);

        AlternativesReport report = review.Alternatives[0];
        AlternativeGroupReport coordinates = report.Groups[0];
        AlternativeGroupReport address = report.Groups[1];

        Console.WriteLine($"{coordinates.Incomplete} of {report.RowsJudged} rows lack usable coordinates");
        Console.WriteLine($"{address.Incomplete} of them have an incomplete address, e.g. rows {string.Join(", ", address.IncompleteRows)}");
        Console.WriteLine($"{report.Unresolved} rows can be located by neither");
        Console.WriteLine($"{review.Summary.RowsFailed} rows would not import at all");
        // --8<-- [end:review]
    }
}
