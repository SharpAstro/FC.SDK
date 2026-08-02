using System.Buffers.Binary;
using FC.SDK.Protocol;

namespace FC.SDK.Canon;

internal sealed class CanonPtpSession(PtpSession ptp) : IAsyncDisposable
{
    private const uint SessionId = 1;
    private const uint RemoteModeStandard = 1;
    private const uint EventModeStandard = 1;

    /// <summary>
    /// All OLC info groups. Newer bodies stay silent about Tv/Av/ISO/AF state until subscribed.
    /// </summary>
    private const uint OlcInfoGroupAll = 0x00001FFF;

    /// <summary>
    /// Safety valve for the GetEvent drain loop. The initial dump after SetEventMode takes a
    /// handful of round trips; anything beyond this means the camera is looping.
    /// </summary>
    private const int MaxDrainIterations = 64;

    /// <summary>
    /// Canon returns DeviceBusy from SetDevicePropValueEx while it is mid-capture or still
    /// digesting an earlier change. libgphoto2 retries the same way.
    /// </summary>
    private const int SetPropertyBusyRetries = 5;
    private static readonly TimeSpan SetPropertyBusyDelay = TimeSpan.FromMilliseconds(200);

    /// <summary>
    /// Standard PTP battery level (0x5001), readable via WPD MTP EXT.
    /// </summary>
    internal const ushort StandardPtpBatteryLevel = 0x5001;

    /// <summary>
    /// Properties read on session open. Updated by <see cref="RefreshPropertiesAsync"/>.
    /// </summary>
    internal byte? BatteryLevelPercent { get; private set; }

    internal string? SerialNumber { get; private set; }
    internal string? Model { get; private set; }

    /// <summary>
    /// PTP operation codes the camera advertises in GetDeviceInfo. Empty until the session is open.
    /// EOS bodies typically do NOT list GetDevicePropValue (0x1015), which is why property reads
    /// have to come out of <see cref="Properties"/>.
    /// </summary>
    internal IReadOnlySet<ushort> SupportedOperations { get; private set; } = new HashSet<ushort>();

    /// <summary>Property mirror fed by the event stream. The only source of EOS property values.</summary>
    internal CanonPropertyCache Properties { get; } = new();

    /// <summary>
    /// Raised for every event decoded from GetEvent, no matter which call site polled — the
    /// background poller, the initial drain, or a read that requested a property push. Consumers
    /// see the whole stream instead of only what the poller happened to pick up.
    /// </summary>
    internal event Action<CanonPtpEvent>? EventReceived;

    internal async Task<EdsError> OpenAsync(CancellationToken ct = default)
    {
        // 1. Standard PTP OpenSession
        var resp = await ptp.SendCommandAsync(PtpOperationCode.OpenSession, ct, SessionId);
        if (!resp.IsSuccess) return resp.ToEdsError();

        // 2. Read standard PTP device info first — it tells us which operations exist
        await RefreshPropertiesAsync(ct);

        // 3. Canon SetRemoteMode
        resp = await ptp.SendCommandAsync(PtpOperationCode.CanonSetRemoteMode, ct, RemoteModeStandard);
        if (!resp.IsSuccess) return resp.ToEdsError();

        // 4. Canon SetEventMode
        resp = await ptp.SendCommandAsync(PtpOperationCode.CanonSetEventMode, ct, EventModeStandard);
        if (!resp.IsSuccess) return resp.ToEdsError();

        // 5. Subscribe to the OLC info bundle (Tv/Av/ISO/AF state). Best effort — older bodies
        //    do not implement it and answer GeneralError, which is not fatal.
        if (IsOperationSupported(PtpOperationCode.CanonSetRequestOLCInfoGroup))
        {
            await ptp.SendCommandAsync(PtpOperationCode.CanonSetRequestOLCInfoGroup, ct, OlcInfoGroupAll);
        }

        // 6. Drain the initial property dump. This is not one GetEvent but several: the camera
        //    describes every property and its selectable values before it will accept a
        //    SetDevicePropValueEx, and answers DeviceBusy until the queue is empty.
        await DrainEventsAsync(ct);

        // 7. Ask for the values the camera does not volunteer.
        foreach (var propCode in RequestOnOpenPropertyCodes)
        {
            await RequestDevicePropValueAsync(propCode, ct);
        }
        await DrainEventsAsync(ct);

        return EdsError.OK;
    }

    /// <summary>
    /// Properties the camera never pushes on its own — they have to be requested explicitly.
    /// Matches the set libgphoto2 asks for after SetEventMode.
    /// </summary>
    private static readonly ushort[] RequestOnOpenPropertyCodes =
    [
        0xD115, // Owner
        0xD1D0, // Artist
        0xD1D1, // Copyright
        0xD1AF, // SerialNumber (Canon-specific)
        0xD11B, // AvailableShots
        0xD1AB, // TempStatus
    ];

    internal bool IsOperationSupported(PtpOperationCode opCode) =>
        SupportedOperations.Count == 0 || SupportedOperations.Contains((ushort)opCode);

    /// <summary>
    /// Reads standard PTP device info and properties.
    /// </summary>
    internal async Task RefreshPropertiesAsync(CancellationToken ct = default)
    {
        // GetDeviceInfo (0x1001) — serial number, model, manufacturer, supported operations
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
            var (operations, o0) = ReadPtpUInt16Array(data, offset); // OperationsSupported
            SupportedOperations = operations;
            offset = o0;
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

    private static (HashSet<ushort> Values, int NewOffset) ReadPtpUInt16Array(byte[] data, int offset)
    {
        var values = new HashSet<ushort>();
        if (offset + 4 > data.Length) return (values, offset);
        uint count = BitConverter.ToUInt32(data, offset);
        offset += 4;
        for (uint i = 0; i < count && offset + 2 <= data.Length; i++, offset += 2)
        {
            values.Add(BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset)));
        }
        return (values, offset);
    }

    /// <summary>
    /// Opens a PTP session without Canon remote/event mode.
    /// Standard PTP commands (InitiateCapture) work; Canon vendor commands (RemoteRelease) do not.
    /// </summary>
    internal async Task<EdsError> OpenNoRemoteModeAsync(CancellationToken ct = default)
    {
        var resp = await ptp.SendCommandAsync(PtpOperationCode.OpenSession, ct, SessionId);
        if (resp.IsSuccess)
            await RefreshPropertiesAsync(ct);
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

    internal async Task<EdsError> SetEventModeAsync(uint mode, CancellationToken ct = default)
    {
        var resp = await ptp.SendCommandAsync(PtpOperationCode.CanonSetEventMode, ct, mode);
        return resp.ToEdsError();
    }

    internal async Task<EdsError> CloseAsync(CancellationToken ct = default)
    {
        // Disable remote mode
        await ptp.SendCommandAsync(PtpOperationCode.CanonSetRemoteMode, ct, 0);

        var resp = await ptp.SendCommandAsync(PtpOperationCode.CloseSession, ct);
        Properties.Clear();
        return resp.ToEdsError();
    }

    /// <summary>
    /// Canon SetDevicePropValueEx (0x9110). Data phase is <c>[size:u32][propCode:u32][value:u32]</c>
    /// — the leading size word covers the whole record (12 bytes for a scalar) and the camera
    /// rejects the record without it. Retries while the camera reports DeviceBusy.
    /// </summary>
    internal async Task<EdsError> SetPropertyUInt32Async(ushort ptpPropCode, uint value, CancellationToken ct = default)
    {
        var data = new byte[12];
        BinaryPrimitives.WriteUInt32LittleEndian(data, (uint)data.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(4), ptpPropCode);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(8), value);

        EdsError err = EdsError.DeviceBusy;
        for (int attempt = 0; attempt <= SetPropertyBusyRetries; attempt++)
        {
            if (attempt > 0)
            {
                // Draining pending events is what actually clears the busy state — the camera
                // blocks property writes while its event queue has unread records.
                await DrainEventsAsync(ct);
                await Task.Delay(SetPropertyBusyDelay, ct);
            }

            var resp = await ptp.SendCommandWithDataAsync(PtpOperationCode.CanonSetPropValue, data, ct);
            err = resp.ToEdsError();
            if (err is not EdsError.DeviceBusy) break;
        }

        if (err is EdsError.OK && Properties.TryGetValue(ptpPropCode, out _))
        {
            // Mirror the write immediately; the camera also echoes it back as a PropertyChanged
            // event. Only for properties the camera itself has announced, though: a 450D answers OK
            // to a write of a property it does not have, and mirroring that phantom would make every
            // later read answer the value we invented rather than fail honestly.
            Properties.SetValue(ptpPropCode, value);
        }
        return err;
    }

    /// <summary>
    /// Reads a device property. For EOS vendor properties (0xD1xx/0xD2xx) the value comes from the
    /// event-fed mirror, asking the camera to push it first if we have not seen it yet; there is no
    /// EOS read operation. Non-vendor codes fall through to standard PTP GetDevicePropValue.
    /// </summary>
    internal async Task<(EdsError Error, uint Value)> GetPropertyUInt32Async(ushort ptpPropCode, CancellationToken ct = default)
    {
        if (CanonPropertyCache.IsEosVendorProperty(ptpPropCode))
        {
            if (Properties.TryGetValue(ptpPropCode, out uint cached))
                return (EdsError.OK, cached);

            // Not seen yet: ask the camera to emit it, then drain.
            await RequestDevicePropValueAsync(ptpPropCode, ct);
            await DrainEventsAsync(ct);

            if (Properties.TryGetValue(ptpPropCode, out cached))
                return (EdsError.OK, cached);

            // Older bodies (pre-EOS-vendor-extension PowerShots) do answer 0x1015 for 0xD1xx.
            if (!IsOperationSupported(PtpOperationCode.GetDevicePropValue))
                return (EdsError.DevicePropNotSupported, 0);
        }

        var (resp, data) = await ptp.SendCommandReceiveDataAsync(PtpOperationCode.GetDevicePropValue, ct, ptpPropCode);
        if (!resp.IsSuccess)
            return (resp.ToEdsError(), 0);

        uint value = data.Length switch
        {
            0 => 0,
            1 => data[0],
            2 or 3 => BinaryPrimitives.ReadUInt16LittleEndian(data),
            _ => BinaryPrimitives.ReadUInt32LittleEndian(data),
        };

        Properties.SetValue(ptpPropCode, value);
        return (EdsError.OK, value);
    }

    /// <summary>
    /// Canon RequestDevicePropValue (0x9127) — asks the camera to emit a PropertyChanged event for
    /// this property. Failure is expected for properties the body does not have, so it is not fatal.
    /// </summary>
    internal async Task<EdsError> RequestDevicePropValueAsync(ushort ptpPropCode, CancellationToken ct = default)
    {
        if (!IsOperationSupported(PtpOperationCode.CanonRequestDevicePropValue))
            return EdsError.NotSupported;

        var resp = await ptp.SendCommandAsync(PtpOperationCode.CanonRequestDevicePropValue, ct, ptpPropCode);
        return resp.ToEdsError();
    }

    /// <summary>
    /// Tells the camera how much room the host has, so it will hand images over instead of
    /// reporting zero available shots. Required once capture destination is set to the host.
    /// </summary>
    internal async Task<EdsError> PcHddCapacityAsync(
        uint freeClusters = 0x0FFFFFFF, uint bytesPerSector = 0x1000, uint reset = 1, CancellationToken ct = default)
    {
        var resp = await ptp.SendCommandAsync(PtpOperationCode.CanonPcHddCapacity, ct, freeClusters, bytesPerSector, reset);
        // Busy here is harmless: the value is re-sent before each capture anyway.
        var err = resp.ToEdsError();
        return err is EdsError.DeviceBusy ? EdsError.OK : err;
    }

    /// <summary>Resets the camera's auto-power-off countdown without changing any setting.</summary>
    internal async Task<EdsError> KeepDeviceOnAsync(CancellationToken ct = default)
    {
        var resp = await ptp.SendCommandAsync(PtpOperationCode.CanonKeepDeviceOn, ct);
        return resp.ToEdsError();
    }

    internal async Task<EdsError> SetUILockAsync(bool locked, CancellationToken ct = default)
    {
        var opCode = locked ? PtpOperationCode.CanonSetUILock : PtpOperationCode.CanonResetUILock;
        var resp = await ptp.SendCommandAsync(opCode, ct);
        return resp.ToEdsError();
    }

    /// <summary>
    /// True when the body implements the RemoteReleaseOn/Off press-and-hold pair (0x9128/0x9129).
    /// DIGIC III bodies do not; they offer only the single-shot RemoteRelease (0x910F).
    /// </summary>
    internal bool SupportsRemoteReleasePair =>
        SupportedOperations.Count == 0 || SupportedOperations.Contains((ushort)PtpOperationCode.CanonRemoteReleaseOn);

    /// <summary>
    /// Single-shot release (0x910F) for bodies without the press/hold pair. One command fires the
    /// shutter outright, so there is no half-press stage to model.
    /// </summary>
    internal async Task<EdsError> RemoteReleaseAsync(CancellationToken ct = default)
    {
        var resp = await ptp.SendCommandAsync(PtpOperationCode.CanonRemoteRelease, ct);
        return resp.ToEdsError();
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

    internal async Task<EdsError> AfCancelAsync(CancellationToken ct = default)
    {
        var resp = await ptp.SendCommandAsync(PtpOperationCode.CanonAfCancel, ct);
        return resp.ToEdsError();
    }

    internal async Task<EdsError> ResetMirrorLockupStateAsync(CancellationToken ct = default)
    {
        var resp = await ptp.SendCommandAsync(PtpOperationCode.CanonResetMirrorLockupState, ct);
        return resp.ToEdsError();
    }

    internal async Task<EdsError> BulbStartAsync(CancellationToken ct = default)
    {
        // AF half-press first — but only on bodies that have the on/off release pair. A 450D does
        // not (no 0x9128/0x9129), and the unconditional prelude here used to fail the whole wrapper
        // with NotSupported *before 0x9125 was ever sent* — making bulb look unsupported on a body
        // that advertises it. Discovered live: the "refusal" was ours, not the camera's.
        if (IsOperationSupported(PtpOperationCode.CanonRemoteReleaseOn))
        {
            var err = await RemoteReleaseOnAsync(0x01, ct);
            if (err is not EdsError.OK) return err;
        }

        var resp = await ptp.SendCommandAsync(PtpOperationCode.CanonBulbStart, ct);
        return resp.ToEdsError();
    }

    internal async Task<EdsError> BulbEndAsync(CancellationToken ct = default)
    {
        var resp = await ptp.SendCommandAsync(PtpOperationCode.CanonBulbEnd, ct);
        var err = resp.ToEdsError();

        // Release the AF half-press, on the bodies where BulbStartAsync made one.
        if (IsOperationSupported(PtpOperationCode.CanonRemoteReleaseOn))
        {
            await RemoteReleaseOffAsync(0x01, ct);
        }

        return err;
    }

    internal async Task<EdsError> DriveLensAsync(EdsDriveLensStep step, CancellationToken ct = default)
    {
        var resp = await ptp.SendCommandAsync(PtpOperationCode.CanonDriveLens, ct, (uint)step);
        return resp.ToEdsError();
    }

    /// <summary>
    /// True when the body has the explicit viewfinder start/stop pair (0x9151/0x9152). DIGIC III
    /// bodies expose GetViewFinderData (0x9153) but not these — for them, setting EVFOutputDevice
    /// to PC is the entire start sequence.
    /// </summary>
    internal bool SupportsViewfinderStartStop =>
        SupportedOperations.Count == 0 || SupportedOperations.Contains((ushort)PtpOperationCode.CanonInitiateViewfinder);

    internal async Task<EdsError> InitiateViewfinderAsync(CancellationToken ct = default)
    {
        if (!SupportsViewfinderStartStop) return EdsError.OK;

        var resp = await ptp.SendCommandAsync(PtpOperationCode.CanonInitiateViewfinder, ct);
        return resp.ToEdsError();
    }

    internal async Task<(EdsError Error, byte[] JpegData)> GetViewfinderDataAsync(CancellationToken ct = default)
    {
        var (resp, data) = await ptp.SendCommandReceiveDataAsync(
            PtpOperationCode.CanonGetViewfinderData, ct, 0x00200000, 0, 0);

        // "No frame this poll" is an ordinary part of streaming, not a failure: the body answers
        // ObjectNotReady (0xA102) while its live-view subsystem is still settling, and intermittently
        // afterwards whenever a read lands between frames. Report it the way a caller can act on —
        // an empty frame, poll again — which is also what the COM transport reports for the same
        // condition, since it sees a zero declared size and never gets as far as the response code.
        if (resp.Code is PtpResponseCode.CanonObjectNotReady) return (EdsError.OK, []);

        if (!resp.IsSuccess) return (resp.ToEdsError(), []);

        // The payload is a record envelope, not a bare JPEG — see CanonViewfinderFrame.
        return (EdsError.OK, CanonViewfinderFrame.ExtractJpeg(data));
    }

    internal async Task<EdsError> TerminateViewfinderAsync(CancellationToken ct = default)
    {
        // Symmetric with InitiateViewfinderAsync: on a body without the pair, resetting
        // EVFOutputDevice (which the caller does next) is what actually stops live view.
        if (!SupportsViewfinderStartStop) return EdsError.OK;

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

    /// <summary>Canon CustomFuncEx — the packed custom-function block (libgphoto2 PTP_DPC_CANON_EOS_CustomFuncEx).</summary>
    internal const ushort CustomFuncExPropertyCode = 0xD1A0;

    /// <summary>
    /// Reads the packed Custom Function data block from the camera.
    /// </summary>
    /// <remarks>
    /// Like every other EOS property this arrives on the event stream, so the block is requested and
    /// then read out of the cache's raw bytes. Older non-EOS-vendor bodies that answer 0x1015 are
    /// still probed as a fallback across the 0xD1A0..0xD1A2 range.
    /// </remarks>
    /// <param name="refresh">
    /// Bypass the cache and ask the camera to re-emit the block first. Needed to verify a write:
    /// the camera echoes a written value on the event stream before deciding whether to keep it,
    /// so right after a set the cache can hold a value the camera is about to revert.
    /// </param>
    internal async Task<(EdsError Error, CanonCustomFunctionBlock? Block)> GetCustomFunctionBlockAsync(
        bool refresh = false, CancellationToken ct = default)
    {
        if (refresh || Properties.GetRawValue(CustomFuncExPropertyCode) is not { Length: >= 16 })
        {
            await RequestDevicePropValueAsync(CustomFuncExPropertyCode, ct);
            await DrainEventsAsync(ct);
        }

        var cached = Properties.GetRawValue(CustomFuncExPropertyCode) ?? [];

        if (cached.Length >= 16)
            return (EdsError.OK, CanonCustomFunctionBlock.Parse(cached));

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
        // Same record framing as a scalar write: [size:u32][propcode:u32][payload...]
        // Try 0xD1A0 first.
        ushort cfnPropCode = CustomFuncExPropertyCode;
        var rawData = block.RawData;
        var data = new byte[8 + rawData.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(data, (uint)data.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(4), cfnPropCode);
        rawData.CopyTo(data, 8);

        var resp = await ptp.SendCommandWithDataAsync(PtpOperationCode.CanonSetPropValue, data, ct);
        return resp.ToEdsError();
    }

    /// <summary>
    /// One GetEvent round trip. Decoded events update <see cref="Properties"/> and are published
    /// on <see cref="EventReceived"/>.
    /// </summary>
    internal async Task<IReadOnlyList<CanonPtpEvent>> PollEventsAsync(CancellationToken ct = default)
    {
        var (resp, data) = await ptp.SendCommandReceiveDataAsync(PtpOperationCode.CanonGetEvent, ct);
        if (!resp.IsSuccess)
            return [];

        var events = ParseEvents(data);
        foreach (var evt in events)
        {
            ApplyToCache(evt);
            EventReceived?.Invoke(evt);
        }
        return events;
    }

    /// <summary>
    /// Polls GetEvent until the camera has nothing left to say. The camera reports DeviceBusy for
    /// property writes while records are still queued, so anything that needs an up-to-date
    /// property mirror — or a writable camera — has to drain first.
    /// </summary>
    /// <param name="maxRounds">
    /// Cap on GetEvent round trips. The background poller passes a small value so a chatty camera
    /// (live view announces properties continuously) cannot monopolise the bus for a whole tick;
    /// the session-open drain uses the full budget because that dump genuinely is large.
    /// </param>
    internal async Task<int> DrainEventsAsync(CancellationToken ct = default, int maxRounds = MaxDrainIterations)
    {
        int total = 0;
        for (int i = 0; i < maxRounds; i++)
        {
            var events = await PollEventsAsync(ct);
            if (events.Count == 0) break;
            total += events.Count;
        }
        return total;
    }

    private void ApplyToCache(in CanonPtpEvent evt)
    {
        switch (evt.Type)
        {
            case CanonEventType.PropertyChanged:
                ApplyPropertyValue(evt);
                break;

            case CanonEventType.AllowedValuesChanged:
                ApplyAllowedValues(evt);
                break;
        }
    }

    private void ApplyPropertyValue(in CanonPtpEvent evt)
    {
        // Payload: [propCode:u32][value…]. Canon pads scalars to 4 bytes but a handful of
        // properties report narrower, and composite ones (strings, AF-point structs) report wider —
        // keep their first word, which is all the uint32 surface exposes.
        var payload = evt.Payload.Span;
        if (payload.Length < 4) return;

        var propCode = (ushort)BinaryPrimitives.ReadUInt32LittleEndian(payload);
        var valueBytes = payload[4..];
        if (valueBytes.Length == 0) return;

        uint value = valueBytes.Length switch
        {
            1 => valueBytes[0],
            2 or 3 => BinaryPrimitives.ReadUInt16LittleEndian(valueBytes),
            _ => BinaryPrimitives.ReadUInt32LittleEndian(valueBytes),
        };

        Properties.SetValue(propCode, value, valueBytes.ToArray());
    }

    private void ApplyAllowedValues(in CanonPtpEvent evt)
    {
        // Payload: [propCode:u32][dataType:u32][count:u32][value:u32 × count].
        // Canon pads every element to 4 bytes regardless of the declared data type.
        var payload = evt.Payload.Span;
        if (payload.Length < 12) return;

        uint count = BinaryPrimitives.ReadUInt32LittleEndian(payload[8..]);
        int available = (payload.Length - 12) / 4;
        if (count == 0 || count > available) count = (uint)available;
        if (count == 0) return;

        var values = new uint[count];
        for (int i = 0; i < values.Length; i++)
            values[i] = BinaryPrimitives.ReadUInt32LittleEndian(payload[(12 + i * 4)..]);

        Properties.SetAllowedValues((ushort)evt.Param1, values);
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

            // A record shorter than its header, or longer than what arrived, means the stream is
            // desynchronised — stop rather than walk off into unrelated bytes.
            if (recordLen < 8 || offset + recordLen > (uint)data.Length)
                break;

            int payloadLen = (int)recordLen - 8;

            uint p1 = payloadLen >= 4 ? BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset + 8)) : 0;
            uint p2 = payloadLen >= 8 ? BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset + 12)) : 0;
            uint p3 = payloadLen >= 12 ? BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset + 16)) : 0;

            events.Add(new CanonPtpEvent
            {
                Type = (CanonEventType)eventType,
                Param1 = p1,
                Param2 = p2,
                Param3 = p3,
                Payload = new ReadOnlyMemory<byte>(data, offset + 8, payloadLen),
            });

            offset += (int)recordLen;
        }

        return events;
    }

    public ValueTask DisposeAsync() => ptp.DisposeAsync();
}
