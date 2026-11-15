using System.Buffers.Binary;
using FC.SDK.Canon;
using FC.SDK.Protocol;
using FC.SDK.Transport;
using Shouldly;
using Xunit;

namespace FC.SDK.Tests;

/// <summary>
/// What stopping the event pump does to the GetEvent it has in flight.
/// </summary>
/// <remarks>
/// Cancelling it left its reply in a PTP/IP stream, where the next command read it as its own:
/// CloseSession failed in 2 runs of 10 on an EOS 6D (issue #29). A stop now lets the round trip
/// finish, and cancels only one that does not.
/// </remarks>
public class EventPollerStopTests
{
    /// <summary>A body whose reply to GetEvent comes when the test says so, and which notes a cancelled wait.</summary>
    private sealed class SlowTransport : IPtpTransport
    {
        public readonly TaskCompletionSource Asked = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource Answer = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public volatile bool Cancelled;

        public Task ConnectAsync(CancellationToken ct = default) => Task.CompletedTask;
        public bool IsConnected => true;
        public string DeviceId => "fake";
        public ValueTask SendAsync(ReadOnlyMemory<byte> packet, CancellationToken ct = default) => ValueTask.CompletedTask;

        public async ValueTask<int> ReceiveAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            Asked.TrySetResult();
            try
            {
                await Answer.Task.WaitAsync(ct);
            }
            catch (OperationCanceledException)
            {
                Cancelled = true;
                throw;
            }

            // A bare response container: DeviceBusy, which ends a drain without a data phase.
            BinaryPrimitives.WriteUInt32LittleEndian(buffer.Span, 12);
            BinaryPrimitives.WriteUInt16LittleEndian(buffer.Span[4..], (ushort)PtpContainerType.Response);
            BinaryPrimitives.WriteUInt16LittleEndian(buffer.Span[6..], (ushort)PtpResponseCode.DeviceBusy);
            return 12;
        }

        public ValueTask<int> ReceiveEventAsync(Memory<byte> buffer, CancellationToken ct = default) => ValueTask.FromResult(0);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    [Fact]
    public async Task A_stop_lets_the_round_trip_in_flight_finish()
    {
        var transport = new SlowTransport();
        var poller = new EventPoller(new CanonPtpSession(new PtpSession(transport)), TimeSpan.FromMilliseconds(10));
        poller.Start();
        await transport.Asked.Task.WaitAsync(TestContext.Current.CancellationToken);

        var stop = poller.DisposeAsync().AsTask();
        transport.Answer.SetResult();
        await stop.WaitAsync(TestContext.Current.CancellationToken);

        transport.Cancelled.ShouldBeFalse();
    }

    [Fact]
    public async Task A_round_trip_that_outlasts_the_grace_is_cancelled_so_the_stop_returns()
    {
        var transport = new SlowTransport();
        var poller = new EventPoller(
            new CanonPtpSession(new PtpSession(transport)), TimeSpan.FromMilliseconds(10), stopGrace: TimeSpan.FromMilliseconds(50));
        poller.Start();
        await transport.Asked.Task.WaitAsync(TestContext.Current.CancellationToken);

        await poller.DisposeAsync().AsTask().WaitAsync(TestContext.Current.CancellationToken);

        transport.Cancelled.ShouldBeTrue();
    }
}
