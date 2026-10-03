namespace TriasDev.Tabular.Tests.Fixtures;

/// <summary>
/// A write target that behaves like an ASP.NET Core response body: it cannot seek, a synchronous
/// write or flush throws, and a synchronous dispose is recorded.
/// </summary>
public sealed class WriteTarget : MemoryStream
{
    private bool _disposingAsync;
    private bool _storing;

    /// <summary>Whether the stream was closed.</summary>
    public bool IsDisposed { get; private set; }

    /// <summary>Whether it was closed by <c>Dispose()</c> rather than <c>DisposeAsync()</c>.</summary>
    public bool DisposedSynchronously { get; private set; }

    /// <summary>How many times <c>FlushAsync</c> was called.</summary>
    public int AsyncFlushes { get; private set; }

    /// <summary>When set, every asynchronous write throws it — a client that went away.</summary>
    public Exception? FailWith { get; set; }

    public override bool CanSeek => false;

    public override void Write(byte[] buffer, int offset, int count)
    {
        // MemoryStream.Write(ReadOnlySpan<byte>) routes a derived type back through this overload.
        if (!_storing)
        {
            throw Synchronous();
        }

        base.Write(buffer, offset, count);
    }

    public override void Write(ReadOnlySpan<byte> buffer) => throw Synchronous();

    public override void WriteByte(byte value) => throw Synchronous();

    public override void Flush() => throw Synchronous();

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (FailWith is { } failure)
        {
            throw failure;
        }

        _storing = true;

        try
        {
            base.Write(buffer.Span);
        }
        finally
        {
            _storing = false;
        }

        return ValueTask.CompletedTask;
    }

    public override Task FlushAsync(CancellationToken cancellationToken)
    {
        AsyncFlushes++;
        return Task.CompletedTask;
    }

    public override async ValueTask DisposeAsync()
    {
        _disposingAsync = true;
        await base.DisposeAsync();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposingAsync)
        {
            DisposedSynchronously = true;
        }

        IsDisposed = true;
        base.Dispose(disposing);
    }

    private static InvalidOperationException Synchronous() => new("Synchronous operations are disallowed.");
}
