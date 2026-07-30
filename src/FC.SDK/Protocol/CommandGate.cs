namespace FC.SDK.Protocol;

/// <summary>
/// Serialises blocking transport calls behind one deadline each.
/// </summary>
/// <remarks>
/// <para>
/// Extracted from the WPD transport so the deadline behaviour can be tested without COM, a camera, or
/// real elapsed time — every wait goes through <see cref="TimeProvider"/>, so a
/// <c>FakeTimeProvider</c> can drive it. Kept transport-agnostic because the same problem exists
/// anywhere a synchronous, uncancellable native call has to be given a bound.
/// </para>
/// <para>
/// The hard constraint it is built around: a COM call in flight cannot be cancelled. So a deadline
/// here does not stop the work — it stops the <em>waiting</em>, and reports that the work is still out
/// there. Two consequences shape the design:
/// </para>
/// <list type="bullet">
/// <item>The lock is released by the offloaded work itself, not by whoever awaited it, so abandoning a
/// wait can never let a second call in against the same device.</item>
/// <item>Acquiring the lock is bounded too. A call stuck forever keeps holding it, and an unbounded
/// queue behind it would inherit exactly the hang the deadline exists to prevent.</item>
/// </list>
/// </remarks>
internal sealed class CommandGate(TimeProvider time, TimeSpan timeout)
{
    private readonly SemaphoreSlim _lock = new(1, 1);

    /// <summary>How long one command may take, and how long acquiring the gate may take.</summary>
    internal TimeSpan Timeout { get; set; } = timeout;

    /// <summary>
    /// Runs <paramref name="work"/> exclusively, returning <paramref name="onTimeout"/> if it does not
    /// finish within <see cref="Timeout"/> — counting the wait for the gate as well as the call itself.
    /// </summary>
    internal async Task<T> RunAsync<T>(Func<T> work, T onTimeout, CancellationToken ct = default)
    {
        if (!await TryEnterAsync(ct).ConfigureAwait(false))
            return onTimeout;

        // The thread hop is load-bearing, not tidiness: SemaphoreSlim.WaitAsync completes
        // synchronously when uncontended, so awaiting it need not yield. Without an explicit hop a
        // blocking call would run inline on the caller's thread and no deadline could be observed.
        var running = Task.Run(() =>
        {
            try { return work(); }
            finally { _lock.Release(); }
        }, CancellationToken.None);

        return await AwaitWithDeadlineAsync(running, onTimeout, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Runs a multi-phase <paramref name="work"/> sequence exclusively under one deadline. Each phase
    /// inside offloads its own blocking call with <see cref="Offload"/> and awaits it, so no thread
    /// ever sits in a blocking <c>Wait</c> — the only thread parked is the one the native call itself
    /// is holding, which nothing can avoid.
    /// </summary>
    internal async Task<T> RunAsync<T>(Func<Task<T>> work, T onTimeout, CancellationToken ct = default)
    {
        if (!await TryEnterAsync(ct).ConfigureAwait(false))
            return onTimeout;

        var running = Task.Run(work, CancellationToken.None);

        // The gate is released by the sequence FINISHING, not by us still caring about it — same
        // invariant the single-call path gets from its finally block, so abandoning the wait can
        // never let a second caller in against the same device.
        _ = running.ContinueWith(
            static (t, state) =>
            {
                _ = t.Exception;
                ((SemaphoreSlim)state!).Release();
            },
            _lock, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

        return await AwaitWithDeadlineAsync(running, onTimeout, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Moves one blocking native call off the awaiting thread. The thread hop is the whole point: a
    /// synchronous COM call cannot be cancelled, so the only way to bound it is to wait on it from
    /// somewhere else.
    /// </summary>
    internal static Task<T> Offload<T>(Func<T> syncCall) => Task.Run(syncCall, CancellationToken.None);

    /// <summary>
    /// Takes the gate, giving up after <see cref="Timeout"/>. False means a previous command is still
    /// holding it — i.e. the transport is wedged, not merely busy.
    /// </summary>
    private async Task<bool> TryEnterAsync(CancellationToken ct)
    {
        var acquire = _lock.WaitAsync(ct);
        if (acquire.IsCompletedSuccessfully) return true;

        if (await RaceAgainstDeadlineAsync(acquire, ct).ConfigureAwait(false))
        {
            // "Finished" is not "acquired". A cancelled or faulted wait holds no permit, and treating
            // it as entry would run the work unlocked and then release a permit it never took —
            // throwing SemaphoreFullException and, worse, letting two native calls touch the same
            // device at once. Observe any exception so it is not reported as unhandled.
            _ = acquire.Exception;
            return acquire.IsCompletedSuccessfully;
        }

        // Abandoned, but the wait is still queued on the semaphore: if it later succeeds nobody is
        // going to run the work or release the permit, so hand it straight back. Without this, every
        // timed-out acquire would permanently consume the gate.
        _ = acquire.ContinueWith(
            static (t, state) =>
            {
                if (t.IsCompletedSuccessfully) ((SemaphoreSlim)state!).Release();
            },
            _lock, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

        return false;
    }

    private async Task<T> AwaitWithDeadlineAsync<T>(Task<T> work, T onTimeout, CancellationToken ct)
    {
        return await RaceAgainstDeadlineAsync(work, ct).ConfigureAwait(false)
            ? await work.ConfigureAwait(false)
            : onTimeout;
    }

    /// <summary>True if <paramref name="task"/> won the race; false if the deadline did.</summary>
    private async Task<bool> RaceAgainstDeadlineAsync(Task task, CancellationToken ct)
    {
        using var expiryCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        // The TimeProvider overload of Task.Delay, so a FakeTimeProvider can expire it on demand
        // rather than the test having to wait out a real interval.
        var expiry = Task.Delay(Timeout, time, expiryCts.Token);
        var winner = await Task.WhenAny(task, expiry).ConfigureAwait(false);

        // Release the timer either way, rather than leaving one pending per command for the full
        // timeout. A cancelled Delay completes as cancelled, never faulted, so nothing is left
        // unobserved.
        await expiryCts.CancelAsync().ConfigureAwait(false);

        return winner == task;
    }
}
