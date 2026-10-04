namespace TriasDev.Tabular.Samples.Export;

/// <summary>The caller's own type — the library never sees it, only the lambdas that read it.</summary>
internal sealed record Order(long Id, string Customer, DateOnly Placed, decimal Amount, string Status, bool Paid);

/// <summary>A message of a server stream: a batch of orders in a repeated field.</summary>
internal sealed record OrderChunk(IReadOnlyList<Order> Orders);

/// <summary>Orders by column, as a columnar source delivers them.</summary>
internal sealed record OrderColumns(long[] Ids, string[] Customers, decimal[] Amounts);

/// <summary>Made-up data for the samples.</summary>
internal static class Orders
{
    private static readonly string[] Names = ["Contoso Ltd", "Fabrikam GmbH", "Northwind", "Tailspin AG"];

    public static Order Make(long id) => new(
        id,
        Names[id % Names.Length],
        new DateOnly(2026, 1, 1).AddDays((int)(id % 365)),
        10m + (id % 1000) * 1.25m,
        id % 10 == 0 ? "late" : "ok",
        id % 3 != 0);

    public static IReadOnlyList<Order> Many(long first, int count) =>
        [.. Enumerable.Range(0, count).Select(i => Make(first + i))];

    /// <summary>A stream of chunks, standing in for a gRPC server stream or a paged query.</summary>
    public static async IAsyncEnumerable<OrderChunk> Stream(
        int chunks,
        int size,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        for (int i = 0; i < chunks; i++)
        {
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            yield return new OrderChunk(Many((long)i * size, size));
        }
    }
}
