using System.Buffers.Binary;
using FC.SDK.Protocol;

namespace FC.SDK.Canon;

internal sealed class CanonPtpSession(PtpSession ptp) : IAsyncDisposable
{
    private const uint SessionId = 1;
    private const uint RemoteModeStandard = 1;
    private const uint EventModeStandard = 1;

    /// <summary>
    /// Standard PTP battery level (0x5001), readable via WPD MTP EXT.
    /// </summary>
    internal const ushort StandardPtpBatteryLevel = 0x5001;

    /// <summary>
    /// Properties read on session open. Updated by <see cref="RefreshPropertiesAsync"/>.
    /// </summary>
    internal byte? BatteryLevelPercent { get; private set; }

    internal async Task<EdsError> OpenAsync(CancellationToken ct = default)
    {
        // 1. Standard PTP OpenSession
        var resp = await ptp.SendCommandAsync(PtpOperationCode.OpenSession, ct, SessionId);
        if (!resp.IsSuccess) return resp.ToEdsError();

        // 2. Canon SetRemoteMode
        resp = await ptp.SendCommandAsync(PtpOperationCode.CanonSetRemoteMode, ct, RemoteModeStandard);
        if (!resp.IsSuccess) return resp.ToEdsError();

        // 3. Canon SetEventMode
        resp = await ptp.SendCommandAsync(PtpOperationCode.CanonSetEventMode, ct, EventModeStandard);
        if (!resp.IsSuccess) return resp.ToEdsError();

        // 4. Drain initial events
        _ = await PollEventsAsync(ct);

        // 5. Read standard PTP properties
        await RefreshPropertiesAsync(ct);

        return EdsError.OK;
    }

    internal string? SerialNumber { get; private set; }
    internal string? Model { get; private set; }

    /// <summary>
    /// Reads standard PTP device info and properties.
    /// </summary>
    internal async Task RefreshPropertiesAsync(CancellationToken ct = default)
    {
        // GetDeviceInfo (0x1001) — serial number, model, manufacturer
        var (diResp, diData) = await ptp.SendCommandReceiveDataAsync(PtpOperationCode.GetDeviceInfo, ct);
        if (diResp.IsSuccess && diData.Length > 0)
            ParseDeviceInfo(diData);

        // Battery level — standard PTP property 0x5001
        var (resp, data) = await ptp.SendCommandReceiveDataAsync(PtpOperationCode.GetDevicePropValue, ct, StandardPtpBatteryLevel);
        if (resp.IsSuccess && data.Length >= 1)
        {
            BatteryLevelPercent = data[0];
        }
    }

    private void ParseDeviceInfo(byte[] data)
    {
        // PTP DeviceInfo dataset: skip fixed fields, read PTP strings
        // Offset 8: VendorExtensionDesc (PTP string), then skip several fields to reach:
        // Model, DeviceVersion, SerialNumber as the last three PTP strings.
        // PTP string format: uint8 length (in chars), then UTF-16LE chars
        try
        {
            int offset = 8; // skip StandardVersion(u16), VendorExtId(u32), VendorExtVersion(u16)
            offset = SkipPtpString(data, offset); // VendorExtensionDesc
            offset += 2; // FunctionalMode (u16)
            offset = SkipPtpArray(data, offset); // OperationsSupported (u16 array)
            offset = SkipPtpArray(data, offset); // EventsSupported (u16 array)
            offset = SkipPtpArray(data, offset); // DevicePropertiesSupported (u16 array)
            offset = SkipPtpArray(data, offset); // CaptureFormats (u16 array)
            offset = SkipPtpArray(data, offset); // ImageFormats (u16 array)
            var (manufacturer, o1) = ReadPtpString(data, offset);
            var (model, o2) = ReadPtpString(data, o1);
            var (deviceVersion, o3) = ReadPtpString(data, o2);
            var (serialNumber, _) = ReadPtpString(data, o3);
            Model = model;
            SerialNumber = serialNumber;
        }
        catch { /* malformed device info — not fatal */ }
    }

    private static (string Value, int NewOffset) ReadPtpString(byte[] data, int offset)
    {
        if (offset >= data.Length) return ("", offset);
        int charCount = data[offset];
        offset++;
        if (charCount == 0) return ("", offset);
        var str = System.Text.Encoding.Unicode.GetString(data, offset, (charCount - 1) * 2); // exclude null terminator
        return (str, offset + charCount * 2);
    }

    private static int SkipPtpString(byte[] data, int offset)
    {
        if (offset >= data.Length) return offset;
        int charCount = data[offset];
        return offset + 1 + charCount * 2;
    }

    private static int SkipPtpArray(byte[] data, int offset)
    {
        if (offset + 4 > data.Length) return offset;
        uint count = BitConverter.ToUInt32(data, offset);
        return offset + 4 + (int)count * 2; // u16 elements
    }

    /// <summary>
    /// Opens a PTP session without Canon remote/event mode.
    /// Standard PTP commands (InitiateCapture) work; Canon vendor commands (RemoteRelease) do not.
    /// </summary>
    internal async Task<EdsError> OpenNoRemoteModeAsync(CancellationToken ct = default)
    {
        var resp = await ptp.SendCommandAsync(PtpOperationCode.OpenSession, ct, SessionId);
        return resp.ToEdsError();
    }

    /// <summary>
    /// Standard PTP InitiateCapture (0x100E). Camera takes a picture using its current settings.
    /// </summary>
    internal async Task<EdsError> InitiateCaptureAsync(CancellationToken ct = default)
    {
        var resp = await ptp.SendCommandAsync(PtpOperationCode.InitiateCapture, ct, 0u, 0u);
        return resp.ToEdsError();
    }

    internal async Task<EdsError> SetRemoteModeAsync(uint mode, CancellationToken ct = default)
    {
        var resp = await ptp.SendCommandAsync(PtpOperationCode.CanonSetRemoteMode, ct, mode);
        return resp.ToEdsError();
    }

    internal async Task<EdsError> CloseAsync(CancellationToken ct = default)
    {
        // Disable remote mode
        await ptp.SendCommandAsync(PtpOperationCode.CanonSetRemoteMode, ct, 0);

        var resp = await ptp.SendCommandAsync(PtpOperationCode.CloseSession, ct);
        return resp.ToEdsError();
    }

    internal async Task<EdsError> SetPropertyUInt32Async(ushort ptpPropCode, uint value, CancellationToken ct = default)
    {
        // Canon SetPropValue (0x9110): data phase = [propCode:u32][value:u32]
        var data = new byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(data, ptpPropCode);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(4), value);

        var resp = await ptp.SendCommandWithDataAsync(PtpOperationCode.CanonSetPropValue, data, ct);
        return resp.ToEdsError();
    }

    internal async Task<(EdsError Error, uint Value)> GetPropertyUInt32Async(ushort ptpPropCode, CancellationToken ct = default)
    {
        var (resp, data) = await ptp.SendCommandReceiveDataAsync(PtpOperationCode.GetDevicePropValue, ct, ptpPropCode);
        if (!resp.IsSuccess)
            return (resp.ToEdsError(), 0);

        uint value = data.Length >= 4
            ? BinaryPrimitives.ReadUInt32LittleEndian(data)
            : 0;

        return (EdsError.OK, value);
    }

    internal async Task<EdsError> RemoteReleaseOnAsync(uint mode, CancellationToken ct = default)
    {
        var resp = await ptp.SendCommandAsync(PtpOperationCode.CanonRemoteReleaseOn, ct, mode);
        return resp.ToEdsError();
    }

    internal async Task<EdsError> RemoteReleaseOffAsync(uint mode, CancellationToken ct = default)
    {
        var resp = await ptp.SendCommandAsync(PtpOperationCode.CanonRemoteReleaseOff, ct, mode);
        return resp.ToEdsError();
    }

    internal async Task<EdsError> BulbStartAsync(CancellationToken ct = default)
    {
        // AF first
        var err = await RemoteReleaseOnAsync(0x01, ct);
        if (err is not EdsError.OK) return err;

        var resp = await ptp.SendCommandAsync(PtpOperationCode.CanonBulbStart, ct);
        return resp.ToEdsError();
    }

    internal async Task<EdsError> BulbEndAsync(CancellationToken ct = default)
    {
        var resp = await ptp.SendCommandAsync(PtpOperationCode.CanonBulbEnd, ct);
        var err = resp.ToEdsError();

        // Release AF
        await RemoteReleaseOffAsync(0x01, ct);

        return err;
    }

    internal async Task<EdsError> DriveLensAsync(EdsDriveLensStep step, CancellationToken ct = default)
    {
        var resp = await ptp.SendCommandAsync(PtpOperationCode.CanonDriveLens, ct, (uint)step);
        return resp.ToEdsError();
    }

    internal async Task<EdsError> InitiateViewfinderAsync(CancellationToken ct = default)
    {
        var resp = await ptp.SendCommandAsync(PtpOperationCode.CanonInitiateViewfinder, ct);
        return resp.ToEdsError();
    }

    internal async Task<(EdsError Error, byte[] JpegData)> GetViewfinderDataAsync(CancellationToken ct = default)
    {
        var (resp, data) = await ptp.SendCommandReceiveDataAsync(
            PtpOperationCode.CanonGetViewfinderData, ct, 0x00200000, 0, 0);

        return (resp.ToEdsError(), data);
    }

    internal async Task<EdsError> TerminateViewfinderAsync(CancellationToken ct = default)
    {
        var resp = await ptp.SendCommandAsync(PtpOperationCode.CanonTerminateViewfinder, ct);
        return resp.ToEdsError();
    }

    /// <summary>
    /// Queries Canon GetObjectInfo (0x9103) and returns the original filename (e.g. "IMG_1234.CR2" or "IMG_1234.CR3").
    /// </summary>
    internal async Task<(EdsError Error, string? FileName)> GetObjectInfoAsync(uint objectHandle, CancellationToken ct = default)
    {
        var (resp, data) = await ptp.SendCommandReceiveDataAsync(PtpOperationCode.CanonGetObjectInfo, ct, objectHandle);
        if (!resp.IsSuccess)
            return (resp.ToEdsError(), null);

        // Canon ObjectInfo dataset (same layout as standard PTP ObjectInfo):
        //   StorageID:u32, ObjectFormat:u16, ProtectionStatus:u16,
        //   ObjectCompressedSize:u32, ThumbFormat:u16, ThumbCompressedSize:u32,
        //   ThumbPixWidth:u32, ThumbPixHeight:u32, ImagePixWidth:u32, ImagePixHeight:u32,
        //   ImageBitDepth:u32, ParentObject:u32, AssociationType:u16, AssociationDesc:u32,
        //   SequenceNumber:u32, Filename (PTP string), ...
        try
        {
            // Fixed portion = 4+2+2+4+2+4+4+4+4+4+4+4+2+4+4 = 52 bytes
            const int filenameOffset = 52;
            if (data.Length > filenameOffset)
            {
                var (fileName, _) = ReadPtpString(data, filenameOffset);
                return (EdsError.OK, fileName);
            }
        }
        catch { /* malformed — fall through */ }
        return (EdsError.OK, null);
    }

    /// <summary>
    /// Downloads the JPEG thumbnail for an object. Much faster than full CR2/CR3 download.
    /// Uses standard PTP GetThumb (0x100A).
    /// </summary>
    internal async Task<(EdsError Error, byte[] JpegData)> GetThumbAsync(uint objectHandle, CancellationToken ct = default)
    {
        var (resp, data) = await ptp.SendCommandReceiveDataAsync(PtpOperationCode.GetThumb, ct, objectHandle);
        return (resp.ToEdsError(), data);
    }

    internal async Task<EdsError> GetObjectAsync(uint objectHandle, Stream destination, CancellationToken ct = default)
    {
        var (resp, data) = await ptp.SendCommandReceiveDataAsync(PtpOperationCode.CanonGetObject, ct, objectHandle);
        if (!resp.IsSuccess)
            return resp.ToEdsError();

        await destination.WriteAsync(data, ct);
        return EdsError.OK;
    }

    internal async Task<EdsError> TransferCompleteAsync(uint objectHandle, CancellationToken ct = default)
    {
        var resp = await ptp.SendCommandAsync(PtpOperationCode.CanonTransferComplete, ct, objectHandle);
        return resp.ToEdsError();
    }

    /// <summary>Cancels an in-progress transfer. Use when a download is stuck or unwanted.</summary>
    internal async Task<EdsError> CancelTransferAsync(uint objectHandle, CancellationToken ct = default)
    {
        var resp = await ptp.SendCommandAsync(PtpOperationCode.CanonCancelTransfer, ct, objectHandle);
        return resp.ToEdsError();
    }

    /// <summary>Resets a failed transfer so it can be retried.</summary>
    internal async Task<EdsError> ResetTransferAsync(uint objectHandle, CancellationToken ct = default)
    {
        var resp = await ptp.SendCommandAsync(PtpOperationCode.CanonResetTransfer, ct, objectHandle);
        return resp.ToEdsError();
    }

    /// <summary>
    /// Reads the packed Custom Function data block from the camera.
    /// Canon stores C.Fn data as a device property in the 0xD1A0..0xD1AF range.
    /// Tries 0xD1A0 first (common on most EOS bodies).
    /// </summary>
    internal async Task<(EdsError Error, CanonCustomFunctionBlock? Block)> GetCustomFunctionBlockAsync(CancellationToken ct = default)
    {
        // Canon C.Fn property codes vary by generation; 0xD1A0 is the most common
        ushort[] cfnPropertyCodes = [0xD1A0, 0xD1A1, 0xD1A2];

        foreach (var propCode in cfnPropertyCodes)
        {
            var (resp, data) = await ptp.SendCommandReceiveDataAsync(PtpOperationCode.GetDevicePropValue, ct, propCode);
            if (resp.IsSuccess && data.Length >= 16)
                return (EdsError.OK, CanonCustomFunctionBlock.Parse(data));
        }

        return (EdsError.DevicePropNotSupported, null);
    }

    /// <summary>
    /// Writes a modified Custom Function data block back to the camera.
    /// </summary>
    internal async Task<EdsError> SetCustomFunctionBlockAsync(CanonCustomFunctionBlock block, CancellationToken ct = default)
    {
        // Write via Canon SetPropValue (0x9110) with the C.Fn property code.
        // The data phase = [propcode:u32][cfn_block_bytes...]
        // Try 0xD1A0 first.
        ushort cfnPropCode = 0xD1A0;
        var rawData = block.RawData;
        var data = new byte[4 + rawData.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(data, cfnPropCode);
        rawData.CopyTo(data, 4);

        var resp = await ptp.SendCommandWithDataAsync(PtpOperationCode.CanonSetPropValue, data, ct);
        return resp.ToEdsError();
    }

    internal async Task<IReadOnlyList<CanonPtpEvent>> PollEventsAsync(CancellationToken ct = default)
    {
        var (resp, data) = await ptp.SendCommandReceiveDataAsync(PtpOperationCode.CanonGetEvent, ct);
        if (!resp.IsSuccess)
            return [];

        return ParseEvents(data);
    }

    private static List<CanonPtpEvent> ParseEvents(byte[] data)
    {
        var events = new List<CanonPtpEvent>();
        int offset = 0;

        while (offset + 8 <= data.Length)
        {
            uint recordLen = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset));
            uint eventType = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset + 4));

            // Sentinel: {length=8, type=0} terminates the event list
            if (recordLen <= 8 && eventType == 0)
                break;

            uint p1 = offset + 8 + 4 <= data.Length && recordLen > 8
                ? BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset + 8))
                : 0;
            uint p2 = offset + 12 + 4 <= data.Length && recordLen > 12
                ? BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset + 12))
                : 0;
            uint p3 = offset + 16 + 4 <= data.Length && recordLen > 16
                ? BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset + 16))
                : 0;

            events.Add(new CanonPtpEvent
            {
                Type = (CanonEventType)eventType,
                Param1 = p1,
                Param2 = p2,
                Param3 = p3,
            });

            offset += (int)recordLen;
        }

        return events;
    }

    public ValueTask DisposeAsync() => ptp.DisposeAsync();
}
