using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using System.Runtime.Versioning;

namespace FC.SDK.Transport;

[SupportedOSPlatform("windows")]
internal sealed partial class WpdPtpTransport : IPtpTransport
{
    private readonly string _deviceId;
    private IWpdDevice? _device;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public bool IsConnected => _device is not null;

    public string DeviceId => _deviceId;

    internal WpdPtpTransport(string deviceId)
    {
        _deviceId = deviceId;
    }

    public Task ConnectAsync(CancellationToken ct = default)
    {
        _device = WpdInterop.CreateInstance<IWpdDevice>(WpdInterop.CLSID_PortableDevice);
        Marshal.ThrowExceptionForHR(_device.Open(_deviceId, CreateClientInfo()));
        return Task.CompletedTask;
    }

    private static IWpdValues CreateClientInfo()
    {
        var clientInfo = WpdInterop.CreateInstance<IWpdValues>(WpdInterop.CLSID_PortableDeviceValues);
        var clientKey = new PropertyKey { fmtid = WpdInterop.WPD_CLIENT_INFO, pid = 2 }; // WPD_CLIENT_NAME
        clientInfo.SetStringValue(in clientKey, "TianWen");
        clientKey.pid = 3; // WPD_CLIENT_MAJOR_VERSION
        clientInfo.SetUnsignedIntegerValue(in clientKey, 1);
        clientKey.pid = 4; // WPD_CLIENT_MINOR_VERSION
        clientInfo.SetUnsignedIntegerValue(in clientKey, 0);
        clientKey.pid = 5; // WPD_CLIENT_REVISION
        clientInfo.SetUnsignedIntegerValue(in clientKey, 0);
        return clientInfo;
    }

    public ValueTask SendAsync(ReadOnlyMemory<byte> packet, CancellationToken ct = default) =>
        throw new NotSupportedException("WPD transport uses typed command methods.");

    public ValueTask<int> ReceiveAsync(Memory<byte> buffer, CancellationToken ct = default) =>
        throw new NotSupportedException("WPD transport uses typed command methods.");

    public ValueTask<int> ReceiveEventAsync(Memory<byte> buffer, CancellationToken ct = default) =>
        ValueTask.FromResult(0);

    internal async Task<(ushort ResponseCode, uint[] ResponseParams)> ExecuteCommandAsync(
        ushort opCode, uint[] @params, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try { return ExecuteNoData(opCode, @params); }
        finally { _lock.Release(); }
    }

    internal async Task<(ushort ResponseCode, uint[] ResponseParams, byte[] Data)> ExecuteCommandReadDataAsync(
        ushort opCode, uint[] @params, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try { return ExecuteReadData(opCode, @params); }
        finally { _lock.Release(); }
    }

    internal async Task<(ushort ResponseCode, uint[] ResponseParams)> ExecuteCommandWriteDataAsync(
        ushort opCode, uint[] @params, byte[] data, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try { return ExecuteWriteData(opCode, @params, data); }
        finally { _lock.Release(); }
    }

    private (ushort, uint[]) ExecuteNoData(ushort opCode, uint[] @params)
    {
        var cmd = CreateValues();
        SetCommandKey(cmd, WpdInterop.PID_EXECUTE_NO_DATA);
        cmd.SetUnsignedIntegerValue(WpdInterop.MtpExtKey(WpdInterop.PID_OPERATION_CODE), opCode);
        SetOperationParams(cmd, @params);

        var results = SendWpdCommand(cmd);
        return ExtractResponse(results);
    }

    private (ushort, uint[], byte[]) ExecuteReadData(ushort opCode, uint[] @params)
    {
        // Step 1: Initiate read
        var cmd = CreateValues();
        SetCommandKey(cmd, WpdInterop.PID_EXECUTE_DATA_READ);
        cmd.SetUnsignedIntegerValue(WpdInterop.MtpExtKey(WpdInterop.PID_OPERATION_CODE), opCode);
        SetOperationParams(cmd, @params);

        var results = SendWpdCommand(cmd);

        // Debug: dump all properties from results
        results.GetCount(out uint propCount);
        Console.Error.WriteLine($"[WPD] ExecuteDataRead opCode=0x{opCode:X4} — result has {propCount} properties:");
        for (uint i = 0; i < propCount; i++)
        {
            results.GetAt(i, out PropertyKey key, out PropVariant val);
            Console.Error.WriteLine($"  [{i}] fmtid={key.fmtid} pid={key.pid} vt={val.vt} data=0x{val.data:X}");
        }

        CheckHResult(results);
        results.GetStringValue(WpdInterop.MtpExtKey(WpdInterop.PID_TRANSFER_CONTEXT), out string context);
        results.GetUnsignedIntegerValue(WpdInterop.MtpExtKey(WpdInterop.PID_TRANSFER_TOTAL_SIZE), out uint totalSize);

        // Step 2: Read data
        var readCmd = CreateValues();
        SetCommandKey(readCmd, WpdInterop.PID_READ_DATA);
        readCmd.SetStringValue(WpdInterop.MtpExtKey(WpdInterop.PID_TRANSFER_CONTEXT), context);
        readCmd.SetUnsignedIntegerValue(WpdInterop.MtpExtKey(WpdInterop.PID_TRANSFER_NUM_BYTES_TO_READ), totalSize);
        readCmd.SetBufferValue(WpdInterop.MtpExtKey(WpdInterop.PID_TRANSFER_DATA), new byte[totalSize], totalSize);

        var readResults = SendWpdCommand(readCmd);
        CheckHResult(readResults);
        byte[] data = ExtractBuffer(readResults, totalSize);

        // Step 3: End transfer
        var endResponse = EndTransfer(context);
        return (endResponse.ResponseCode, endResponse.ResponseParams, data);
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

        // Step 2: Write data
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
        if (@params.Length == 0) return;

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
        Console.Error.WriteLine($"[WPD] SendCommand hr=0x{hr:X8}");
        Marshal.ThrowExceptionForHR(hr);
        return results!;
    }

    private const int E_ELEMENT_NOT_FOUND = unchecked((int)0x80070490);

    private static void CheckHResult(IWpdValues results)
    {
        int hr = results.GetErrorValue(WpdInterop.CommonKey(WpdInterop.PID_HRESULT), out int errorValue);
        Console.Error.WriteLine($"[WPD] CheckHResult: GetErrorValue hr=0x{hr:X8} errorValue=0x{errorValue:X8}");
        // 0x80070490 (ELEMENT_NOT_FOUND) is returned when:
        // - GetEvent has no events (normal)
        // - A property code is not recognized by the WPD driver
        // Treat as "no data" rather than a hard error
        if (hr >= 0 && errorValue != 0 && errorValue != E_ELEMENT_NOT_FOUND)
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
        Console.Error.WriteLine($"[WPD] Advise hr=0x{hr:X8} cookie={_adviseCookie}");
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

        // Call IPortableDeviceResources::GetStream via raw vtable (index 2 after IUnknown)
        // GetStream(LPCWSTR objectId, REFPROPERTYKEY key, DWORD mode, DWORD* optimalSize, IStream** ppStream)
        var streamPtr = nint.Zero;
        uint optimalSize = 0;
        unsafe
        {
            var vtable = Marshal.ReadIntPtr(resourcesPtr);
            var getStreamFn = Marshal.ReadIntPtr(vtable, 5 * nint.Size); // IUnknown(3) + GetSupportedResources(0) + GetResourceAttributes(1) + GetStream(2) = slot 5
            var key = WpdInterop.WPD_RESOURCE_DEFAULT;
            var pObjectId = Marshal.StringToCoTaskMemUni(objectId);
            try
            {
                var fn = (delegate* unmanaged<nint, nint, PropertyKey*, uint, uint*, nint*, int>)getStreamFn;
                int hr2 = fn(resourcesPtr, pObjectId, &key, WpdInterop.STGM_READ, &optimalSize, &streamPtr);
                Marshal.ThrowExceptionForHR(hr2);
            }
            finally
            {
                Marshal.FreeCoTaskMem(pObjectId);
            }
        }

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

    public ValueTask DisposeAsync()
    {
        UnregisterObjectAddedCallback();
        _device?.Close();
        _device = null;
        _lock.Dispose();
        return ValueTask.CompletedTask;
    }
}
