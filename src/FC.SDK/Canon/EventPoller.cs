namespace FC.SDK.Canon;

/// <summary>
/// Drives GetEvent (0x9116) on a timer. Decoding, property-cache updates and event dispatch all
/// happen inside <see cref="CanonPtpSession.PollEventsAsync"/>, so this type only supplies the
/// heartbeat — every consumer sees the same stream regardless of who polled.
/// </summary>
/// <remarks>
/// Polling is not optional on EOS bodies: an undrained event queue makes the camera answer
/// DeviceBusy to property writes, and property reads are served from the cache the stream fills.
/// </remarks>
internal sealed class EventPoller(CanonPtpSession session, TimeSpan interval) : IAsyncDisposable
{
    /// <summary>
    /// GetEvent round trips per tick. Deliberately small: during live view the camera re-announces
    /// properties continuously, and an unbounded drain here would starve the viewfinder stream.
    /// Anything left over is picked up on the next tick.
    /// </summary>
    private const int RoundsPerTick = 8;

    private Task? _loopTask;
    private readonly CancellationTokenSource _cts = new();

    public void Start()
    {
        _loopTask = Task.Run(RunAsync);
    }

    private async Task RunAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                // Drain rather than single-poll: a burst (capture, property sweep) queues more
                // records than one round trip returns, and leftovers block property writes.
                await session.DrainEventsAsync(_cts.Token, RoundsPerTick);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                // Swallow transport errors during polling; camera may be busy
            }

            await Task.Delay(interval, _cts.Token)
                .ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();
        if (_loopTask is not null)
        {
            try { await _loopTask; }
            catch (OperationCanceledException) { }
        }
        _cts.Dispose();
    }
}
