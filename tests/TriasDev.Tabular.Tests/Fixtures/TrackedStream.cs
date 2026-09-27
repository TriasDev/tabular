

namespace TriasDev.Tabular.Tests.Fixtures;

/// <summary>A read-only stream over bytes that remembers whether it was closed.</summary>
/// <remarks>For the rule that a stream handed over is closed on every path, unless the caller asked otherwise.</remarks>
public sealed class TrackedStream(byte[] content) : MemoryStream(content, writable: false)
{
    public bool IsDisposed { get; private set; }

    protected override void Dispose(bool disposing)
    {
        IsDisposed = true;
        base.Dispose(disposing);
    }
}
