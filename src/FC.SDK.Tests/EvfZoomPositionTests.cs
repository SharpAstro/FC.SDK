using System.Buffers.Binary;
using FC.SDK.Canon;
using FC.SDK.Transport;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
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
/// <para>
/// On a fake clock: the camera's settle wait runs on its <see cref="TimeProvider"/>, so the test
/// measures how much of that clock the call used, and waits for nothing.
/// </para>
/// </remarks>
public class EvfZoomPositionTests
{
    /// <summary>
    /// Answers the two operations a pan uses: <c>0x9159 ZoomPosition</c> with OK, and each
    /// <c>0x9153 GetViewfinderData</c> with the next scripted frame, moving the clock by that poll's
    /// duration. Two frames play a pan as WPD delivers it, one before and one after; a longer script
    /// with poll times plays an R6 over PTP/IP, where a poll can return a frame the body made long
    /// before it. The last frame is repeated once the script runs out.
    /// </summary>
    private sealed class ScriptedFrames(FakeTimeProvider time, params (int PollMs, byte[] Envelope)[] frames) : IMtpExtTransport
    {
        private const ushort Ok = 0x2001, NotSupported = 0x2005;
        private int _next;

        /// <summary>How many frames the camera has asked for.</summary>
        public int Polls => _next;

        public Task<(ushort ResponseCode, uint[] ResponseParams)> ExecuteCommandAsync(
            ushort opCode, uint[] @params, CancellationToken ct = default) =>
            Task.FromResult((opCode is 0x9159 ? Ok : NotSupported, Array.Empty<uint>()));

        public Task<(ushort ResponseCode, uint[] ResponseParams, byte[] Data)> ExecuteCommandReadDataAsync(
            ushort opCode, uint[] @params, CancellationToken ct = default)
        {
            if (opCode is not 0x9153) return Task.FromResult((NotSupported, Array.Empty<uint>(), Array.Empty<byte>()));
            var (pollMs, envelope) = frames[Math.Min(_next++, frames.Length - 1)];
            time.Advance(TimeSpan.FromMilliseconds(pollMs));
            return Task.FromResult((Ok, Array.Empty<uint>(), envelope));
        }

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
        var time = new FakeTimeProvider();
        var transport = new ScriptedFrames(time, (0, EvfFrameInfoTests.R61x()), (0, EvfFrameInfoTests.R61xPannedTo1000()));
        await using var camera = new CanonCamera(transport, NullLogger<CanonCamera>.Instance, time);

        var pan = camera.SetEvfZoomPositionAsync(1000, 1000, ct: TestContext.Current.CancellationToken);
        var waited = time.Pump(pan, step: TimeSpan.FromMilliseconds(50), budget: TimeSpan.FromSeconds(10));

        pan.IsCompleted.ShouldBeTrue("the pan was still waiting after 10 s of its own clock");
        var (error, landed) = await pan;
        error.ShouldBe(EdsError.OK);
        waited.ShouldBeLessThan(TimeSpan.FromSeconds(1));   // the settle timeout is 3 s
        landed.ShouldNotBeNull();
        (landed.Value.X, landed.Value.Y, landed.Value.Width).ShouldBe((0u, 0u, 5472u));   // still record 18: the whole sensor at 1×
    }

    // The R6's body clock in run 2 of issue #30 (0x6AF9CA05 s); a frame's stamp is that plus its
    // made time in ms from the start of the test, so host time minus body time is a constant.
    private const long BodyClockMs = 0x6AF9CA05L * 1000;

    /// <summary>An R6 frame at 5× with the magnifier at (<paramref name="x"/>, <paramref name="y"/>), made <paramref name="madeMs"/> into the test.</summary>
    private static byte[] R6At5x(uint x, uint y, long madeMs)
    {
        var stamp = BodyClockMs + madeMs;
        (uint Type, uint[] Words)[] records =
        [
            (1, []),                                       // the JPEG, empty: nothing here reads it
            (4, [5]),
            (18, [x, y, 1104, 736]),
            (13, [x, y, 1104, 736]),
            (14, [5472, 3648]),
            (10, [(uint)(stamp / 1000), (uint)(stamp % 1000)]),
        ];
        var envelope = new byte[records.Sum(r => 8 + 4 * r.Words.Length)];
        var at = 0;
        foreach (var (type, words) in records)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(envelope.AsSpan(at), (uint)(8 + 4 * words.Length));
            BinaryPrimitives.WriteUInt32LittleEndian(envelope.AsSpan(at + 4), type);
            for (var i = 0; i < words.Length; i++)
                BinaryPrimitives.WriteUInt32LittleEndian(envelope.AsSpan(at + 8 + 4 * i), words[i]);
            at += 8 + 4 * words.Length;
        }
        return envelope;
    }

    /// <summary>
    /// The stale-start shape, modelled on run 2 of issue #30 (<c>r1 5x quick back (0,0)</c>, where the
    /// call returned (3000,2000) at 138 ms on the R6); the timings are the test's own. The last frame
    /// received before the send shows (196,212), but the body had already moved to (3000,2000) for an
    /// earlier pan, and that landing arrives after the send. On arrival alone it reads as this pan's
    /// landing. It was made before the send, so it only moves the start; the next frame, made after
    /// the send but before the body shows this pan, does not settle either; the clamped landing does.
    /// </summary>
    [Fact]
    public async Task A_frame_made_before_the_pan_is_not_its_landing()
    {
        var time = new FakeTimeProvider();
        var transport = new ScriptedFrames(time,
            (30, R6At5x(196, 212, madeMs: -5)),     // the caller's own live view at 0 ms: a fresh frame sets the clock offset
            (25, R6At5x(196, 212, madeMs: 150)),    // the start read at 300 ms: a frame made 150 ms before it
            (130, R6At5x(3000, 2000, madeMs: 260)), // the first poll after the send (325 ms): an earlier pan's landing, made before the send
            (30, R6At5x(3000, 2000, madeMs: 400)),  // made after the send; the body has not shown the pan yet
            (30, R6At5x(196, 212, madeMs: 650)));   // the landing: (0,0) clamped to the corner of the AF area
        await using var camera = new CanonCamera(transport, NullLogger<CanonCamera>.Instance, time);
        var ct = TestContext.Current.CancellationToken;

        (await camera.GetLiveViewFrameAndInfoAsync(ct)).Error.ShouldBe(EdsError.OK);
        time.Advance(TimeSpan.FromMilliseconds(270));

        var pan = camera.SetEvfZoomPositionAsync(0, 0, ct: ct);
        var waited = time.Pump(pan, step: TimeSpan.FromMilliseconds(10), budget: TimeSpan.FromSeconds(10));

        pan.IsCompleted.ShouldBeTrue("the pan was still waiting after 10 s of its own clock");
        var (error, landed) = await pan;
        error.ShouldBe(EdsError.OK);
        landed.ShouldNotBeNull();
        (landed.Value.X, landed.Value.Y).ShouldBe((196u, 212u));   // not (3000,2000), the earlier pan's landing
        waited.ShouldBeLessThan(TimeSpan.FromSeconds(1));          // settled on the landing, not on the 3 s timeout
    }

    /// <summary>
    /// The stale-target shape: the target is where the magnifier stood a moment before, so a frame
    /// made before the send already shows it exactly. Judged on arrival that frame settled the call
    /// while the body was elsewhere (four such returns on an R6 over PTP/IP in #27, 166–335 ms). Made
    /// before the send, it only moves the start, and the call reads on to the first frame made after
    /// the send that shows the target. The timings are the test's own.
    /// </summary>
    [Fact]
    public async Task A_frame_made_before_the_pan_that_shows_its_target_is_not_its_landing()
    {
        var time = new FakeTimeProvider();
        var transport = new ScriptedFrames(time,
            (30, R6At5x(1000, 1000, madeMs: -5)),    // the caller's own live view at 0 ms: a fresh frame sets the clock offset
            (25, R6At5x(1000, 1000, madeMs: 50)),    // the start read at 300 ms: the target, before an earlier pan moved the magnifier
            (30, R6At5x(1000, 1000, madeMs: 60)),    // the first poll after the send (325 ms): the target, made before the send
            (100, R6At5x(3000, 2000, madeMs: 200)),  // the earlier pan's landing, made before the send
            (30, R6At5x(3000, 2000, madeMs: 400)),   // made after the send; the body has not shown this pan yet
            (30, R6At5x(1000, 1000, madeMs: 520)));  // the landing
        await using var camera = new CanonCamera(transport, NullLogger<CanonCamera>.Instance, time);
        var ct = TestContext.Current.CancellationToken;

        (await camera.GetLiveViewFrameAndInfoAsync(ct)).Error.ShouldBe(EdsError.OK);
        time.Advance(TimeSpan.FromMilliseconds(270));

        var pan = camera.SetEvfZoomPositionAsync(1000, 1000, ct: ct);
        time.Pump(pan, step: TimeSpan.FromMilliseconds(10), budget: TimeSpan.FromSeconds(10));

        pan.IsCompleted.ShouldBeTrue("the pan was still waiting after 10 s of its own clock");
        var (error, landed) = await pan;
        error.ShouldBe(EdsError.OK);
        landed.ShouldNotBeNull();
        (landed.Value.X, landed.Value.Y).ShouldBe((1000u, 1000u));
        transport.Polls.ShouldBe(6);   // read on to the landing; judged on arrival it stopped at the third frame
    }
}
