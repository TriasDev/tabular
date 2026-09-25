using System.Diagnostics;
using System.Runtime.InteropServices;

namespace TriasDev.Tabular.Benchmarks.Shared;

/// <summary>
/// Peak resident memory of the current process — the number that decides whether a reader fits in
/// the memory a host gives it.
/// </summary>
/// <remarks>
/// <para>
/// Peak, not allocated. Allocated bytes add up everything the garbage collector handed out and took
/// back over the whole run, so a reader that recycles a small buffer millions of times reports
/// gigabytes while never holding more than a few megabytes. What a container or a server runs out
/// of is what is held at the worst moment.
/// </para>
/// <para>
/// <see cref="Process.PeakWorkingSet64"/> is populated on Windows and returns zero on macOS, so on
/// Unix the number comes from <c>getrusage</c> instead. The unit of <c>ru_maxrss</c> differs between
/// the two Unixes it matters on — bytes on macOS, kilobytes on Linux — which is a trap worth naming
/// rather than discovering through a result that is off by a factor of a thousand.
/// </para>
/// <para>
/// It only ever rises within a process, which is why every measurement runs in a process of its own.
/// </para>
/// </remarks>
internal static class PeakMemory
{
    private const int RUSAGE_SELF = 0;

    public static long ResidentBytes()
    {
        long reported = Process.GetCurrentProcess().PeakWorkingSet64;

        if (reported > 0)
        {
            return reported;
        }

        if (getrusage(RUSAGE_SELF, out RUsage usage) != 0)
        {
            return 0;
        }

        return OperatingSystem.IsMacOS() ? usage.MaxResidentSetSize : usage.MaxResidentSetSize * 1024;
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int getrusage(int who, out RUsage usage);

    [StructLayout(LayoutKind.Sequential)]
    private struct RUsage
    {
        public long UserSeconds;
        public long UserMicroseconds;
        public long SystemSeconds;
        public long SystemMicroseconds;
        public long MaxResidentSetSize;
        private readonly long _integralSharedMemory;
        private readonly long _integralUnsharedData;
        private readonly long _integralUnsharedStack;
        private readonly long _pageReclaims;
        private readonly long _pageFaults;
        private readonly long _swaps;
        private readonly long _blockInputOperations;
        private readonly long _blockOutputOperations;
        private readonly long _messagesSent;
        private readonly long _messagesReceived;
        private readonly long _signalsReceived;
        private readonly long _voluntaryContextSwitches;
        private readonly long _involuntaryContextSwitches;
    }
}
