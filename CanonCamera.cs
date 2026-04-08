using FC.SDK.Canon;
using FC.SDK.Protocol;
using FC.SDK.Transport;

namespace FC.SDK;

public sealed class CanonCamera : IAsyncDisposable
{
    private static readonly TimeSpan DefaultPollInterval = TimeSpan.FromMilliseconds(200);

    private readonly IPtpTransport _transport;
    private readonly PtpSession _ptp;
    private readonly CanonPtpSession _canon;
    private EventPoller? _poller;

    public event EventHandler<CanonPropertyChangedEventArgs>? PropertyChanged;
    public event EventHandler<CanonObjectAddedEventArgs>? ObjectAdded;
    public event EventHandler<CanonStateChangedEventArgs>? StateChanged;

    /// <summary>
    /// A stable device identifier. USB: serial number or device path. WiFi: responder GUID from PTP/IP handshake.
    /// Available after <see cref="OpenSessionAsync"/>.
    /// </summary>
    public string DeviceId => _transport.DeviceId;

    private CanonCamera(IPtpTransport transport)
    {
        _transport = transport;
        _ptp = new PtpSession(transport);
        _canon = new CanonPtpSession(_ptp);
    }

    public static CanonCamera ConnectUsb(UsbDeviceInfo device) =>
        new(new UsbPtpTransport(device));

    public static CanonCamera ConnectUsb(ushort vendorId, ushort productId) =>
        new(new UsbPtpTransport(vendorId, productId));

    public static CanonCamera ConnectWifi(string host, string? clientName = null) =>
        new(new PtpIpTransport(host, clientName: clientName));

    public static IEnumerable<UsbDeviceInfo> EnumerateUsbCameras() =>
        UsbPtpTransport.Enumerate();

    public async Task<EdsError> OpenSessionAsync(CancellationToken ct = default)
    {
        await _transport.ConnectAsync(ct);
        return await _canon.OpenAsync(ct);
    }

    public Task<EdsError> CloseSessionAsync(CancellationToken ct = default) =>
        _canon.CloseAsync(ct);

    public async Task<(EdsError Error, uint Value)> GetPropertyAsync(EdsPropertyId id, CancellationToken ct = default)
    {
        var ptpCode = CanonPropertyMap.GetPtpCodeOrThrow(id);
        return await _canon.GetPropertyUInt32Async(ptpCode, ct);
    }

    public async Task<EdsError> SetPropertyAsync(EdsPropertyId id, uint value, CancellationToken ct = default)
    {
        var ptpCode = CanonPropertyMap.GetPtpCodeOrThrow(id);
        return await _canon.SetPropertyUInt32Async(ptpCode, value, ct);
    }

    public async Task<EdsError> TakePictureAsync(CancellationToken ct = default)
    {
        // Half-press AF
        var err = await _canon.RemoteReleaseOnAsync(0x01, ct);
        if (err is not EdsError.OK) return err;

        // Full press
        err = await _canon.RemoteReleaseOnAsync(0x02, ct);
        if (err is not EdsError.OK) return err;

        // Release shutter
        err = await _canon.RemoteReleaseOffAsync(0x02, ct);
        if (err is not EdsError.OK) return err;

        // Release AF
        return await _canon.RemoteReleaseOffAsync(0x01, ct);
    }

    public Task<EdsError> PressShutterHalfwayAsync(CancellationToken ct = default) =>
        _canon.RemoteReleaseOnAsync(0x01, ct);

    public Task<EdsError> ReleaseShutterAsync(CancellationToken ct = default) =>
        _canon.RemoteReleaseOffAsync(0x01, ct);

    public Task<EdsError> BulbStartAsync(CancellationToken ct = default) =>
        _canon.BulbStartAsync(ct);

    public Task<EdsError> BulbEndAsync(CancellationToken ct = default) =>
        _canon.BulbEndAsync(ct);

    public Task<EdsError> EnableMirrorLockupAsync(CancellationToken ct = default) =>
        SetPropertyAsync(EdsPropertyId.MirrorUpSetting, (uint)EdsMirrorUpSetting.On, ct);

    public Task<EdsError> DisableMirrorLockupAsync(CancellationToken ct = default) =>
        SetPropertyAsync(EdsPropertyId.MirrorUpSetting, (uint)EdsMirrorUpSetting.Off, ct);

    public async Task<(EdsError Error, EdsMirrorLockupState State)> GetMirrorLockupStateAsync(CancellationToken ct = default)
    {
        var (err, val) = await GetPropertyAsync(EdsPropertyId.MirrorLockUpState, ct);
        return (err, (EdsMirrorLockupState)val);
    }

    public async Task<(EdsError Error, EdsMirrorUpSetting Setting)> GetMirrorUpSettingAsync(CancellationToken ct = default)
    {
        var (err, val) = await GetPropertyAsync(EdsPropertyId.MirrorUpSetting, ct);
        return (err, (EdsMirrorUpSetting)val);
    }

    /// <summary>
    /// Drives the lens focus motor by the specified step. Requires live view to be active.
    /// </summary>
    public Task<EdsError> DriveLensAsync(EdsDriveLensStep step, CancellationToken ct = default) =>
        _canon.DriveLensAsync(step, ct);

    public Task<EdsError> DownloadAsync(uint objectHandle, Stream destination, CancellationToken ct = default) =>
        _canon.GetObjectAsync(objectHandle, destination, ct);

    public async Task<EdsError> TransferCompleteAsync(uint objectHandle, CancellationToken ct = default) =>
        await _canon.TransferCompleteAsync(objectHandle, ct);

    public async Task<EdsError> StartLiveViewAsync(CancellationToken ct = default)
    {
        // Set live view output to PC
        var err = await SetPropertyAsync(EdsPropertyId.Evf_OutputDevice, (uint)EdsEvfOutputDevice.PC, ct);
        if (err is not EdsError.OK) return err;

        return await _canon.InitiateViewfinderAsync(ct);
    }

    public async Task<(EdsError Error, byte[] JpegData)> GetLiveViewFrameAsync(CancellationToken ct = default) =>
        await _canon.GetViewfinderDataAsync(ct);

    public async Task<EdsError> StopLiveViewAsync(CancellationToken ct = default)
    {
        var err = await _canon.TerminateViewfinderAsync(ct);

        // Reset live view output
        await SetPropertyAsync(EdsPropertyId.Evf_OutputDevice, (uint)EdsEvfOutputDevice.TFT, ct);

        return err;
    }

    public void StartEventPolling(TimeSpan? interval = null)
    {
        if (_poller is not null) return;

        _poller = new EventPoller(_canon, interval ?? DefaultPollInterval);
        _poller.EventReceived += OnCanonEvent;
        _poller.Start();
    }

    public async Task StopEventPollingAsync()
    {
        if (_poller is null) return;
        _poller.EventReceived -= OnCanonEvent;
        await _poller.DisposeAsync();
        _poller = null;
    }

    private void OnCanonEvent(CanonPtpEvent evt)
    {
        switch (evt.Type)
        {
            case CanonEventType.PropertyChanged:
                PropertyChanged?.Invoke(this, new CanonPropertyChangedEventArgs((EdsPropertyId)evt.Param1, evt.Param2));
                break;

            case CanonEventType.ObjectAddedEx:
            case CanonEventType.ObjectAddedEx64:
                ObjectAdded?.Invoke(this, new CanonObjectAddedEventArgs(evt.Param1));
                break;

            default:
                StateChanged?.Invoke(this, new CanonStateChangedEventArgs(evt.Type, evt.Param1));
                break;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_poller is not null) await _poller.DisposeAsync();
        await _canon.DisposeAsync();
    }
}
