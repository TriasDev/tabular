using System.Collections.Concurrent;

namespace TriasDev.Tabular.Tests.Fixtures;

/// <summary>
/// Catches an await that comes back to the caller's synchronization context — one written without
/// <c>ConfigureAwait(false)</c>, which deadlocks a caller that blocks on a context of one thread.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Start"/> runs the work under a recording context. The operations the probe hands out
/// (<see cref="Operation"/>, and the stream and sources built on it) complete at once, except the one
/// <see cref="PendAt"/> names, which completes a millisecond later on the pool. Up to that operation
/// everything runs synchronously on the starting thread, so every await then waiting on it — the
/// whole call stack, from the stream up to the export — was registered under the recording context.
/// One that captured it posts its continuation there, and the post is recorded.
/// </para>
/// <para>
/// Why one pending operation per run, at each position in turn: once an await that does say
/// <c>ConfigureAwait(false)</c> resumes, it runs on the pool without a context, so a later await is
/// tested only in a run whose pending operation it is the first to wait on. A context installed
/// deeper down does not help either: an async method puts back its caller's context when it returns.
/// </para>
/// </remarks>
internal sealed class PostProbe
{
    private readonly ConcurrentQueue<string> _posts = new();
    private int _operations;
    private int _pendingAt = -1;

    /// <summary>What was posted to the recording context: the continuation's target, where known.</summary>
    public IReadOnlyCollection<string> Posts => _posts;

    /// <summary>The operations handed out since <see cref="PendAt"/>.</summary>
    public int Operations => Volatile.Read(ref _operations);

    public void Clear() => _posts.Clear();

    /// <summary>Makes operation <paramref name="operation"/> (counted from 0) the one that completes later.</summary>
    public void PendAt(int operation)
    {
        Volatile.Write(ref _operations, 0);
        _pendingAt = operation;
    }

    /// <summary>An operation that runs <paramref name="work"/>: at once, or later on the pool if it is the one to pend.</summary>
    public Task Operation(Action? work = null)
    {
        if (Interlocked.Increment(ref _operations) - 1 != _pendingAt)
        {
            work?.Invoke();
            return Task.CompletedTask;
        }

        return Task.Delay(1).ContinueWith(
            _ => work?.Invoke(),
            CancellationToken.None,
            TaskContinuationOptions.RunContinuationsAsynchronously,
            TaskScheduler.Default);
    }

    /// <summary>Starts work on this thread under the recording context, and puts the thread's own back.</summary>
    public Task Start(Func<Task> work)
    {
        SynchronizationContext? original = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new Recording(this));

        try
        {
            return work();
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(original);
        }
    }

    /// <summary>The items, the first and every <paramref name="every"/>th after an operation of the probe.</summary>
    public async IAsyncEnumerable<T> Slowly<T>(IEnumerable<T> items, int every = 1)
    {
        int index = 0;

        foreach (T item in items)
        {
            if (index++ % every == 0)
            {
                await Operation().ConfigureAwait(false);
            }

            yield return item;
        }
    }

    private sealed class Recording(PostProbe probe) : SynchronizationContext
    {
        public override void Post(SendOrPostCallback d, object? state)
        {
            probe._posts.Enqueue(Describe(state));
            ThreadPool.QueueUserWorkItem(_ => d(state));
        }

        public override void Send(SendOrPostCallback d, object? state)
        {
            probe._posts.Enqueue(Describe(state));
            d(state);
        }

        public override SynchronizationContext CreateCopy() => new Recording(probe);

        private static string Describe(object? state) =>
            (state as Delegate)?.Target?.ToString() ?? state?.ToString() ?? "a continuation";
    }
}

/// <summary>A write target whose every operation is one of the probe's.</summary>
internal sealed class PostProbeStream(PostProbe probe) : Stream
{
    private long _written;

    /// <summary>The bytes written, counted as each write completes.</summary>
    public long Written => Interlocked.Read(ref _written);

    public override bool CanRead => false;

    public override bool CanSeek => false;

    public override bool CanWrite => true;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        int count = buffer.Length;
        return new ValueTask(probe.Operation(() => Interlocked.Add(ref _written, count)));
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override Task FlushAsync(CancellationToken cancellationToken) => probe.Operation();

    public override ValueTask DisposeAsync() => new(probe.Operation());

    public override void Write(byte[] buffer, int offset, int count) => throw Synchronous();

    public override void Flush() => throw Synchronous();

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    private static InvalidOperationException Synchronous() => new("Synchronous operations are disallowed.");
}
