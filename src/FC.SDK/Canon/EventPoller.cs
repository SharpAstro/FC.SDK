namespace FC.SDK.Canon;

/// <summary>
/// Drives GetEvent (0x9116) on a timer. Decoding, property-cache updates and event dispatch all
/// happen inside <see cref="CanonPtpSession.PollEventsAsync"/>, so this type only supplies the
/// heartbeat — every consumer sees the same stream regardless of who polled.
/// </summary>
/// <remarks>
/// <para>
/// Polling is not optional on EOS bodies: an undrained event queue makes the camera answer
/// DeviceBusy to property writes, and property reads are served from the cache the stream fills.
/// </para>
/// <para>
/// A transport failure ends the loop and is reported once through <paramref name="lost"/>. It used to
/// be swallowed with every other error, so pulling the cable left a session that looked alive: no
/// event, no exception, and property reads still answering OK out of the cache. Measured on an EOS R6
/// and an EOS 6D over WPD (raw ioctl): the first real transaction after the pull throws
/// <see cref="IOException"/> "WPD ioctl failed with Win32 error 995", every later one too, and the body
/// leaves <c>EnumerateWpdCameras</c> within 5 s. No false alarm on the 6D over 90 s of live view
/// (1254 frames) with the pump running. PTP/IP and USB throw <see cref="IOException"/> too, but neither
/// has been unplugged under this. WPD COM never gets here: <c>PtpSession</c> turns its
/// <c>COMException</c> into <c>GeneralError</c> instead of throwing, so over COM a pulled cable still
/// goes unreported (SharpAstro/FC.SDK#20).
/// </para>
/// </remarks>
internal sealed class EventPoller(CanonPtpSession session, TimeSpan interval, Action<Exception>? lost = null) : IAsyncDisposable
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
            catch (IOException ex)
            {
                // The link itself failed: polling again only repeats the same error. Stop, and say so.
                lost?.Invoke(ex);
                break;
            }
            catch
            {
                // Anything else is not a lost link (a busy or slow camera): keep polling.
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
