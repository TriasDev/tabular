namespace TriasDev.Tabular.Samples.Export;

internal static class RowByRow
{
    private const string CustomerHeader = "Customer";

    // --8<-- [start:styles]
    // Styles are declared once. A style compares by value, so the file holds each one once.
    private static readonly CellStyle HeaderStyle = new()
    {
        Fill = CellColor.Parse("#1F4E78"),
        Font = new CellFont { Color = CellColor.Parse("#FFFFFF"), Bold = true },
    };

    private static readonly CellStyle MoneyStyle = new() { Number = NumberFormat.Parse("#,##0.00") };
    private static readonly CellStyle DateStyle = new() { Date = DateFormat.Parse("dd/mm/yyyy") };

    // A legend: late orders are marked in red.
    private static readonly CellStyle LateStyle = new() { Fill = CellColor.Parse("#FFC7CE") };
    // --8<-- [end:styles]

    // --8<-- [start:rows]
    public static async Task WriteAsync(Stream stream, IEnumerable<Order> orders, CancellationToken cancellationToken)
    {
        // Disposing the writer closes the stream. Without CompleteAsync the file stays incomplete.
        await using TabularWriter writer = TabularWriter.Create(stream, TabularFormat.Xlsx);

        writer.BeginSheet(
            "Orders",
            [new WriteColumn("Id", 8), new WriteColumn("Customer", 28), new WriteColumn("Placed", 12), new WriteColumn("Amount", 12), new WriteColumn("Status", 10)],
            new SheetOptions { HeaderStyle = HeaderStyle, FreezeRows = 1, AutoFilter = true });

        StyleId money = writer.Style(MoneyStyle);
        StyleId date = writer.Style(DateStyle);
        StyleId late = writer.Style(LateStyle);

        foreach (Order order in orders)
        {
            writer.BeginRow();
            writer.Write(order.Id);
            writer.Write(order.Customer);
            writer.Write(order.Placed, date);
            writer.Write(order.Amount, money);
            writer.Write(order.Status, order.Status == "late" ? late : default);
            writer.EndRow();

            // Writes go into memory; this moves them to the stream once about a megabyte is pending.
            if (writer.FlushRecommended)
            {
                await writer.FlushAsync(cancellationToken);
            }
        }

        await writer.CompleteAsync(cancellationToken);
    }
    // --8<-- [end:rows]

    // --8<-- [start:merge]
    public static async Task WriteWithTotalAsync(Stream stream, IReadOnlyList<Order> orders)
    {
        await using TabularWriter writer = TabularWriter.Create(stream, TabularFormat.Xlsx);
        writer.BeginSheet("Orders", [new WriteColumn("Id"), new WriteColumn(CustomerHeader), new WriteColumn("Amount")]);
        StyleId money = writer.Style(MoneyStyle);

        foreach (Order order in orders)
        {
            writer.BeginRow();
            writer.Write(order.Id);
            writer.Write(order.Customer);
            writer.Write(order.Amount, money);
            writer.EndRow();
        }

        // "Total" spans the first two cells of the last row; the amount lands in the third.
        writer.BeginRow();
        writer.Merge(1, 2);
        writer.Write("Total");
        writer.Write(orders.Sum(o => o.Amount), money);
        writer.EndRow();

        await writer.CompleteAsync();
    }
    // --8<-- [end:merge]

    // --8<-- [start:zip]
    public static async Task WriteZipAsync(Stream stream, IReadOnlyList<Order> orders, IReadOnlyList<Order> returns)
    {
        // Each sheet becomes <name>.csv in the zip.
        await using TabularWriter writer = TabularWriter.Create(stream, TabularFormat.Zip);

        foreach ((string name, IReadOnlyList<Order> sheet) in new[] { ("orders", orders), ("returns", returns) })
        {
            writer.BeginSheet(name, [new WriteColumn("Id"), new WriteColumn(CustomerHeader)]);

            foreach (Order order in sheet)
            {
                writer.BeginRow();
                writer.Write(order.Id);
                writer.Write(order.Customer);
                writer.EndRow();
            }
        }

        await writer.CompleteAsync();
    }
    // --8<-- [end:zip]

    // --8<-- [start:batch]
    public static async Task WriteColumnsAsync(Stream stream, IAsyncEnumerable<OrderColumns> source, CancellationToken cancellationToken)
    {
        await using TabularWriter writer = TabularWriter.Create(stream, TabularFormat.Xlsx);
        writer.BeginSheet("Orders", [new WriteColumn("Id"), new WriteColumn(CustomerHeader), new WriteColumn("Amount")]);
        StyleId money = writer.Style(MoneyStyle);

        // One batch for the whole sheet: Reset keeps its slots, and the lists are read where they are.
        ColumnBatch batch = new();

        await foreach (OrderColumns columns in source.WithCancellation(cancellationToken))
        {
            batch.Reset(columns.Ids.Length);
            batch.Add(columns.Ids);
            batch.Add(columns.Customers);
            batch.Add(columns.Amounts, money);
            await writer.WriteBatchAsync(batch, cancellationToken);
        }

        await writer.CompleteAsync(cancellationToken);
    }
    // --8<-- [end:batch]
}
