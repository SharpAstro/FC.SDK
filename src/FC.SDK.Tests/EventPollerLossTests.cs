using System.Collections.Concurrent;
using FC.SDK.Transport;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

namespace FC.SDK.Tests;

/// <summary>
/// What the background event pump does when the link fails under it.
/// </summary>
/// <remarks>
/// Pinned because the failure used to be silent: the pump swallowed the error on every tick, so a pulled cable left a
/// session that answered property reads out of the cache with no sign anything was wrong. On an EOS R6 over WPD (raw
/// ioctl) every transaction after the pull throws <see cref="IOException"/> (Win32 error 995), which is what the fake
/// transport throws here.
/// </remarks>
public class EventPollerLossTests
{
    private sealed class FailingTransport(Func<Exception> failure) : IPtpTransport
    {
        public int Sends;
        public Task ConnectAsync(CancellationToken ct = default) => Task.CompletedTask;
        public bool IsConnected => true;
        public string DeviceId => "fake";

        public ValueTask SendAsync(ReadOnlyMemory<byte> packet, CancellationToken ct = default)
        {
            Interlocked.Increment(ref Sends);
            throw failure();
        }

        public ValueTask<int> ReceiveAsync(Memory<byte> buffer, CancellationToken ct = default) => throw failure();
        public ValueTask<int> ReceiveEventAsync(Memory<byte> buffer, CancellationToken ct = default) => throw failure();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    [Fact]
    public async Task A_lost_link_stops_the_pump_and_is_reported_once()
    {
        var transport = new FailingTransport(() => new IOException("WPD ioctl failed with Win32 error 995."));
        await using var camera = new CanonCamera(transport, NullLogger<CanonCamera>.Instance);
        var lost = new ConcurrentQueue<CanonConnectionLostEventArgs>();
        camera.ConnectionLost += (_, e) => lost.Enqueue(e);

        camera.StartEventPolling(TimeSpan.FromMilliseconds(10));
        for (int i = 0; i < 200 && lost.IsEmpty; i++) await Task.Delay(10, TestContext.Current.CancellationToken);
        int sendsWhenLost = transport.Sends;
        await Task.Delay(100, TestContext.Current.CancellationToken);

        lost.ShouldHaveSingleItem().Error.ShouldBeOfType<IOException>();
        transport.Sends.ShouldBe(sendsWhenLost);   // nothing more reaches the dead transport
    }

    [Fact]
    public async Task Any_other_failure_keeps_the_pump_running()
    {
        var transport = new FailingTransport(() => new TimeoutException("camera slow to answer"));
        await using var camera = new CanonCamera(transport, NullLogger<CanonCamera>.Instance);
        int lost = 0;
        camera.ConnectionLost += (_, _) => Interlocked.Increment(ref lost);

        camera.StartEventPolling(TimeSpan.FromMilliseconds(10));
        for (int i = 0; i < 200 && transport.Sends < 3; i++) await Task.Delay(10, TestContext.Current.CancellationToken);

        transport.Sends.ShouldBeGreaterThanOrEqualTo(3);
        lost.ShouldBe(0);
    }
}
