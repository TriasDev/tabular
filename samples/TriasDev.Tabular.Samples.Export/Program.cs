using Microsoft.AspNetCore.Builder;
using TriasDev.Tabular;
using TriasDev.Tabular.Samples.Export;

// Writes files: row by row, from a declared export, from a stream of chunks, by column — then reads
// each back to show what it holds.
//
//   dotnet run --project samples/TriasDev.Tabular.Samples.Export
//   dotnet run --project samples/TriasDev.Tabular.Samples.Export -- serve   (the endpoint: GET /orders.xlsx on the address it prints)

if (args.Length > 0 && args[0] == "serve")
{
    WebApplication app = WebApplication.CreateBuilder(args[1..]).Build();
    Endpoint.Map(app);
    await app.RunAsync();
    return 0;
}

string folder = Path.Combine(Path.GetTempPath(), "tabular-export-sample");
Directory.CreateDirectory(folder);

IReadOnlyList<Order> orders = Orders.Many(1, 2_500);

// --8<-- [start:blob]
// Any writable stream will do — a file, a blob upload, a response body. It need not seek.
await using (Stream blob = await OpenBlobAsync(Path.Combine(folder, "rows.xlsx")))
{
    await RowByRow.WriteAsync(blob, orders, CancellationToken.None);
}
// --8<-- [end:blob]

await using (FileStream file = File.Create(Path.Combine(folder, "total.xlsx")))
{
    await RowByRow.WriteWithTotalAsync(file, orders);
}

await using (FileStream file = File.Create(Path.Combine(folder, "declared.xlsx")))
{
    long rows = await Declared.WriteStreamAsync(file, Orders.Stream(chunks: 5, size: 1_000), CancellationToken.None);
    Console.WriteLine($"declared.xlsx: {rows} rows written");
}

await using (FileStream file = File.Create(Path.Combine(folder, "two-sheets.xlsx")))
{
    await Declared.WriteTwoSheetsAsync(file, orders.Take(10).ToList(), orders.Skip(10).Take(10).ToList());
}

await using (FileStream file = File.Create(Path.Combine(folder, "sheets.zip")))
{
    await RowByRow.WriteZipAsync(file, orders.Take(10).ToList(), orders.Skip(10).Take(5).ToList());
}

await using (FileStream file = File.Create(Path.Combine(folder, "columns.xlsx")))
{
    await RowByRow.WriteColumnsAsync(file, ColumnSource(), CancellationToken.None);
}

// --8<-- [start:failure]
string failing = Path.Combine(folder, "failing.xlsx");

try
{
    await using FileStream file = File.Create(failing);
    await using TabularWriter writer = TabularWriter.Create(file, TabularFormat.Xlsx);
    writer.BeginSheet("Orders", [new WriteColumn("Id"), new WriteColumn("Ratio")]);
    writer.BeginRow();
    writer.Write(1);
    writer.Write(double.NaN);
    writer.EndRow();
    await writer.CompleteAsync();
}
catch (TabularWriteException failure)
{
    // The code says what, the rest says where: sheet "Orders", row 2, column 1 ("Ratio").
    Console.WriteLine($"{failure.Code} in {failure.SheetName}, row {failure.RowNumber}, column {failure.ColumnIndex} ({failure.Header})");
    File.Delete(failing);
}
// --8<-- [end:failure]

foreach (string path in Directory.GetFiles(folder).Order())
{
    await using FileStream file = File.OpenRead(path);
    using ITabularCursor cursor = TabularFile.Open(file, Path.GetFileName(path));
    FileProfile profile = TabularAnalyzer.Analyze(cursor);
    Console.WriteLine($"{Path.GetFileName(path)}: " + string.Join(", ", profile.Sheets.Select(s => $"{s.Name} {s.RowCount} rows")));
}

return 0;

static Task<Stream> OpenBlobAsync(string name) => Task.FromResult<Stream>(File.Create(name));

static async IAsyncEnumerable<OrderColumns> ColumnSource()
{
    for (int i = 0; i < 3; i++)
    {
        await Task.Yield();
        IReadOnlyList<Order> chunk = Orders.Many(i * 500L, 500);
        yield return new OrderColumns(
            [.. chunk.Select(o => o.Id)],
            [.. chunk.Select(o => o.Customer)],
            [.. chunk.Select(o => o.Amount)]);
    }
}
