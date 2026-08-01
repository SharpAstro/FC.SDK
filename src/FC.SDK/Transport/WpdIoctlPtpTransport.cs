using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using FC.SDK.Protocol;

namespace FC.SDK.Transport;

/// <summary>
/// Talks to the Windows MTP class driver through <c>DeviceIoControl</c> on a single long-lived
/// handle, bypassing the WPD COM API entirely.
/// </summary>
/// <remarks>
/// <para>
/// Same driver and same three command phases as <see cref="WpdPtpTransport"/>, reached one layer
/// lower — the property bags this serialises by hand are the ones
/// <c>PortableDeviceApi.dll!SendCommand</c> would have built. It is how EDSDK itself reaches an EOS
/// body on Windows; <c>docs/canon-windows-transports.md</c> and <c>docs/wpd-ioctl-wire-format.md</c>
/// record how that was established and how the format was decoded.
/// </para>
/// <para>
/// Why it exists, concretely: the COM transport has to create, open, close and release an entire
/// <c>IPortableDevice</c> for every live-view frame, because an unfinished transfer poisons a device
/// object while END_DATA_TRANSFER for GetViewFinderData never returns at all. Neither happens here —
/// the same end phase completes in single-digit milliseconds and one handle streams indefinitely.
/// </para>
/// <para>
/// What that is and is not worth, measured on a 450D over 300 frames each, polling flat out:
/// throughput is <em>identical</em> (20.3 fps against 20.2), because the body is the limiter — half
/// the polls answer ObjectNotReady on both. The difference is what it costs to get there: private
/// working set held 38–47 MiB here against 73–119 MiB through COM. An earlier note in this repo
/// claimed 11.4 fps against ~5; that comparison did not hold up once both paths were driven through
/// the same harness, and the memory figure is the honest headline.
/// </para>
/// <para>
/// A session commits to one transport or the other. Both open the device independently, and the
/// camera is a half-duplex peer with one PTP session — so this is an alternative to the COM
/// transport, never a supplement to it.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
internal sealed class WpdIoctlPtpTransport : IMtpExtTransport
{
    private const string ClientName = "FC.SDK";

    /// <summary>
    /// Enough for any response that is not carrying a data phase. Command responses are a handful of
    /// small properties; the driver reports what it actually wrote, so headroom costs nothing.
    /// </summary>
    private const int CommandResponseCapacity = 8 * 1024;

    /// <summary>Room for the property keys wrapped around a data payload in a READ_DATA response.</summary>
    private const int ResponseOverhead = 4 * 1024;

    /// <summary>
    /// What a viewfinder read asks for regardless of the size the driver declared, matching EDSDK's
    /// own 2 MiB request per frame on the same body. See <see cref="WpdPtpTransport"/> for why
    /// over-requesting is the right shape for a vendor read.
    /// </summary>
    private const int ViewfinderReadSize = 2 << 20;

    /// <summary>
    /// Ceiling on one READ_DATA call. Reads larger than this are split across several calls on the
    /// same transfer context rather than handed to the I/O manager as one buffered request.
    /// </summary>
    private const int MaxChunk = 2 << 20;

    private static readonly bool Trace = Environment.GetEnvironmentVariable("FCSDK_WPD_TRACE") is "1";

    private readonly string _deviceId;
    private readonly TimeProvider _time;
    private readonly CommandGate _gate;

    private WpdIoctlDevice? _device;

    /// <summary>
    /// The context the driver handed back from the client-information handshake, echoed on every
    /// later command.
    /// </summary>
    private string _clientContext = string.Empty;

    // One request and one response buffer for the whole session, grown on demand. The gate
    // guarantees a single command in flight, so there is never a second user of either. This is
    // where the per-frame churn goes: a live-view frame reuses a 2 MiB request buffer it built once.
    private byte[] _request = new byte[4096];
    private byte[] _response = new byte[CommandResponseCapacity];
    private int _requestLength;

    public bool IsConnected => _device is not null;

    public string DeviceId => _deviceId;

    /// <param name="timeProvider">Drives the command deadline; a test can pass a fake one.</param>
    internal WpdIoctlPtpTransport(string deviceId, TimeProvider? timeProvider = null)
    {
        _deviceId = deviceId;
        _time = timeProvider ?? TimeProvider.System;
        _gate = new CommandGate(_time, TimeSpan.FromSeconds(15));
    }

    /// <summary>Deadline for one command, counting the wait for the gate. See <see cref="CommandGate"/>.</summary>
    internal TimeSpan CommandTimeout
    {
        get => _gate.Timeout;
        set => _gate.Timeout = value;
    }

    public async Task ConnectAsync(CancellationToken ct = default)
    {
        var device = WpdIoctlDevice.Open(_deviceId);
        try
        {
            // The driver refuses MTP extension commands from a client it has not been introduced to,
            // so this is not optional bookkeeping — it is what IPortableDevice::Open does for you.
            Build(WpdCommands.SaveClientInformation(_request, ClientName));
            int length = await device
                .SendAsync(WpdIoctlDevice.MessageRead, _request, _requestLength, _response)
                .ConfigureAwait(false);

            _clientContext = ReadClientContext(length);
            _device = device;
        }
        catch
        {
            device.Dispose();
            throw;
        }
    }

    private string ReadClientContext(int responseLength)
    {
        var reader = new WpdBagReader(_response.AsSpan(0, responseLength));
        ThrowIfFailed(reader);

        // Captures show EDSDK sending a fresh GUID string on every command and the driver accepting
        // values it never issued, so this does not look validated — but echoing what we were given
        // is what the COM layer does, and a generated token is only the fallback.
        return reader.TryGetString(
                WpdInterop.WPD_COMMAND_COMMON, WpdInterop.PID_CLIENT_INFORMATION_CONTEXT, out string context)
            && context.Length > 0
                ? context
                : $"{{{Guid.NewGuid().ToString().ToUpperInvariant()}}}";
    }

    public ValueTask SendAsync(ReadOnlyMemory<byte> packet, CancellationToken ct = default) =>
        throw new NotSupportedException("WPD transports use typed command methods.");

    public ValueTask<int> ReceiveAsync(Memory<byte> buffer, CancellationToken ct = default) =>
        throw new NotSupportedException("WPD transports use typed command methods.");

    /// <summary>
    /// No interrupt endpoint here — the MTP driver owns it. Canon events arrive through GetEvent
    /// (0x9116) polling, exactly as they do over the COM transport.
    /// </summary>
    public ValueTask<int> ReceiveEventAsync(Memory<byte> buffer, CancellationToken ct = default) =>
        ValueTask.FromResult(0);

    private const ushort TimedOutCode = (ushort)PtpResponseCode.LocalTimeout;

    public Task<(ushort ResponseCode, uint[] ResponseParams)> ExecuteCommandAsync(
        ushort opCode, uint[] @params, CancellationToken ct = default) =>
        _gate.RunAsync(() => ExecuteNoDataAsync(opCode, @params), (TimedOutCode, Array.Empty<uint>()), ct);

    public Task<(ushort ResponseCode, uint[] ResponseParams, byte[] Data)> ExecuteCommandReadDataAsync(
        ushort opCode, uint[] @params, CancellationToken ct = default) =>
        _gate.RunAsync(() => ExecuteReadDataAsync(opCode, @params),
            (TimedOutCode, Array.Empty<uint>(), Array.Empty<byte>()), ct);

    public Task<(ushort ResponseCode, uint[] ResponseParams)> ExecuteCommandWriteDataAsync(
        ushort opCode, uint[] @params, byte[] data, CancellationToken ct = default) =>
        _gate.RunAsync(() => ExecuteWriteDataAsync(opCode, @params, data), (TimedOutCode, Array.Empty<uint>()), ct);

    private async Task<(ushort, uint[])> ExecuteNoDataAsync(ushort opCode, uint[] @params)
    {
        Build(WpdCommands.Execute(_request, _clientContext, WpdInterop.PID_EXECUTE_NO_DATA, opCode, @params));
        int length = await ExchangeAsync(CommandResponseCapacity).ConfigureAwait(false);
        return ReadResponse(length);
    }

    private async Task<(ushort, uint[], byte[])> ExecuteReadDataAsync(ushort opCode, uint[] @params)
    {
        // The viewfinder is the one operation whose payload size is not worth believing in advance,
        // and the one the whole transport exists for. It gets EDSDK's shape: over-request a fixed
        // 2 MiB, take whatever comes back, end the transfer, keep the handle.
        bool viewfinder = opCode == (ushort)PtpOperationCode.CanonGetViewfinderData;
        long started = _time.GetTimestamp();

        Build(WpdCommands.Execute(_request, _clientContext, WpdInterop.PID_EXECUTE_DATA_READ, opCode, @params));
        int length = await ExchangeAsync(CommandResponseCapacity).ConfigureAwait(false);

        var (responseCode, responseParams, context, totalSize) = ReadInitiateResult(length);

        // No transfer context means the driver answered the command outright — nothing to read and
        // nothing to end.
        if (context.Length == 0) return (responseCode, responseParams, []);

        byte[] data = [];
        try
        {
            if (viewfinder)
            {
                data = await ReadOneChunkAsync(context, Math.Max((int)totalSize + 1, ViewfinderReadSize))
                    .ConfigureAwait(false);
            }
            else if (totalSize > 0)
            {
                data = await ReadAllAsync(context, totalSize).ConfigureAwait(false);
            }
        }
        catch
        {
            // Ending a transfer we failed mid-way is what keeps the next command from inheriting it.
            try { await EndTransferAsync(context).ConfigureAwait(false); } catch { /* best effort */ }
            throw;
        }

        var end = await EndTransferAsync(context).ConfigureAwait(false);

        if (Trace && viewfinder && ++_frameCount % TraceEveryFrames == 0)
        {
            Console.Error.WriteLine(
                $"[wpd-ioctl] 0x9153 frame {_frameCount}: {data.Length} bytes in " +
                $"{_time.GetElapsedTime(started).TotalMilliseconds:F0} ms, " +
                $"managed {GC.GetTotalMemory(false) >> 20} MiB, private {Environment.WorkingSet >> 20} MiB");
        }

        return (end.ResponseCode, end.ResponseParams, data);
    }

    private int _frameCount;

    /// <summary>Frames between trace lines — one per frame at 11 fps is unreadable.</summary>
    private const int TraceEveryFrames = 25;

    private async Task<(ushort, uint[])> ExecuteWriteDataAsync(ushort opCode, uint[] @params, byte[] data)
    {
        Build(WpdCommands.Execute(
            _request, _clientContext, WpdInterop.PID_EXECUTE_DATA_WRITE, opCode, @params, data.Length));
        int length = await ExchangeAsync(CommandResponseCapacity).ConfigureAwait(false);

        var (responseCode, responseParams, context, _) = ReadInitiateResult(length);
        if (context.Length == 0) return (responseCode, responseParams);

        try
        {
            Build(WpdCommands.WriteData(_request, _clientContext, context, data));
            int written = await ExchangeAsync(CommandResponseCapacity).ConfigureAwait(false);
            ThrowIfFailed(written);
        }
        catch
        {
            try { await EndTransferAsync(context).ConfigureAwait(false); } catch { /* best effort */ }
            throw;
        }

        return await EndTransferAsync(context).ConfigureAwait(false);
    }

    private async Task<(ushort ResponseCode, uint[] ResponseParams)> EndTransferAsync(string transferContext)
    {
        Build(WpdCommands.EndDataTransfer(_request, _clientContext, transferContext));
        int length = await ExchangeAsync(CommandResponseCapacity).ConfigureAwait(false);
        return ReadResponse(length);
    }

    /// <summary>Reads a payload of known size, splitting it across READ_DATA calls if need be.</summary>
    /// <remarks>
    /// Every read this transport has been exercised with fits in one chunk. The loop is here because
    /// a full-resolution GetObject would not, and because a short read is the driver's own way of
    /// saying the data phase is exhausted — so trusting the declared size alone would be wrong.
    /// </remarks>
    private async Task<byte[]> ReadAllAsync(string transferContext, uint totalSize)
    {
        var data = new byte[totalSize];
        int offset = 0;

        while (offset < data.Length)
        {
            int want = Math.Min(data.Length - offset, MaxChunk);
            Build(WpdCommands.ReadData(_request, _clientContext, transferContext, want));
            int length = await ExchangeAsync(want + ResponseOverhead).ConfigureAwait(false);

            int read = CopyPayload(length, data.AsSpan(offset));
            if (read == 0) return offset == data.Length ? data : data[..offset];
            offset += read;
        }

        return data;
    }

    /// <summary>
    /// Reads one chunk, over-requesting on purpose and returning exactly what arrived. The driver's
    /// length is the truth — a short read means the data phase is done, not that something failed.
    /// </summary>
    private async Task<byte[]> ReadOneChunkAsync(string transferContext, int want)
    {
        Build(WpdCommands.ReadData(_request, _clientContext, transferContext, want));
        int length = await ExchangeAsync(want + ResponseOverhead).ConfigureAwait(false);
        return TakePayload(length);
    }

    // --- buffer plumbing ---
    //
    // Deliberately in non-async methods: WpdBagReader is a ref struct and cannot live across an
    // await, and keeping the decode separate is what lets the async paths stay readable.

    private void Build(WpdBagWriter writer)
    {
        _request = writer.Buffer;
        _requestLength = writer.Length;
    }

    private Task<int> ExchangeAsync(int responseCapacity)
    {
        if (_response.Length < responseCapacity) _response = new byte[responseCapacity];

        var device = _device ?? throw new InvalidOperationException("Transport not connected.");
        return device.SendAsync(WpdIoctlDevice.MessageReadWrite, _request, _requestLength, _response);
    }

    private (ushort ResponseCode, uint[] ResponseParams) ReadResponse(int responseLength)
    {
        var reader = new WpdBagReader(_response.AsSpan(0, responseLength));
        ThrowIfFailed(reader);

        reader.TryGetUInt32(WpdInterop.WPD_COMMAND_MTP_EXT, WpdInterop.PID_RESPONSE_CODE, out uint code);
        return ((ushort)code,
            reader.GetUInt32Collection(WpdInterop.WPD_COMMAND_MTP_EXT, WpdInterop.PID_RESPONSE_PARAMS));
    }

    private (ushort ResponseCode, uint[] ResponseParams, string Context, uint TotalSize) ReadInitiateResult(
        int responseLength)
    {
        var reader = new WpdBagReader(_response.AsSpan(0, responseLength));
        ThrowIfFailed(reader);

        reader.TryGetString(WpdInterop.WPD_COMMAND_MTP_EXT, WpdInterop.PID_TRANSFER_CONTEXT, out string context);
        reader.TryGetUInt32(WpdInterop.WPD_COMMAND_MTP_EXT, WpdInterop.PID_TRANSFER_TOTAL_SIZE, out uint totalSize);
        reader.TryGetUInt32(WpdInterop.WPD_COMMAND_MTP_EXT, WpdInterop.PID_RESPONSE_CODE, out uint code);

        // A command the driver handled without opening a transfer reports no code of its own; call
        // that OK rather than inventing a failure, matching the COM transport.
        ushort responseCode = code == 0 ? (ushort)PtpResponseCode.OK : (ushort)code;
        return (responseCode,
            reader.GetUInt32Collection(WpdInterop.WPD_COMMAND_MTP_EXT, WpdInterop.PID_RESPONSE_PARAMS),
            context, totalSize);
    }

    private int CopyPayload(int responseLength, Span<byte> destination)
    {
        var reader = new WpdBagReader(_response.AsSpan(0, responseLength));
        ThrowIfFailed(reader);

        if (!reader.TryGetBytes(WpdInterop.WPD_COMMAND_MTP_EXT, WpdInterop.PID_TRANSFER_DATA, out var payload))
            return 0;

        // Clamped, not trusted: the declared total is the camera's claim and the payload length is
        // the driver's, and a mismatch must not become a buffer overrun.
        if (payload.Length > destination.Length) payload = payload[..destination.Length];
        payload.CopyTo(destination);
        return payload.Length;
    }

    private byte[] TakePayload(int responseLength)
    {
        var reader = new WpdBagReader(_response.AsSpan(0, responseLength));
        ThrowIfFailed(reader);

        return reader.TryGetBytes(WpdInterop.WPD_COMMAND_MTP_EXT, WpdInterop.PID_TRANSFER_DATA, out var payload)
            ? payload.ToArray()
            : [];
    }

    private void ThrowIfFailed(int responseLength) =>
        ThrowIfFailed(new WpdBagReader(_response.AsSpan(0, responseLength)));

    /// <summary>
    /// Surfaces a driver-level failure as <see cref="COMException"/> — the same type the COM
    /// transport raises, so <c>PtpSession</c> maps both to a general error without caring which
    /// road the command took.
    /// </summary>
    private static void ThrowIfFailed(WpdBagReader reader)
    {
        if (reader.TryGetHResult(WpdInterop.WPD_COMMAND_COMMON, WpdInterop.PID_HRESULT, out int hr) && hr < 0)
            throw new COMException($"WPD ioctl command failed with HRESULT 0x{hr:X8}", hr);
    }

    public async ValueTask DisposeAsync()
    {
        // Closed through the gate so an in-flight command finishes first, and exchanged first so a
        // concurrent second dispose finds nothing left to close. A wedged command times out and the
        // close proceeds anyway: failing that command is the point of tearing down.
        if (Interlocked.Exchange(ref _device, null) is not { } device) return;

        bool closed = await _gate.RunAsync(() => { device.Dispose(); return true; }, onTimeout: false)
            .ConfigureAwait(false);

        if (!closed) device.Dispose();
    }
}
