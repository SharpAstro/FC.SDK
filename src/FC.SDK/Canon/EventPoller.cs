namespace FC.SDK.Canon;

internal sealed class EventPoller(CanonPtpSession session, TimeSpan interval) : IAsyncDisposable
{
    private Task? _loopTask;
    private CancellationTokenSource _cts = new();

    public event Action<CanonPtpEvent>? EventReceived;

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
                var events = await session.PollEventsAsync(_cts.Token);
                foreach (var ev in events)
                    EventReceived?.Invoke(ev);
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
