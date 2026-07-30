using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using System.Runtime.Versioning;

namespace FC.SDK.Transport;

[SupportedOSPlatform("windows")]
internal sealed partial class WpdPtpTransport : IPtpTransport
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
    internal Task<(ushort ResponseCode, uint[] ResponseParams)> ExecuteCommandAsync(
        ushort opCode, uint[] @params, CancellationToken ct = default) =>
        _gate.RunAsync(() => ExecuteNoData(opCode, @params), (TimedOutCode, Array.Empty<uint>()), ct);

    // The read path is a SEQUENCE of blocking calls (initiate → read → end), so it takes the
    // async gate overload: each phase is awaited off-thread individually, which is what lets the
    // end phase carry its own deadline without anybody blocking on a Wait.
    internal Task<(ushort ResponseCode, uint[] ResponseParams, byte[] Data)> ExecuteCommandReadDataAsync(
        ushort opCode, uint[] @params, CancellationToken ct = default) =>
        _gate.RunAsync(() => ExecuteReadDataAsync(opCode, @params),
            (TimedOutCode, Array.Empty<uint>(), Array.Empty<byte>()), ct);

    internal Task<(ushort ResponseCode, uint[] ResponseParams)> ExecuteCommandWriteDataAsync(
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
    /// How long the end-of-transfer phase of the FIRST viewfinder read may take before this body is
    /// judged to hang there. Short on purpose: live view runs at ~10 fps, so a phase that has not
    /// answered in this long is the known hang rather than a slow link.
    /// </summary>
    private static readonly TimeSpan ViewfinderEndTransferProbe = TimeSpan.FromMilliseconds(750);

    /// <summary>Chunk size for draining a data phase whose full length the driver did not report.</summary>
    private const uint DrainChunkSize = 1u << 20;

    /// <summary>Safety bound on drain iterations, so a driver that never returns 0 cannot spin forever.</summary>
    private const int MaxDrainChunks = 16;

    /// <summary>
    /// Keeps reading until the driver stops handing bytes back, appending to what the first read got.
    /// </summary>
    /// <remarks>
    /// WPD's TRANSFER_TOTAL_SIZE is exact for an object read (the driver knows the object's size) but
    /// on a vendor read it can describe only the first chunk. Leaving the rest in the pipe is what
    /// made ENDDATATRANSFER hang on a 450D viewfinder frame: the driver was still waiting for data
    /// nobody collected. Draining first lets the end phase complete normally, which is what keeps the
    /// session usable for the NEXT frame — skipping the end phase instead bought two frames and then
    /// jammed every later initiate at totalSize=0.
    /// </remarks>
    private async Task<byte[]> DrainRemainderAsync(string context, byte[] first, bool trace)
    {
        var chunks = new List<byte[]> { first };
        var total = first.Length;

        for (var i = 0; i < MaxDrainChunks; i++)
        {
            byte[] chunk;
            try
            {
                chunk = await Offload(() => ReadChunk(context, DrainChunkSize)).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // A refused extra read means there was nothing left — normal, not a failure.
                if (trace) Console.Error.WriteLine($"[wpd] drain stopped: {ex.Message}");
                break;
            }

            if (chunk.Length == 0) break;

            chunks.Add(chunk);
            total += chunk.Length;
            if (trace) Console.Error.WriteLine($"[wpd] drained {chunk.Length} more bytes (total {total})");
        }

        if (chunks.Count == 1) return first;

        var joined = new byte[total];
        var offset = 0;
        foreach (var chunk in chunks)
        {
            chunk.CopyTo(joined, offset);
            offset += chunk.Length;
        }
        return joined;
    }

    private byte[] ReadChunk(string context, uint want)
    {
        var cmd = CreateValues();
        SetCommandKey(cmd, WpdInterop.PID_READ_DATA);
        cmd.SetStringValue(WpdInterop.MtpExtKey(WpdInterop.PID_TRANSFER_CONTEXT), context);
        cmd.SetUnsignedIntegerValue(WpdInterop.MtpExtKey(WpdInterop.PID_TRANSFER_NUM_BYTES_TO_READ), want);
        cmd.SetBufferValue(WpdInterop.MtpExtKey(WpdInterop.PID_TRANSFER_DATA), new byte[want], want);

        var results = SendWpdCommand(cmd);
        CheckHResult(results);
        return ExtractBuffer(results, want);
    }

    /// <summary>
    /// Finishes a viewfinder read, bounded so a body that never answers cannot wedge the transport.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Reports <see cref="Protocol.PtpResponseCode.LocalTimeout"/> when the phase does not come back,
    /// which surfaces as <c>WaitTimeoutError</c> and lets the caller's timeout counter stop live view
    /// cleanly. Deliberately NOT "skip the end phase and keep going": that bought two frames and then
    /// jammed every later initiate at <c>totalSize=0</c>, and left the session unusable until
    /// reconnect — the original "always end transfer to avoid jamming the pipe" note was right.
    /// The real cure is draining the data phase first (see <see cref="DrainRemainder"/>).
    /// </para>
    /// <para>
    /// Frida-tracing EDSDK on the same body shows the structural difference: it bypasses the WPD COM
    /// API entirely and drives the MTP device through overlapped <c>DeviceIoControl</c> calls, one
    /// ~2 MiB request per frame, with no separate end phase to hang in.
    /// </para>
    /// </remarks>
    private async Task<ushort> EndViewfinderTransferAsync(string context, int frameBytes, bool trace)
    {
        // A dedicated thread, not a pool one: if this call never returns the thread is lost, and at
        // ~10 fps burning pool threads starves everything — that is what made the UI go sluggish.
        var end = Task.Factory.StartNew(() => EndTransfer(context),
            CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

        try
        {
            // Awaited, not waited: the deadline costs no thread of its own, and it runs on the
            // transport's TimeProvider so a test can expire it without real elapsed time.
            var response = (await end.WaitAsync(ViewfinderEndTransferProbe, _time).ConfigureAwait(false)).ResponseCode;
            if (trace) Console.Error.WriteLine($"[wpd] 0x9153 done ({frameBytes} bytes), response 0x{response:X4}");
            return response;
        }
        catch (TimeoutException)
        {
            // Observe the eventual exception so the abandoned phase is never an unhandled one.
            _ = end.ContinueWith(static t => _ = t.Exception, TaskScheduler.Default);
            if (trace) Console.Error.WriteLine("[wpd] 0x9153 end-transfer did not answer; reporting a timeout");
            return TimedOutCode;
        }
        catch (Exception ex)
        {
            if (trace) Console.Error.WriteLine($"[wpd] 0x9153 end-transfer failed: {ex.Message}");
            return TimedOutCode;
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

        // Step 2: Read data — always end transfer even on failure to avoid jamming the pipe
        try
        {
            if (trace) Console.Error.WriteLine($"[wpd] 0x{opCode:X4} reading {totalSize} bytes…");
            byte[] data = await Offload(() => ReadChunk(context, totalSize)).ConfigureAwait(false);

            var isViewfinder = opCode == (ushort)Protocol.PtpOperationCode.CanonGetViewfinderData;
            if (isViewfinder)
            {
                data = await DrainRemainderAsync(context, data, trace).ConfigureAwait(false);
            }

            // Step 3: End transfer
            if (trace) Console.Error.WriteLine($"[wpd] 0x{opCode:X4} read {data.Length} bytes, ending transfer…");

            if (isViewfinder)
            {
                return (await EndViewfinderTransferAsync(context, data.Length, trace).ConfigureAwait(false), [], data);
            }

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
        int hr = _device.SendCommand(0, cmd, out var results);

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

    private static byte[] ExtractBuffer(IWpdValues results, uint size)
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
