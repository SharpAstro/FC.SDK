namespace FC.SDK.Hosts;

/// <summary>
/// A stopwatch on a <see cref="TimeProvider"/>: what <c>System.Diagnostics.Stopwatch</c> is on the
/// system clock, with the members the hosts use. Compiled into the viewer, the diagnostics and the
/// sample, so that their timings run on the same clock as the camera's waits.
/// </summary>
internal sealed class ClockWatch
{
    private readonly TimeProvider _time;
    private long _started;
    private TimeSpan? _stopped;

    private ClockWatch(TimeProvider time)
    {
        _time = time;
        _started = time.GetTimestamp();
    }

    public static ClockWatch StartNew(TimeProvider time) => new(time);

    /// <summary>Time since the start, or up to <see cref="Stop"/> once that was called.</summary>
    public TimeSpan Elapsed => _stopped ?? _time.GetElapsedTime(_started);

    public long ElapsedMilliseconds => (long)Elapsed.TotalMilliseconds;

    /// <summary>Holds <see cref="Elapsed"/> at what it reads now.</summary>
    public void Stop() => _stopped ??= _time.GetElapsedTime(_started);

    public void Restart()
    {
        _stopped = null;
        _started = _time.GetTimestamp();
    }
}
