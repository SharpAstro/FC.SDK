using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace FC.SDK.Transport;

[SupportedOSPlatform("windows")]
internal sealed class WpdPtpTransport : IPtpTransport
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
        var clientInfo = WpdInterop.CreateInstance<IWpdValues>(WpdInterop.CLSID_PortableDeviceValues);

        // WPD requires client identity — name, major/minor version, revision
        var clientKey = new PropertyKey { fmtid = WpdInterop.WPD_CLIENT_INFO, pid = 2 }; // WPD_CLIENT_NAME
        clientInfo.SetStringValue(in clientKey, "TianWen");
        clientKey.pid = 3; // WPD_CLIENT_MAJOR_VERSION
        clientInfo.SetUnsignedIntegerValue(in clientKey, 1);
        clientKey.pid = 4; // WPD_CLIENT_MINOR_VERSION
        clientInfo.SetUnsignedIntegerValue(in clientKey, 0);
        clientKey.pid = 5; // WPD_CLIENT_REVISION
        clientInfo.SetUnsignedIntegerValue(in clientKey, 0);

        Marshal.ThrowExceptionForHR(_device.Open(_deviceId, clientInfo));
        return Task.CompletedTask;
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
        Marshal.ThrowExceptionForHR(_device.SendCommand(0, cmd, out var results));
        return results!;
    }

    private static void CheckHResult(IWpdValues results)
    {
        int hr = results.GetErrorValue(WpdInterop.CommonKey(WpdInterop.PID_HRESULT), out int errorValue);
        if (hr >= 0 && errorValue != 0)
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

    public ValueTask DisposeAsync()
    {
        _device?.Close();
        _device = null;
        _lock.Dispose();
        return ValueTask.CompletedTask;
    }
}
