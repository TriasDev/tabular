using System.Runtime.ExceptionServices;

using Xunit;

namespace TriasDev.Tabular.Tests.Fixtures;

/// <summary>
/// Runs work that must end on its own, and fails the test once a deadline passes instead of waiting.
/// </summary>
/// <remarks>
/// For loops a token cannot reach: the scanner's tokenizer checks none, so a mistake in it that stops
/// the scan position from advancing spins forever. The work gets a background thread of its own, so an
/// abandoned loop holds neither a pool thread nor the process. Whatever the work throws is rethrown as
/// it was, so <c>Assert.Throws</c> sees the exception itself.
/// </remarks>
internal static class Deadline
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(20);

    public static T Within<T>(Func<T> work)
    {
        T result = default!;
        ExceptionDispatchInfo? failure = null;

        Thread thread = new(() =>
        {
            try
            {
                result = work();
            }
            catch (Exception failed)
            {
                failure = ExceptionDispatchInfo.Capture(failed);
            }
        })
        { IsBackground = true };

        thread.Start();

        if (!thread.Join(Limit))
        {
            Assert.Fail($"The work did not end within {Limit.TotalSeconds} seconds: it loops.");
        }

        failure?.Throw();
        return result;
    }

    public static void Within(Action work) =>
        Within(() =>
        {
            work();
            return true;
        });
}
