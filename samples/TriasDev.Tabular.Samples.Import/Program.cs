using TriasDev.Tabular;
using TriasDev.Tabular.Samples.Import;

// Imports a file into your own type: profile it, build a plan from its headers, check the plan
// against the profile, then read typed rows — or errors that name their row and column.
//
//   dotnet run --project samples/TriasDev.Tabular.Samples.Import -- [path]
//
// Without a path it imports samples/customers.csv, which has a date that is not one and an empty
// amount in a required field.

string path = args.Length > 0 ? args[0] : Path.Combine(AppContext.BaseDirectory, "customers.csv");

// The fields, declared once: they build the schema and read the values.
TextField name = ImportField.Text("name").Require().MaxLength(100);
TextField country = ImportField.Text("country").ExactLength(2);
DateField signedOn = ImportField.Date("signed_on");
DecimalField amount = ImportField.Decimal("amount").Require();
TargetSchema schema = new() { Fields = [name, country, signedOn, amount] };

// 1. Profile the file, and build the plan from its headers.
FileProfile profile;
using (FileStream file = File.OpenRead(path))
using (ITabularCursor cursor = TabularFile.Open(file, Path.GetFileName(path)))
{
    profile = new TabularAnalyzer().Analyze(cursor);
}

MappingPlan plan = MappingPlan.ByHeader(profile.Sheets[0], schema, culture: "de-DE");

// 2. Judge the plan against the profile, before reading the file again.
PrecheckResult check = MappingPrecheck.Check(plan, schema, profile);

foreach (PrecheckFinding finding in check.Findings)
{
    Console.WriteLine($"precheck: {finding.Severity} {finding.Code} on {finding.TargetFieldName}");
}

if (!check.CanImport)
{
    return 1;
}

// 3. Import: typed rows, or errors that point at their row and column.
using FileStream again = File.OpenRead(path);
using ImportRun<Customer> run = TabularImporter.Import(
    again, Path.GetFileName(path), plan, schema,
    row => new Customer(row[name]!, row[country], row[signedOn], row[amount]!.Value));

foreach (ImportOutcome<Customer> outcome in run)
{
    if (outcome.HasErrors)
    {
        foreach (RowError error in outcome.Errors)
        {
            Console.WriteLine($"row {error.RowNumber}, {error.TargetFieldName}: {error.Code} ({error.RawValue})");
        }

        continue;
    }

    Customer customer = outcome.Value;
    Console.WriteLine(string.Create(
        System.Globalization.CultureInfo.InvariantCulture,
        $"imported {customer.Name}, {customer.Country}, {customer.SignedOn:yyyy-MM-dd}, {customer.Amount}"));
}

Console.WriteLine($"{run.Summary.RowsProduced} imported, {run.Summary.RowsFailed} failed");
return 0;
