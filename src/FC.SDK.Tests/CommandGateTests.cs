using FC.SDK.Protocol;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;
using Xunit.Sdk;

namespace FC.SDK.Tests;

/// <summary>
/// Deadline behaviour for blocking transport calls.
/// </summary>
/// <remarks>
/// These exist because the bug they guard against is otherwise unreproducible without hardware: an
/// EOS 450D advertised GetViewFinderData, answered ObjectNotReady ten times, and then never replied at
/// all — wedging the WPD transport, starving every queued command, and leaving a UI that still looked
/// perfectly healthy. A <see cref="FakeTimeProvider"/> reproduces that shape in milliseconds.
/// </remarks>
public class CommandGateTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    private static CommandGate Gate(FakeTimeProvider time) => new(time, Timeout);

    [Fact]
    public async Task Returns_the_result_when_the_call_completes()
    {
        var gate = Gate(new FakeTimeProvider());

        var result = await gate.RunAsync(() => 42, onTimeout: -1);

        result.ShouldBe(42);
    }

    [Fact]
    public async Task Returns_the_timeout_value_when_the_call_never_finishes()
    {
        var time = new FakeTimeProvider();
        var gate = Gate(time);
        using var stuck = new ManualResetEventSlim(false);

        // Stands in for a COM call that accepted the request and never answered.
        var call = gate.RunAsync(() => { stuck.Wait(); return 42; }, onTimeout: -1);

        await WaitUntilPendingAsync(call);
        time.Advance(Timeout);

        (await call).ShouldBe(-1);

        stuck.Set();   // let the abandoned worker finish so it releases the gate
    }

    [Fact]
    public async Task A_wedged_call_does_not_hang_the_next_one()
    {
        // The actual failure: one unanswered command starved everything queued behind it. The second
        // caller must get a timeout of its own rather than inheriting the first one's hang.
        var time = new FakeTimeProvider();
        var gate = Gate(time);
        using var stuck = new ManualResetEventSlim(false);

        var first = gate.RunAsync(() => { stuck.Wait(); return 1; }, onTimeout: -1);
        await WaitUntilPendingAsync(first);

        var second = gate.RunAsync(() => 2, onTimeout: -1);

        time.Advance(Timeout);
        (await first).ShouldBe(-1);

        time.Advance(Timeout);
        (await second).ShouldBe(-1, "the gate is still held by the wedged call, so this must time out too");

        stuck.Set();
    }

    [Fact]
    public async Task The_gate_is_usable_again_once_a_wedged_call_finally_returns()
    {
        // A timed-out acquire leaves a wait queued on the semaphore. If that wait later succeeds with
        // nobody to release it, the gate would be consumed forever and the transport dead until
        // reconnect — so the abandoned waiter has to hand the permit straight back.
        var time = new FakeTimeProvider();
        var gate = Gate(time);
        using var stuck = new ManualResetEventSlim(false);

        var first = gate.RunAsync(() => { stuck.Wait(); return 1; }, onTimeout: -1);
        await WaitUntilPendingAsync(first);

        var abandoned = gate.RunAsync(() => 2, onTimeout: -1);
        time.Advance(Timeout);
        await first;
        time.Advance(Timeout);
        await abandoned;

        stuck.Set();   // the wedged call returns; the gate must recover

        await WaitUntilAsync(async () => await gate.RunAsync(() => 3, onTimeout: -1) == 3,
            because: "a later command should succeed once the gate is free again");
    }

    [Fact]
    public async Task Honours_cancellation_without_waiting_for_the_deadline()
    {
        var time = new FakeTimeProvider();
        var gate = Gate(time);
        using var stuck = new ManualResetEventSlim(false);
        using var cts = new CancellationTokenSource();

        var first = gate.RunAsync(() => { stuck.Wait(); return 1; }, onTimeout: -1);
        await WaitUntilPendingAsync(first);

        // Queued behind the wedged call, then cancelled. The clock never moves.
        var queued = gate.RunAsync(() => 2, onTimeout: -1, cts.Token);
        await cts.CancelAsync();

        (await queued).ShouldBe(-1);

        time.Advance(Timeout);
        await first;
        stuck.Set();
    }

    [Fact]
    public async Task A_cancelled_wait_does_not_corrupt_the_gate()
    {
        // Regression: the cancelled wait finished, which was mistaken for having acquired the permit.
        // The work then ran WITHOUT the lock and released on the way out, throwing
        // SemaphoreFullException — and, before it threw, allowing two native calls against the same
        // device concurrently. Observed on a real teardown as
        // "Adding the specified count to the semaphore would cause it to exceed its maximum count".
        var time = new FakeTimeProvider();
        var gate = Gate(time);
        using var stuck = new ManualResetEventSlim(false);
        using var cts = new CancellationTokenSource();

        var holder = gate.RunAsync(() => { stuck.Wait(); return 1; }, onTimeout: -1);
        await WaitUntilPendingAsync(holder);

        var cancelled = gate.RunAsync(() => 2, onTimeout: -1, cts.Token);
        await cts.CancelAsync();
        (await cancelled).ShouldBe(-1);

        // Let the real holder finish and hand its permit back.
        time.Advance(Timeout);
        await holder;
        stuck.Set();

        // If the cancelled attempt had over-released, the gate would now admit two callers at once —
        // or the next Release would throw. Both are caught by simply using it again.
        await WaitUntilAsync(async () => await gate.RunAsync(() => 3, onTimeout: -1) == 3,
            because: "the gate must still be intact after a cancelled wait");
    }

    [Fact]
    public async Task Never_admits_two_callers_at_once_when_a_wait_is_cancelled()
    {
        // Asserts the property the bug actually broke. Checking for SemaphoreFullException is not
        // enough: an over-release only throws once the count is already at its maximum, so with the
        // wrong ordering the corruption passes silently. What is always observable is that a permit
        // appeared out of nowhere and let a second caller in.
        var time = new FakeTimeProvider();
        var gate = Gate(time);
        using var holderInside = new ManualResetEventSlim(false);
        using var releaseHolder = new ManualResetEventSlim(false);

        var concurrent = 0;
        var overlapped = false;

        int Tracked(ManualResetEventSlim? entered, ManualResetEventSlim? release)
        {
            // Only the fact matters, not the peak, so a plain flag beats tracking a maximum.
            if (Interlocked.Increment(ref concurrent) > 1) Volatile.Write(ref overlapped, true);
            entered?.Set();
            release?.Wait(TimeSpan.FromSeconds(5));
            Interlocked.Decrement(ref concurrent);
            return 0;
        }

        var holder = gate.RunAsync(() => Tracked(holderInside, releaseHolder), onTimeout: -1);
        holderInside.Wait(TimeSpan.FromSeconds(5)).ShouldBeTrue("the first call should be inside the gate");

        using var cts = new CancellationTokenSource();
        var cancelled = gate.RunAsync(() => Tracked(null, null), onTimeout: -1, cts.Token);
        await cts.CancelAsync();
        await cancelled;

        releaseHolder.Set();
        time.Advance(Timeout);
        await holder;

        Volatile.Read(ref overlapped)
            .ShouldBeFalse("a cancelled wait holds no permit, so it must never run the work");
    }

    [Fact]
    public async Task Does_not_time_out_a_call_that_finishes_inside_the_deadline()
    {
        var time = new FakeTimeProvider();
        var gate = Gate(time);
        using var release = new ManualResetEventSlim(false);

        var call = gate.RunAsync(() => { release.Wait(); return 7; }, onTimeout: -1);
        await WaitUntilPendingAsync(call);

        // Just short of the deadline, then the call lands.
        time.Advance(Timeout - TimeSpan.FromMilliseconds(1));
        release.Set();

        (await call).ShouldBe(7);
    }

    /// <summary>
    /// Waits until <paramref name="task"/> is actually in flight. Needed because the work runs on the
    /// thread pool: advancing a FakeTimeProvider before the delay is registered would expire nothing.
    /// </summary>
    private static Task WaitUntilPendingAsync(Task task) =>
        WaitUntilAsync(() => Task.FromResult(!task.IsCompleted), "the call should still be running");

    private static async Task WaitUntilAsync(Func<Task<bool>> condition, string because)
    {
        // Real time here, deliberately: this is synchronising with the thread pool, not with the
        // camera clock the fake provider stands in for.
        for (var i = 0; i < 200; i++)
        {
            if (await condition()) return;
            await Task.Delay(10);
        }
        throw new XunitException($"Timed out waiting: {because}");
    }
}
