using Microsoft.Extensions.Time.Testing;

namespace FC.SDK.Tests;

/// <summary>
/// Drives code that waits on a <see cref="FakeTimeProvider"/>: moves the clock until the work is done.
/// </summary>
internal static class FakeClock
{
    /// <summary>
    /// Advances <paramref name="time"/> in <paramref name="step"/>s until <paramref name="work"/>
    /// completes, and answers how much fake time that took. It stops at <paramref name="budget"/> of
    /// fake time, so a piece of work that never finishes ends the loop instead of spinning it; the
    /// caller then finds the task incomplete.
    /// </summary>
    /// <remarks>
    /// Only for work whose every await is either already complete or a wait on this clock (a fake
    /// camera, a scripted transport): its continuations then run inside <c>Advance</c>, on this
    /// thread, and the loop never turns without progress.
    /// </remarks>
    public static TimeSpan Pump(this FakeTimeProvider time, Task work, TimeSpan step, TimeSpan budget)
    {
        var started = time.GetTimestamp();
        while (!work.IsCompleted && time.GetElapsedTime(started) < budget)
            time.Advance(step);
        return time.GetElapsedTime(started);
    }
}
