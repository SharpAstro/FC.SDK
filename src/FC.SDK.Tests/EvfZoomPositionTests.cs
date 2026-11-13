using System.Diagnostics;
using FC.SDK.Canon;
using FC.SDK.Transport;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

namespace FC.SDK.Tests;

/// <summary>
/// <see cref="CanonCamera.SetEvfZoomPositionAsync"/> end to end, over a scripted body.
/// </summary>
/// <remarks>
/// <see cref="EvfFrameInfoTests"/> pins the settle rule; this pins that the call uses it. Wired back to
/// the zoom rect (record 18), a 1× pan waits out the whole 3 s settle timeout, which is what an R6 and a
/// 6D did over WPD raw ioctl before the fix (3021–3144 ms, twelve tries).
/// </remarks>
public class EvfZoomPositionTests
{
    /// <summary>
    /// Answers the two operations a pan uses, as WPD does: <c>0x9159 ZoomPosition</c> with OK, and
    /// <c>0x9153 GetViewfinderData</c> with one envelope before the pan and another after it.
    /// </summary>
    private sealed class LiveViewResponder(byte[] before, byte[] after) : IMtpExtTransport
    {
        private const ushort Ok = 0x2001, NotSupported = 0x2005;
        private volatile bool _panned;

        public Task<(ushort ResponseCode, uint[] ResponseParams)> ExecuteCommandAsync(
            ushort opCode, uint[] @params, CancellationToken ct = default)
        {
            if (opCode is 0x9159) _panned = true;
            return Task.FromResult((opCode is 0x9159 ? Ok : NotSupported, Array.Empty<uint>()));
        }

        public Task<(ushort ResponseCode, uint[] ResponseParams, byte[] Data)> ExecuteCommandReadDataAsync(
            ushort opCode, uint[] @params, CancellationToken ct = default) =>
            Task.FromResult(opCode is 0x9153
                ? (Ok, Array.Empty<uint>(), _panned ? after : before)
                : (NotSupported, Array.Empty<uint>(), Array.Empty<byte>()));

        public Task<(ushort ResponseCode, uint[] ResponseParams)> ExecuteCommandWriteDataAsync(
            ushort opCode, uint[] @params, byte[] data, CancellationToken ct = default) =>
            Task.FromResult((NotSupported, Array.Empty<uint>()));

        public Task ConnectAsync(CancellationToken ct = default) => Task.CompletedTask;
        public bool IsConnected => true;
        public string DeviceId => "scripted";
        public ValueTask SendAsync(ReadOnlyMemory<byte> packet, CancellationToken ct = default) => throw new NotSupportedException();
        public ValueTask<int> ReceiveAsync(Memory<byte> buffer, CancellationToken ct = default) => throw new NotSupportedException();
        public ValueTask<int> ReceiveEventAsync(Memory<byte> buffer, CancellationToken ct = default) => throw new NotSupportedException();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    [Fact]
    public async Task A_one_times_pan_returns_once_the_magnifier_moves()
    {
        var transport = new LiveViewResponder(EvfFrameInfoTests.R61x(), EvfFrameInfoTests.R61xPannedTo1000());
        await using var camera = new CanonCamera(transport, NullLogger<CanonCamera>.Instance);

        var clock = Stopwatch.StartNew();
        var (error, landed) = await camera.SetEvfZoomPositionAsync(1000, 1000, ct: TestContext.Current.CancellationToken);
        clock.Stop();

        error.ShouldBe(EdsError.OK);
        clock.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(1));   // the settle timeout is 3 s
        landed.ShouldNotBeNull();
        (landed.Value.X, landed.Value.Y, landed.Value.Width).ShouldBe((0u, 0u, 5472u));   // still record 18: the whole sensor at 1×
    }
}
