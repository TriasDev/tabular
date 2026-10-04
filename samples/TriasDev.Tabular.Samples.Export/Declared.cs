namespace TriasDev.Tabular.Samples.Export;

internal static class Declared
{
    // --8<-- [start:declare]
    private static readonly CellStyle Late = new() { Fill = CellColor.Parse("#FFC7CE") };

    private static readonly CellStyle Header = new() { Fill = CellColor.Parse("#1F4E78"), Font = new CellFont { Bold = true, Color = CellColor.Parse("#FFFFFF") } };

    // Declared once, kept in a static field, shared by any number of concurrent writes.
    public static readonly TabularExport<Order> Export = TabularExport.For<Order>()
        .Column("Id", o => o.Id, width: 8)
        .Column("Customer", o => o.Customer, width: 28)
        .Column("Placed", o => o.Placed)
        .Column("Amount", o => o.Amount, width: 12)
        .Column("Status", o => o.Status, width: 10, style: status => status == "late" ? Late : null)
        .Column("Paid", o => o.Paid)
        .Sheet(new SheetOptions { HeaderStyle = Header, FreezeRows = 1, AutoFilter = true })
        .Build();
    // --8<-- [end:declare]

    // --8<-- [start:chunks]
    public static async Task<long> WriteChunksAsync(Stream stream, IAsyncEnumerable<IReadOnlyList<Order>> chunks, CancellationToken cancellationToken) =>
        await Export.WriteAsync(stream, TabularFormat.Xlsx, "Orders", chunks, cancellationToken: cancellationToken);
    // --8<-- [end:chunks]

    // --8<-- [start:itemsof]
    // A server stream whose messages carry a repeated field: pass the stream, and say where the items are.
    public static async Task<long> WriteStreamAsync(Stream stream, IAsyncEnumerable<OrderChunk> messages, CancellationToken cancellationToken) =>
        await Export.WriteAsync(stream, TabularFormat.Xlsx, "Orders", messages, message => message.Orders, cancellationToken: cancellationToken);
    // --8<-- [end:itemsof]

    // --8<-- [start:sheet]
    // Several sheets in one workbook: one writer, one WriteSheetAsync per sheet.
    public static async Task WriteTwoSheetsAsync(Stream stream, IReadOnlyList<Order> open, IReadOnlyList<Order> closed)
    {
        await using TabularWriter writer = TabularWriter.Create(stream, TabularFormat.Xlsx);
        await Export.WriteSheetAsync(writer, "Open", open);
        await Export.WriteSheetAsync(writer, "Closed", closed);
        await writer.CompleteAsync();
    }
    // --8<-- [end:sheet]
}
