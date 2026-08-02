using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using System.Runtime.Versioning;

namespace FC.SDK.Transport;

[SupportedOSPlatform("windows")]
internal sealed partial class WpdPtpTransport : IMtpExtTransport
{
    private readonly string _deviceId;
    private IWpdDevice? _device;

    /// <summary>One command at a time, each bounded by a deadline. See <see cref="CommandTimeout"/>.</summary>
    private readonly Protocol.CommandGate _gate;

    public bool IsConnected => _device is not null;

    public string DeviceId => _deviceId;

    /// <param name="timeProvider">
    /// Drives the command deadline. Defaults to <see cref="TimeProvider.System"/>; a test can pass a
    /// fake one to expire a command without waiting.
    /// </param>
    internal WpdPtpTransport(string deviceId, TimeProvider? timeProvider = null)
    {
        _deviceId = deviceId;
        _time = timeProvider ?? TimeProvider.System;
        _gate = new Protocol.CommandGate(_time, TimeSpan.FromSeconds(15));
    }

    private readonly TimeProvider _time;

    /// <summary>Shorthand for <see cref="Protocol.CommandGate.Offload{T}"/> — one blocking call, off-thread.</summary>
    private static Task<T> Offload<T>(Func<T> syncCall) => Protocol.CommandGate.Offload(syncCall);

    public Task ConnectAsync(CancellationToken ct = default)
    {
        // Idempotent for the same reason the ioctl transport's is: a caller may connect a transport
        // before handing it to CanonCamera, whose OpenSessionAsync then connects it again.
        if (_device is not null) return Task.CompletedTask;

        _device = WpdInterop.CreateInstance<IWpdDevice>(WpdInterop.CLSID_PortableDevice);
        Marshal.ThrowExceptionForHR(_device.Open(_deviceId, CreateClientInfo()));
        return Task.CompletedTask;
    }

    private static IWpdValues CreateClientInfo()
    {
        var clientInfo = WpdInterop.CreateInstance<IWpdValues>(WpdInterop.CLSID_PortableDeviceValues);
        var key = new PropertyKey { fmtid = WpdInterop.WPD_CLIENT_INFO, pid = 2 }; // WPD_CLIENT_NAME
        clientInfo.SetStringValue(in key, "FC.SDK");
        key.pid = 3; // WPD_CLIENT_MAJOR_VERSION
        clientInfo.SetUnsignedIntegerValue(in key, 1);
        key.pid = 4; // WPD_CLIENT_MINOR_VERSION
        clientInfo.SetUnsignedIntegerValue(in key, 1);
        key.pid = 5; // WPD_CLIENT_REVISION
        clientInfo.SetUnsignedIntegerValue(in key, 0);
        return clientInfo;
    }

    public ValueTask SendAsync(ReadOnlyMemory<byte> packet, CancellationToken ct = default) =>
        throw new NotSupportedException("WPD transport uses typed command methods.");

    public ValueTask<int> ReceiveAsync(Memory<byte> buffer, CancellationToken ct = default) =>
        throw new NotSupportedException("WPD transport uses typed command methods.");

    public ValueTask<int> ReceiveEventAsync(Memory<byte> buffer, CancellationToken ct = default) =>
        ValueTask.FromResult(0);

    /// <summary>
    /// Deadline for a single MTP extension command. The USB transport gets this from LibUsbDotNet
    /// (5 s on the bulk endpoints) and PTP/IP from its socket timeouts; WPD had none, so a camera
    /// that accepted a request and never answered — or an unplugged cable — hung the caller forever.
    /// </summary>
    /// <remarks>
    /// Generous by default: a full-resolution GetObject over USB 2.0 legitimately takes seconds.
    /// </remarks>
    internal TimeSpan CommandTimeout
    {
        get => _gate.Timeout;
        set => _gate.Timeout = value;
    }

    private const ushort TimedOutCode = (ushort)Protocol.PtpResponseCode.LocalTimeout;

    // All three phases go through the same gate: one command at a time, each with a deadline. The
    // mechanics (thread hop, who releases the lock, bounding the wait for the lock itself) live in
    // CommandGate so they can be tested against a FakeTimeProvider without COM or a camera.
    public Task<(ushort ResponseCode, uint[] ResponseParams)> ExecuteCommandAsync(
        ushort opCode, uint[] @params, CancellationToken ct = default) =>
        _gate.RunAsync(() => ExecuteNoData(opCode, @params), (TimedOutCode, Array.Empty<uint>()), ct);

    // The read path is a SEQUENCE of blocking calls (initiate → read → end), so it takes the
    // async gate overload: each phase is awaited off-thread individually, which is what lets the
    // end phase carry its own deadline without anybody blocking on a Wait.
    //
    // Viewfinder reads take a different road entirely (one blocking sequence on an ephemeral device
    // object — see ExecuteViewfinderRead), but the SAME gate: PTP is half-duplex, and the driver
    // below both device objects talks to one camera.
    public Task<(ushort ResponseCode, uint[] ResponseParams, byte[] Data)> ExecuteCommandReadDataAsync(
        ushort opCode, uint[] @params, CancellationToken ct = default) =>
        opCode == (ushort)Protocol.PtpOperationCode.CanonGetViewfinderData
            ? _gate.RunAsync(() => ExecuteViewfinderRead(@params),
                (TimedOutCode, Array.Empty<uint>(), Array.Empty<byte>()), ct)
            : _gate.RunAsync(() => ExecuteReadDataAsync(opCode, @params),
                (TimedOutCode, Array.Empty<uint>(), Array.Empty<byte>()), ct);

    public Task<(ushort ResponseCode, uint[] ResponseParams)> ExecuteCommandWriteDataAsync(
        ushort opCode, uint[] @params, byte[] data, CancellationToken ct = default) =>
        _gate.RunAsync(() => ExecuteWriteData(opCode, @params, data), (TimedOutCode, Array.Empty<uint>()), ct);

    private (ushort, uint[]) ExecuteNoData(ushort opCode, uint[] @params)
    {
        var cmd = CreateValues();
        SetCommandKey(cmd, WpdInterop.PID_EXECUTE_NO_DATA);
        cmd.SetUnsignedIntegerValue(WpdInterop.MtpExtKey(WpdInterop.PID_OPERATION_CODE), opCode);
        SetOperationParams(cmd, @params);

        var results = SendWpdCommand(cmd);
        return ExtractResponse(results);
    }

    /// <summary>
    /// Per-phase tracing of the three-phase data-read to stderr (`FCSDK_WPD_TRACE=1`). The deadline
    /// wraps the whole read, so when a command wedges only this can say WHICH phase never returned —
    /// initiate (camera ignored the operation) vs read (driver blocked mid-data-phase).
    /// </summary>
    private static readonly bool TraceReads = Environment.GetEnvironmentVariable("FCSDK_WPD_TRACE") is "1";

    /// <summary>
    /// The floor for a viewfinder read request, regardless of the size the driver declared. Matches
    /// the 2 MiB EDSDK requests per frame on the same body.
    /// </summary>
    /// <remarks>
    /// Over-requesting also PROVED the data phase is exhausted rather than merely believed it: asking
    /// for 2 MiB returns exactly TRANSFER_TOTAL_SIZE (~266 KB on a 450D), every frame. TOTAL_SIZE is
    /// exact even for a vendor read, and re-reading the same context — immediately or up to 500 ms
    /// later — returns 0 bytes, so one initiate yields exactly one frame.
    /// </remarks>
    private const uint ViewfinderReadSize = 2u << 20;

    /// <summary>
    /// Pulls one viewfinder frame through a device object that exists only for this frame.
    /// </summary>
    /// <remarks>
    /// <para>
    /// GetViewFinderData (0x9153) has no response phase on this body: END_DATA_TRANSFER waits forever
    /// for a response container that never comes (20 pending phases, 80 s, none settled), and
    /// <c>IPortableDevice::Cancel</c> does not release a parked <c>SendCommand</c>. Skipping END
    /// instead leaves the transfer "unfinished", after which the device object errors EVERY later
    /// command — even CloseSession — until it is reopened. So on a long-lived object the choice per
    /// frame is a spent thread or a poisoned object.
    /// </para>
    /// <para>
    /// Hence the ephemeral object: open, initiate, read short, close, discard. Closing releases the
    /// un-ended transfer without a single END, so nothing is spent and nothing durable is poisoned.
    /// The main device object never sees 0x9153 and stays clean for events and capture; WPD supports
    /// concurrent clients on one device by design, and the shared gate keeps the camera half-duplex.
    /// EDSDK avoids the whole problem by speaking to the driver below this bookkeeping (raw
    /// <c>IOCTL_WPD_MESSAGE_*</c>, no END, one long-lived handle) — this is the closest documented-API
    /// equivalent, priced at one CoCreateInstance+Open per frame.
    /// </para>
    /// <para>
    /// Per frame is not laziness: a dedicated object opened once and REUSED was measured, and the
    /// poison is per-object — it took one frame, then failed the next, giving 204 opens for 200 frames
    /// plus an error for every second attempt. Leaving that object open past live view also broke the
    /// next capture. Open+close is a couple of milliseconds of a 26–34 ms frame.
    /// </para>
    /// </remarks>
    private (ushort, uint[], byte[]) ExecuteViewfinderRead(uint[] @params)
    {
        var trace = TraceReads;
        var started = _time.GetTimestamp();

        IWpdValues? cmd = null, results = null;
        var device = OpenViewfinderDevice();
        try
        {
            cmd = CreateValues();
            SetCommandKey(cmd, WpdInterop.PID_EXECUTE_DATA_READ);
            cmd.SetUnsignedIntegerValue(
                WpdInterop.MtpExtKey(WpdInterop.PID_OPERATION_CODE),
                (ushort)Protocol.PtpOperationCode.CanonGetViewfinderData);
            SetOperationParams(cmd, @params);

            results = SendWpdCommandOn(device, cmd);
            CheckHResult(results);

            int ctxHr = results.GetStringValue(WpdInterop.MtpExtKey(WpdInterop.PID_TRANSFER_CONTEXT), out string context);
            results.GetUnsignedIntegerValue(WpdInterop.MtpExtKey(WpdInterop.PID_TRANSFER_TOTAL_SIZE), out uint totalSize);

            if (ctxHr < 0 || string.IsNullOrEmpty(context))
            {
                results.GetUnsignedIntegerValue(WpdInterop.MtpExtKey(WpdInterop.PID_RESPONSE_CODE), out uint respCode);
                return (respCode == 0 ? (ushort)0x2002 : (ushort)respCode, [], []);
            }

            // Zero bytes is the body settling into live view (~1 s worth after a cold start), not an
            // error. No END here either — closing the object below releases the context.
            if (totalSize == 0) return ((ushort)Protocol.PtpResponseCode.OK, [], []);

            // Strictly more than declared, so the read is short and the driver knows we are done.
            var data = ReadChunk(device, context, Math.Max(totalSize + 1, ViewfinderReadSize));

            if (trace && ++_frameTraceCounter % TraceEveryFrames == 0) Console.Error.WriteLine(
                $"[wpd] 0x9153 frame {_frameTraceCounter}: {data.Length} bytes in " +
                $"{_time.GetElapsedTime(started).TotalMilliseconds:F0} ms, " +
                $"managed {GC.GetTotalMemory(false) >> 20} MiB, private {Environment.WorkingSet >> 20} MiB");
            return ((ushort)Protocol.PtpResponseCode.OK, [], data);
        }
        finally
        {
            // Deterministic, in reverse order of creation. These wrappers are otherwise reclaimed only
            // when the GC runs their finalizers, and the GC cannot feel how much driver-side state each
            // one pins — three per frame at ~5 fps outruns it badly.
            try { device.Close(); } catch { /* releasing a spent object must never throw */ }
            Release(results);
            Release(cmd);
            Release(device);
        }
    }

    private int _frameTraceCounter;

    /// <summary>Frames between trace lines — one line per frame at ~5 fps is unreadable.</summary>
    private const int TraceEveryFrames = 25;

    private IWpdDevice OpenViewfinderDevice()
    {
        var device = WpdInterop.CreateInstance<IWpdDevice>(WpdInterop.CLSID_PortableDevice);
        var clientInfo = CreateClientInfo();
        try
        {
            Marshal.ThrowExceptionForHR(device.Open(_deviceId, clientInfo));
            return device;
        }
        catch
        {
            Release(device);
            throw;
        }
        finally
        {
            Release(clientInfo);
        }
    }

    /// <summary>Releases a COM wrapper's native reference now instead of at finalization.</summary>
    private static void Release(object? com)
    {
        if (com is ComObject o) o.FinalRelease();
    }

    /// <summary>
    /// Reads up to <paramref name="want"/> bytes of an open data phase.
    /// </summary>
    /// <remarks>
    /// <para>
    /// TRANSFER_DATA is listed as a <em>result</em> of READ_DATA, but it is really an in/out: the
    /// caller allocates the landing buffer and the driver fills it. Sending only the context and the
    /// byte count — as the documented input list suggests — makes even a 341-byte GetDeviceInfo read
    /// fail, so this is required, not redundant.
    /// </para>
    /// <para>
    /// That also explains EDSDK's ioctl trace, where every live-view frame carries a 2 MiB input
    /// buffer: same pre-allocated buffer, one layer down. It is not asking for 2 MiB because it knows
    /// the frame size — it over-requests and accepts a short read, which is why
    /// <see cref="ExtractBuffer"/> trusts the driver's length rather than <paramref name="want"/>.
    /// </para>
    /// </remarks>
    /// <summary>
    /// Reused landing buffer for reads up to <see cref="ViewfinderReadSize"/>. Its contents never
    /// survive the call (the driver writes it, <see cref="ExtractBuffer"/> copies out of the results
    /// bag), only the allocation does — and allocating 2 MiB per frame at ~5 fps was pure churn.
    /// Serialized by the gate, so never used concurrently.
    /// </summary>
    private byte[] _landing = [];

    private byte[] ReadChunk(IWpdDevice device, string context, uint want)
    {
        // Bigger reads (a CR2 download) get a one-off buffer rather than permanently growing the
        // cached one to object size.
        var landing = want > ViewfinderReadSize
            ? new byte[want]
            : _landing.Length >= want ? _landing : _landing = new byte[want];

        IWpdValues? cmd = null, results = null;
        try
        {
            cmd = CreateValues();
            SetCommandKey(cmd, WpdInterop.PID_READ_DATA);
            cmd.SetStringValue(WpdInterop.MtpExtKey(WpdInterop.PID_TRANSFER_CONTEXT), context);
            cmd.SetUnsignedIntegerValue(WpdInterop.MtpExtKey(WpdInterop.PID_TRANSFER_NUM_BYTES_TO_READ), want);
            cmd.SetBufferValue(WpdInterop.MtpExtKey(WpdInterop.PID_TRANSFER_DATA), landing, want);

            results = SendWpdCommandOn(device, cmd);
            CheckHResult(results);
            return ExtractBuffer(results);
        }
        finally
        {
            Release(results);
            Release(cmd);
        }
    }

    private async Task<(ushort, uint[], byte[])> ExecuteReadDataAsync(ushort opCode, uint[] @params)
    {
        // GetEvent polls every 200 ms; tracing it would drown the interesting commands.
        var trace = TraceReads && opCode != (ushort)Protocol.PtpOperationCode.CanonGetEvent;

        // Step 1: Initiate read
        var cmd = CreateValues();
        SetCommandKey(cmd, WpdInterop.PID_EXECUTE_DATA_READ);
        cmd.SetUnsignedIntegerValue(WpdInterop.MtpExtKey(WpdInterop.PID_OPERATION_CODE), opCode);
        SetOperationParams(cmd, @params);

        if (trace) Console.Error.WriteLine($"[wpd] 0x{opCode:X4} initiate…");
        var results = await Offload(() => SendWpdCommand(cmd)).ConfigureAwait(false);
        CheckHResult(results);

        // Get transfer context — if absent, WPD handled the command internally with no data
        int ctxHr = results.GetStringValue(WpdInterop.MtpExtKey(WpdInterop.PID_TRANSFER_CONTEXT), out string context);
        results.GetUnsignedIntegerValue(WpdInterop.MtpExtKey(WpdInterop.PID_TRANSFER_TOTAL_SIZE), out uint totalSize);
        if (trace) Console.Error.WriteLine($"[wpd] 0x{opCode:X4} initiated: ctxHr=0x{ctxHr:X8} totalSize={totalSize}");

        if (ctxHr < 0 || string.IsNullOrEmpty(context))
        {
            // No transfer context — WPD rejected or handled internally
            results.GetUnsignedIntegerValue(WpdInterop.MtpExtKey(WpdInterop.PID_RESPONSE_CODE), out uint respCode);
            return (respCode == 0 ? (ushort)0x2002 : (ushort)respCode, [], []);
        }

        if (totalSize == 0)
        {
            // Transfer context allocated but zero data — must end transfer to avoid jamming the pipe
            var endResp = await Offload(() => EndTransfer(context)).ConfigureAwait(false);
            return (endResp.ResponseCode, endResp.ResponseParams, []);
        }

        // Step 2: Read data — always end transfer even on failure to avoid jamming the pipe.
        // (Viewfinder reads never come through here — they take ExecuteViewfinderRead, where both the
        // exact-size read and the end phase below would be wrong.)
        try
        {
            if (trace) Console.Error.WriteLine($"[wpd] 0x{opCode:X4} reading {totalSize} bytes…");
            byte[] data = await Offload(() => ReadChunk(_device!, context, totalSize)).ConfigureAwait(false);

            // Step 3: End transfer
            if (trace) Console.Error.WriteLine($"[wpd] 0x{opCode:X4} read {data.Length} bytes, ending transfer…");
            var endResponse = await Offload(() => EndTransfer(context)).ConfigureAwait(false);
            if (trace) Console.Error.WriteLine($"[wpd] 0x{opCode:X4} done, response 0x{endResponse.ResponseCode:X4}");
            return (endResponse.ResponseCode, endResponse.ResponseParams, data);
        }
        catch
        {
            try { await Offload(() => EndTransfer(context)).ConfigureAwait(false); } catch { /* best effort */ }
            throw;
        }
    }

    private (ushort, uint[]) ExecuteWriteData(ushort opCode, uint[] @params, byte[] data)
    {
        // Step 1: Initiate write
        var cmd = CreateValues();
        SetCommandKey(cmd, WpdInterop.PID_EXECUTE_DATA_WRITE);
        cmd.SetUnsignedIntegerValue(WpdInterop.MtpExtKey(WpdInterop.PID_OPERATION_CODE), opCode);
        SetOperationParams(cmd, @params);
        cmd.SetUnsignedLargeIntegerValue(WpdInterop.MtpExtKey(WpdInterop.PID_TRANSFER_TOTAL_SIZE), (ulong)data.Length);

        var results = SendWpdCommand(cmd);
        CheckHResult(results);
        results.GetStringValue(WpdInterop.MtpExtKey(WpdInterop.PID_TRANSFER_CONTEXT), out string context);

        // Step 2: Write data — always end transfer even on failure
        try
        {
            var writeCmd = CreateValues();
            SetCommandKey(writeCmd, WpdInterop.PID_WRITE_DATA);
            writeCmd.SetStringValue(WpdInterop.MtpExtKey(WpdInterop.PID_TRANSFER_CONTEXT), context);
            writeCmd.SetUnsignedIntegerValue(WpdInterop.MtpExtKey(WpdInterop.PID_TRANSFER_NUM_BYTES_TO_WRITE), (uint)data.Length);
            writeCmd.SetBufferValue(WpdInterop.MtpExtKey(WpdInterop.PID_TRANSFER_DATA), data, (uint)data.Length);

            var writeResults = SendWpdCommand(writeCmd);
            CheckHResult(writeResults);

            // Step 3: End transfer
            return EndTransfer(context);
        }
        catch
        {
            try { EndTransfer(context); } catch { /* best effort */ }
            throw;
        }
    }

    private (ushort ResponseCode, uint[] ResponseParams) EndTransfer(string context)
    {
        var endCmd = CreateValues();
        SetCommandKey(endCmd, WpdInterop.PID_END_DATA_TRANSFER);
        endCmd.SetStringValue(WpdInterop.MtpExtKey(WpdInterop.PID_TRANSFER_CONTEXT), context);

        var endResults = SendWpdCommand(endCmd);
        return ExtractResponse(endResults);
    }

    private static IWpdValues CreateValues() =>
        WpdInterop.CreateInstance<IWpdValues>(WpdInterop.CLSID_PortableDeviceValues);

    private static void SetCommandKey(IWpdValues values, uint commandPid)
    {
        values.SetGuidValue(WpdInterop.CommonKey(WpdInterop.PID_COMMAND_CATEGORY), WpdInterop.WPD_COMMAND_MTP_EXT);
        values.SetUnsignedIntegerValue(WpdInterop.CommonKey(WpdInterop.PID_COMMAND_ID), commandPid);
    }

    private static void SetOperationParams(IWpdValues cmd, uint[] @params)
    {
        // The WPD MTP driver requires the params collection to ALWAYS be present,
        // even when empty. Without it, vendor data-READ commands fail with 0x80070490.
        var col = WpdInterop.CreateInstance<IWpdPropVariantCollection>(WpdInterop.CLSID_PortableDevicePropVariantCollection);
        foreach (uint p in @params)
        {
            var pv = PropVariant.FromUInt32(p);
            col.Add(in pv);
        }
        cmd.SetIPortableDevicePropVariantCollectionValue(WpdInterop.MtpExtKey(WpdInterop.PID_OPERATION_PARAMS), col);
    }

    private IWpdValues SendWpdCommand(IWpdValues cmd)
    {
        if (_device is null) throw new InvalidOperationException("Transport not connected.");
        return SendWpdCommandOn(_device, cmd);
    }

    private static IWpdValues SendWpdCommandOn(IWpdDevice device, IWpdValues cmd)
    {
        int hr = device.SendCommand(0, cmd, out var results);

        Marshal.ThrowExceptionForHR(hr);
        return results!;
    }

    /// <summary>Sends a raw WPD MTP EXT command and dumps the result properties.</summary>
    internal string TestMtpExtCommand(uint commandPid)
    {
        var cmd = CreateValues();
        cmd.SetGuidValue(WpdInterop.CommonKey(WpdInterop.PID_COMMAND_CATEGORY), WpdInterop.WPD_COMMAND_MTP_EXT);
        cmd.SetUnsignedIntegerValue(WpdInterop.CommonKey(WpdInterop.PID_COMMAND_ID), commandPid);

        var results = SendWpdCommand(cmd);
        results.GetCount(out uint propCount);

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"props={propCount}");

        results.GetErrorValue(WpdInterop.CommonKey(WpdInterop.PID_HRESULT), out int hresult);
        sb.AppendLine($"  HRESULT=0x{hresult:X8}");

        // Try to read vendor opcodes (returned as IPortableDevicePropVariantCollection)
        int hr = results.GetIPortableDevicePropVariantCollectionValue(
            WpdInterop.MtpExtKey(1001), out var opcodes);
        if (hr >= 0 && opcodes is not null)
        {
            opcodes.GetCount(out uint opCount);
            sb.AppendLine($"  Vendor opcodes: {opCount}");
            for (uint i = 0; i < Math.Min(opCount, 50); i++)
            {
                opcodes.GetAt(i, out PropVariant pv);
                sb.AppendLine($"    0x{pv.AsUInt32:X4}");
            }
        }

        // Try to read string (vendor extension description)
        hr = results.GetStringValue(WpdInterop.MtpExtKey(1001), out var desc);
        if (hr >= 0 && desc is not null)
        {
            sb.AppendLine($"  Description: {desc}");
        }

        return sb.ToString().TrimEnd();
    }

    private static void CheckHResult(IWpdValues results)
    {
        int hr = results.GetErrorValue(WpdInterop.CommonKey(WpdInterop.PID_HRESULT), out int errorValue);
        if (hr >= 0 && errorValue < 0)
            throw new COMException($"WPD command failed with HRESULT 0x{errorValue:X8}", errorValue);
    }

    private static (ushort ResponseCode, uint[] Params) ExtractResponse(IWpdValues results)
    {
        results.GetUnsignedIntegerValue(WpdInterop.MtpExtKey(WpdInterop.PID_RESPONSE_CODE), out uint respCode);

        uint[] respParams = [];
        int hr = results.GetIPortableDevicePropVariantCollectionValue(
            WpdInterop.MtpExtKey(WpdInterop.PID_RESPONSE_PARAMS), out var col);

        if (hr >= 0 && col is not null)
        {
            col.GetCount(out uint count);
            respParams = new uint[count];
            for (uint i = 0; i < count; i++)
            {
                col.GetAt(i, out PropVariant pv);
                respParams[i] = pv.AsUInt32;
            }
        }

        return ((ushort)respCode, respParams);
    }

    /// <summary>
    /// Takes the data buffer the driver allocated for a READ_DATA result. The length comes from the
    /// driver, not from what we asked for — a short read is legal and says the phase is exhausted.
    /// </summary>
    private static byte[] ExtractBuffer(IWpdValues results)
    {
        int hr = results.GetBufferValue(WpdInterop.MtpExtKey(WpdInterop.PID_TRANSFER_DATA), out nint ptr, out uint readSize);
        Marshal.ThrowExceptionForHR(hr);
        try
        {
            byte[] data = new byte[readSize];
            Marshal.Copy(ptr, data, 0, (int)readSize);
            return data;
        }
        finally
        {
            Marshal.FreeCoTaskMem(ptr);
        }
    }

    public static IEnumerable<string> EnumerateDeviceIds()
    {
        IWpdDeviceManager manager;
        try { manager = WpdInterop.CreateInstance<IWpdDeviceManager>(WpdInterop.CLSID_PortableDeviceManager); }
        catch { yield break; }

        manager.RefreshDeviceList();

        uint count = 0;
        manager.GetDevices(0, ref count);
        if (count == 0) yield break;

        // Manual marshalling: WPD fills an array of LPWSTR pointers
        var ptrs = new nint[(int)count];
        var pinned = GCHandle.Alloc(ptrs, GCHandleType.Pinned);
        try
        {
            manager.GetDevices(pinned.AddrOfPinnedObject(), ref count);
        }
        finally
        {
            pinned.Free();
        }

        for (var i = 0; i < (int)count; i++)
        {
            if (ptrs[i] == 0) continue;
            var id = Marshal.PtrToStringUni(ptrs[i]);
            Marshal.FreeCoTaskMem(ptrs[i]);
            if (!string.IsNullOrWhiteSpace(id))
                yield return id;
        }
    }

    public static string? GetDeviceFriendlyName(string deviceId)
    {
        IWpdDeviceManager manager;
        try { manager = WpdInterop.CreateInstance<IWpdDeviceManager>(WpdInterop.CLSID_PortableDeviceManager); }
        catch { return null; }

        uint nameLen = 0;
        int hr = manager.GetDeviceFriendlyName(deviceId, 0, ref nameLen);
        if (hr < 0 || nameLen == 0) return null;

        nint buf = Marshal.AllocCoTaskMem((int)(nameLen * 2));
        try
        {
            hr = manager.GetDeviceFriendlyName(deviceId, buf, ref nameLen);
            return hr >= 0 ? Marshal.PtrToStringUni(buf) : null;
        }
        finally
        {
            Marshal.FreeCoTaskMem(buf);
        }
    }

    // --- WPD Content API (for operations that MTP EXT data-phase doesn't support) ---

    private IWpdContent? _content;
    private string? _adviseCookie;
    private Action<string>? _objectAddedCallback;

    /// <summary>
    /// Registers for WPD object-added events. The callback receives the WPD object ID of new objects.
    /// </summary>
    internal void RegisterObjectAddedCallback(Action<string> callback)
    {
        if (_device is null) return;
        _objectAddedCallback = callback;

        var handler = new WpdEventHandler(this);
        int hr = _device.Advise(0, handler.Ptr, 0, out _adviseCookie);

        Marshal.ThrowExceptionForHR(hr);
    }

    internal void UnregisterObjectAddedCallback()
    {
        if (_device is not null && _adviseCookie is not null)
        {
            _device.Unadvise(_adviseCookie);
            _adviseCookie = null;
        }
        _objectAddedCallback = null;
    }

    internal void OnWpdEvent(IWpdValues eventParams)
    {
        // Dump event for diagnostics
        eventParams.GetCount(out uint count);
        Console.Error.WriteLine($"[WPD Event] received with {count} properties");
        for (uint i = 0; i < count; i++)
        {
            eventParams.GetAt(i, out PropertyKey key, out PropVariant val);
            Console.Error.WriteLine($"  [{i}] fmtid={key.fmtid} pid={key.pid} vt={val.vt}");
        }

        // Check if this is an object-added event
        // WPD_EVENT_PARAMETER_EVENT_ID is (WPD_EVENT_PROPERTIES, pid=2)
        eventParams.GetGuidValue(new PropertyKey { fmtid = WpdInterop.WPD_EVENT_PROPERTIES, pid = 2 }, out var eventGuid);
        Console.Error.WriteLine($"  EventGuid={eventGuid}");

        // WPD_EVENT_OBJECT_ADDED = {A726DA95-E60C-46D2-8947-048260EC8841}
        if (eventGuid == new Guid("A726DA95-E60C-46D2-8947-048260EC8841"))
        {
            // Get the object ID — WPD_OBJECT_PROPERTIES pid 2 = WPD_OBJECT_ID
            var objKey = new PropertyKey { fmtid = WpdInterop.WPD_OBJECT_PROPERTIES, pid = WpdInterop.PID_OBJECT_ID };
            int hr = eventParams.GetStringValue(objKey, out var objectId);
            Console.Error.WriteLine($"  ObjectId hr=0x{hr:X} id={objectId}");
            if (hr >= 0 && !string.IsNullOrEmpty(objectId))
            {
                _objectAddedCallback?.Invoke(objectId);
            }
        }
    }

    /// <summary>
    /// Downloads a WPD object to a stream using the WPD content/resources API.
    /// </summary>
    internal async Task DownloadObjectAsync(string objectId, Stream destination, CancellationToken ct = default)
    {
        if (_device is null) throw new InvalidOperationException("Not connected");

        _content ??= GetContent();

        Marshal.ThrowExceptionForHR(_content.Transfer(out var resourcesPtr));
        if (resourcesPtr == 0) throw new InvalidOperationException("Failed to get IPortableDeviceResources");

        var (streamPtr, optimalSize) = GetResourceStream(resourcesPtr, objectId);

        if (streamPtr == 0) throw new InvalidOperationException("Failed to get object stream");

        // IStream COM interface — read until empty
        var buffer = new byte[optimalSize > 0 ? optimalSize : 262144];
        try
        {
            while (!ct.IsCancellationRequested)
            {
                int hr = ReadFromIStream(streamPtr, buffer, out var bytesRead);
                if (hr < 0 || bytesRead == 0) break;
                await destination.WriteAsync(buffer.AsMemory(0, (int)bytesRead), ct);
            }
        }
        finally
        {
            Marshal.Release(streamPtr);
        }
    }

    private static unsafe (nint streamPtr, uint optimalSize) GetResourceStream(nint resourcesPtr, string objectId)
    {
        var vtable = Marshal.ReadIntPtr(resourcesPtr);
        var getStreamFn = Marshal.ReadIntPtr(vtable, 5 * nint.Size);
        var key = WpdInterop.WPD_RESOURCE_DEFAULT;
        var pObjectId = Marshal.StringToCoTaskMemUni(objectId);
        try
        {
            nint streamPtr = 0;
            uint optimalSize = 0;
            var fn = (delegate* unmanaged<nint, nint, PropertyKey*, uint, uint*, nint*, int>)getStreamFn;
            int hr = fn(resourcesPtr, pObjectId, &key, WpdInterop.STGM_READ, &optimalSize, &streamPtr);
            Marshal.ThrowExceptionForHR(hr);
            return (streamPtr, optimalSize);
        }
        finally
        {
            Marshal.FreeCoTaskMem(pObjectId);
        }
    }

    /// <summary>
    /// Gets the original filename of a WPD object.
    /// </summary>
    internal string? GetObjectFileName(string objectId)
    {
        if (_device is null) return null;
        _content ??= GetContent();

        int phr = _content.Properties(out var propsPtr);
        if (phr < 0 || propsPtr == 0) return null;
        var props = (IWpdProperties)new StrategyBasedComWrappers().GetOrCreateObjectForComInstance(propsPtr, CreateObjectFlags.None);

        // Get all values (passing null for keys = get all)
        int hr = props.GetValues(objectId, 0, out var values);
        if (hr < 0 || values is null) return null;

        var nameKey = new PropertyKey { fmtid = WpdInterop.WPD_OBJECT_PROPERTIES, pid = WpdInterop.PID_OBJECT_ORIGINAL_FILE_NAME };
        hr = values.GetStringValue(nameKey, out var fileName);
        return hr >= 0 ? fileName : null;
    }

    /// <summary>
    /// Recursively enumerates all objects on the device.
    /// </summary>
    internal List<(string ObjectId, string? FileName)> EnumerateObjects(bool forceRefresh = false)
    {
        if (_device is null) return [];

        _content = GetContent();
        var results = new List<(string, string?)>();
        EnumerateObjectsRecursive("DEVICE", results);
        return results;
    }

    private void EnumerateObjectsRecursive(string parentId, List<(string, string?)> results)
    {
        int hr = _content!.EnumObjects(0, parentId, 0, out var enumPtr);
        if (hr < 0 || enumPtr == 0) return;

        // IEnumPortableDeviceObjectIDs vtable: QueryInterface(0), AddRef(1), Release(2), Next(3), Skip(4), Reset(5), Clone(6), Cancel(7)
        var vtable = Marshal.ReadIntPtr(enumPtr);
        var nextFn = Marshal.ReadIntPtr(vtable, 3 * nint.Size);

        var ids = new nint[20];
        var pinnedIds = GCHandle.Alloc(ids, GCHandleType.Pinned);
        try
        {
            while (true)
            {
                uint fetched = 0;
                unsafe
                {
                    var fn = (delegate* unmanaged<nint, uint, nint, uint*, int>)nextFn;
                    hr = fn(enumPtr, (uint)ids.Length, pinnedIds.AddrOfPinnedObject(), &fetched);
                }
                if (fetched == 0) break;

                for (uint i = 0; i < fetched; i++)
                {
                    var objectId = Marshal.PtrToStringUni(ids[i]);
                    if (ids[i] != 0) Marshal.FreeCoTaskMem(ids[i]);

                    if (string.IsNullOrEmpty(objectId)) continue;

                    var fileName = GetObjectFileName(objectId);
                    if (fileName is not null && Path.HasExtension(fileName))
                    {
                        results.Add((objectId, fileName));
                    }
                    // Always recurse — folders and storage objects contain children
                    EnumerateObjectsRecursive(objectId, results);
                }

                if (hr != 0) break; // S_FALSE = no more
            }
        }
        finally
        {
            pinnedIds.Free();
            Marshal.Release(enumPtr);
        }
    }

    private IWpdContent GetContent()
    {
        Marshal.ThrowExceptionForHR(_device!.Content(out var content));
        return content ?? throw new InvalidOperationException("Failed to get IPortableDeviceContent");
    }

    /// <summary>Reads from an IStream COM pointer. Returns HRESULT.</summary>
    private static int ReadFromIStream(nint pStream, byte[] buffer, out uint bytesRead)
    {
        bytesRead = 0;
        // IStream vtable: QueryInterface(0), AddRef(1), Release(2), Read(3)
        var vtable = Marshal.ReadIntPtr(pStream);
        var readFn = Marshal.ReadIntPtr(vtable, 3 * nint.Size);
        // Read(void* pv, ULONG cb, ULONG* pcbRead)
        var pinnedBuffer = GCHandle.Alloc(buffer, GCHandleType.Pinned);
        try
        {
            unsafe
            {
                uint read = 0;
                var fn = (delegate* unmanaged<nint, nint, uint, uint*, int>)readFn;
                int hr = fn(pStream, pinnedBuffer.AddrOfPinnedObject(), (uint)buffer.Length, &read);
                bytesRead = read;
                return hr;
            }
        }
        finally
        {
            pinnedBuffer.Free();
        }
    }

    /// <summary>
    /// COM-callable event handler that forwards WPD events to <see cref="WpdPtpTransport.OnWpdEvent"/>.
    /// </summary>
    private sealed partial class WpdEventHandler
    {
        private static readonly Guid IID_IPortableDeviceEventCallback = new("a8792a31-f385-493c-a893-40f64eb45f6e");
        private readonly WpdPtpTransport _transport;
        private readonly nint _ptr;

        internal nint Ptr => _ptr;

        internal WpdEventHandler(WpdPtpTransport transport)
        {
            _transport = transport;
            // Create a COM-callable wrapper for our callback
            var callback = new EventCallbackImpl(transport);
            var wrappers = new StrategyBasedComWrappers();
            _ptr = wrappers.GetOrCreateComInterfaceForObject(callback, CreateComInterfaceFlags.None);
        }

        [GeneratedComClass]
        private sealed partial class EventCallbackImpl(WpdPtpTransport transport) : IWpdEventCallback
        {
            public int OnEvent(IWpdValues pEventParameters)
            {
                transport.OnWpdEvent(pEventParameters);
                return 0; // S_OK
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        UnregisterObjectAddedCallback();

        _content = null;

        // Exchange first, so a concurrent second dispose finds nothing left to close — Close on a
        // device another teardown already shut down is where the 0x802A0002 ("Shutdown was already
        // called") warnings at exit came from. Then close through the command gate, so an in-flight
        // command finishes before the device goes away underneath it. A wedged command times out
        // the gate and the close proceeds anyway: failing that command is the point of tearing down.
        if (Interlocked.Exchange(ref _device, null) is { } device)
        {
            var closed = await _gate.RunAsync(() => { device.Close(); return true; }, onTimeout: false)
                .ConfigureAwait(false);
            if (!closed)
            {
                device.Close();
            }
        }
    }
}
