using System.Buffers.Binary;
using FC.SDK.Protocol;

namespace FC.SDK.Canon;

internal sealed class CanonPtpSession(PtpSession ptp) : IAsyncDisposable
{
    private const uint SessionId = 1;
    private const uint RemoteModeStandard = 1;
    private const uint EventModeStandard = 1;

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

        return EdsError.OK;
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
