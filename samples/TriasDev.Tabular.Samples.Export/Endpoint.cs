using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace TriasDev.Tabular.Samples.Export;

internal static class Endpoint
{
    // --8<-- [start:endpoint]
    public static void Map(IEndpointRouteBuilder app) =>
        app.MapGet("/orders.xlsx", async (HttpContext http, CancellationToken cancellationToken) =>
        {
            // Headers first: the first flush starts the response, and headers cannot change after it.
            http.Response.ContentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";
            http.Response.Headers.ContentDisposition = "attachment; filename=\"orders.xlsx\"";

            try
            {
                // The response body refuses synchronous writes; the library only writes to it asynchronously.
                // LeaveOpen: the server owns the response body.
                await Declared.Export.WriteAsync(
                    http.Response.Body,
                    TabularFormat.Xlsx,
                    "Orders",
                    Orders.Stream(chunks: 100, size: 1000, cancellationToken),
                    message => message.Orders,
                    new TabularWriterOptions { LeaveOpen = true },
                    cancellationToken);
            }
            catch (Exception failure) when (failure is TabularException or OperationCanceledException)
            {
                // A client that went away cancels the token; a value the file cannot hold throws a
                // TabularException. Either way the file is incomplete: if nothing was sent yet, answer
                // with an error; otherwise cut the connection, so the client sees a failed download
                // and not a short file.
                if (http.Response.HasStarted)
                {
                    http.Abort();
                }
                else
                {
                    http.Response.Headers.Remove("Content-Disposition");
                    http.Response.ContentType = null;
                    http.Response.StatusCode = StatusCodes.Status500InternalServerError;
                }
            }
        });
    // --8<-- [end:endpoint]
}
