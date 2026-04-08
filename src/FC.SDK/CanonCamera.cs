using FC.SDK.Canon;
using FC.SDK.Protocol;
using FC.SDK.Transport;
using PtpOperationCode = FC.SDK.Protocol.PtpOperationCode;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.Runtime.Versioning;

namespace FC.SDK;

public sealed class CanonCameraFactory(ILogger<CanonCamera> logger)
{
    public CanonCamera ConnectUsb(UsbDeviceInfo device) =>
        new(new UsbPtpTransport(device), logger);

    public CanonCamera ConnectUsb(ushort vendorId, ushort productId) =>
        new(new UsbPtpTransport(vendorId, productId), logger);

    public CanonCamera ConnectWifi(string host, string? clientName = null) =>
        new(new PtpIpTransport(host, clientName: clientName), logger);

    [SupportedOSPlatform("windows")]
    public CanonCamera ConnectWpd(string wpdDeviceId) =>
        new(new WpdPtpTransport(wpdDeviceId), logger);
}

public sealed class CanonCamera : IAsyncDisposable
{
    private static readonly TimeSpan DefaultPollInterval = TimeSpan.FromMilliseconds(200);

    private readonly IPtpTransport _transport;
    private readonly PtpSession _ptp;
    private readonly CanonPtpSession _canon;
    private readonly ILogger<CanonCamera> _logger;
    private EventPoller? _poller;

    public event EventHandler<CanonPropertyChangedEventArgs>? PropertyChanged;
    public event EventHandler<CanonObjectAddedEventArgs>? ObjectAdded;
    public event EventHandler<CanonStateChangedEventArgs>? StateChanged;

    /// <summary>
    /// A stable device identifier. USB: serial number or device path. WiFi: responder GUID from PTP/IP handshake.
    /// Available after <see cref="OpenSessionAsync"/>.
    /// </summary>
    public string DeviceId => _transport.DeviceId;

    internal CanonCamera(IPtpTransport transport, ILogger<CanonCamera> logger)
    {
        _transport = transport;
        _ptp = new PtpSession(transport);
        _canon = new CanonPtpSession(_ptp);
        _logger = logger;
    }

    public static CanonCamera ConnectUsb(UsbDeviceInfo device) =>
        new(new UsbPtpTransport(device), NullLogger<CanonCamera>.Instance);

    public static CanonCamera ConnectUsb(ushort vendorId, ushort productId) =>
        new(new UsbPtpTransport(vendorId, productId), NullLogger<CanonCamera>.Instance);

    public static CanonCamera ConnectWifi(string host, string? clientName = null) =>
        new(new PtpIpTransport(host, clientName: clientName), NullLogger<CanonCamera>.Instance);

    public static IEnumerable<UsbDeviceInfo> EnumerateUsbCameras() =>
        UsbPtpTransport.Enumerate();

    /// <summary>
    /// Creates a WPD (Windows Portable Devices) connection to a Canon camera.
    /// Uses the stock MTP driver — no WinUSB/Zadig required.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static CanonCamera ConnectWpd(string wpdDeviceId) =>
        new(new WpdPtpTransport(wpdDeviceId), NullLogger<CanonCamera>.Instance);

    /// <summary>
    /// Enumerates Canon cameras visible via WPD (Windows Portable Devices).
    /// Returns PnP device IDs that can be passed to <see cref="ConnectWpd"/>.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static IEnumerable<(string DeviceId, string FriendlyName)> EnumerateWpdCameras()
    {
        foreach (var deviceId in WpdPtpTransport.EnumerateDeviceIds())
        {
            // Filter for Canon USB cameras — exclude printers/scanners (SWD\) by requiring USB prefix
            if (deviceId.Contains("USB", StringComparison.OrdinalIgnoreCase)
                && deviceId.Contains("VID_04A9", StringComparison.OrdinalIgnoreCase))
            {
                var friendlyName = WpdPtpTransport.GetDeviceFriendlyName(deviceId) ?? "Canon Camera";
                yield return (deviceId, friendlyName);
            }
        }
    }

    public async Task<EdsError> OpenSessionAsync(CancellationToken ct = default)
    {
        _logger.LogDebug("Opening PTP session via {Transport}", _transport.GetType().Name);
        await _transport.ConnectAsync(ct);
        var result = await _canon.OpenAsync(ct);
        if (result is EdsError.OK)
        {
            _logger.LogInformation("PTP session opened, DeviceId={DeviceId}", DeviceId);
        }
        else
        {
            _logger.LogError("Failed to open PTP session: {Error}", result);
        }
        return result;
    }

    /// <summary>
    /// Opens a PTP session without enabling Canon remote mode.
    /// Use with <see cref="InitiateCaptureAsync"/> for WPD-friendly capture
    /// where the image is saved to card and WPD events fire normally.
    /// </summary>
    public async Task<EdsError> OpenSessionNoRemoteModeAsync(CancellationToken ct = default)
    {
        _logger.LogDebug("Opening PTP session (no remote mode) via {Transport}", _transport.GetType().Name);
        await _transport.ConnectAsync(ct);
        var result = await _canon.OpenNoRemoteModeAsync(ct);
        if (result is EdsError.OK)
        {
            _logger.LogInformation("PTP session opened (no remote mode), DeviceId={DeviceId}", DeviceId);
        }
        else
        {
            _logger.LogError("Failed to open PTP session: {Error}", result);
        }
        return result;
    }

    /// <summary>
    /// Standard PTP InitiateCapture — camera takes picture using its current settings
    /// and saves to card. Works without Canon remote mode.
    /// </summary>
    public async Task<EdsError> InitiateCaptureAsync(CancellationToken ct = default)
    {
        _logger.LogDebug("InitiateCapture (standard PTP)");
        return await _canon.InitiateCaptureAsync(ct);
    }

    /// <summary>Exits Canon remote mode. WPD can then see new objects on the card.</summary>
    public async Task<EdsError> ExitRemoteModeAsync(CancellationToken ct = default)
    {
        var resp = await _canon.SetRemoteModeAsync(0, ct);
        _logger.LogDebug("ExitRemoteMode: {Result}", resp);
        return resp;
    }

    /// <summary>Re-enters Canon remote mode (needed for shutter/bulb/property control).</summary>
    public async Task<EdsError> EnterRemoteModeAsync(CancellationToken ct = default)
    {
        var resp = await _canon.SetRemoteModeAsync(1, ct);
        _logger.LogDebug("EnterRemoteMode: {Result}", resp);
        return resp;
    }

    public async Task<EdsError> CloseSessionAsync(CancellationToken ct = default)
    {
        _logger.LogDebug("Closing PTP session");
        return await _canon.CloseAsync(ct);
    }

    public async Task<(EdsError Error, uint Value)> GetPropertyAsync(EdsPropertyId id, CancellationToken ct = default)
    {
        var ptpCode = CanonPropertyMap.GetPtpCodeOrThrow(id);
        var (err, value) = await _canon.GetPropertyUInt32Async(ptpCode, ct);
        if (err is not EdsError.OK)
        {
            _logger.LogDebug("GetProperty {PropertyId} failed: {Error}", id, err);
        }
        return (err, value);
    }

    public async Task<EdsError> SetPropertyAsync(EdsPropertyId id, uint value, CancellationToken ct = default)
    {
        var ptpCode = CanonPropertyMap.GetPtpCodeOrThrow(id);
        var result = await _canon.SetPropertyUInt32Async(ptpCode, value, ct);
        if (result is not EdsError.OK)
        {
            _logger.LogWarning("SetProperty {PropertyId}={Value} failed: {Error}", id, value, result);
        }
        return result;
    }

    public async Task<EdsError> TakePictureAsync(CancellationToken ct = default)
    {
        _logger.LogDebug("Taking picture");

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

    public Task<EdsError> BulbStartAsync(CancellationToken ct = default)
    {
        _logger.LogDebug("Bulb start");
        return _canon.BulbStartAsync(ct);
    }

    public Task<EdsError> BulbEndAsync(CancellationToken ct = default)
    {
        _logger.LogDebug("Bulb end");
        return _canon.BulbEndAsync(ct);
    }

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

    /// <summary>Sends a raw PTP no-data command with optional parameters.</summary>
    public async Task<EdsError> SendRawCommandAsync(ushort opCode, params uint[] @params)
    {
        var resp = await _ptp.SendCommandAsync((PtpOperationCode)opCode, default, @params);
        return resp.ToEdsError();
    }

    // --- WPD Content API (hybrid: WPD events + downloads when MTP EXT data-phase fails) ---

    /// <summary>
    /// Whether this camera is connected via WPD. When true, use <see cref="RegisterWpdObjectAddedCallback"/>
    /// instead of <see cref="StartEventPolling"/> for new-image notifications.
    /// </summary>
    public bool IsWpdTransport => _transport is WpdPtpTransport;

    /// <summary>
    /// Registers for WPD object-added events. The callback receives the WPD object ID.
    /// Only works when <see cref="IsWpdTransport"/> is true.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public void RegisterWpdObjectAddedCallback(Action<string> callback)
    {
        if (_transport is WpdPtpTransport wpd)
        {
            wpd.RegisterObjectAddedCallback(callback);
        }
    }

    /// <summary>Unregisters the WPD object-added callback.</summary>
    [SupportedOSPlatform("windows")]
    public void UnregisterWpdObjectAddedCallback()
    {
        if (_transport is WpdPtpTransport wpd)
        {
            wpd.UnregisterObjectAddedCallback();
        }
    }

    /// <summary>
    /// Downloads a WPD object by its object ID to a stream.
    /// Only works when <see cref="IsWpdTransport"/> is true.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public Task DownloadWpdObjectAsync(string objectId, Stream destination, CancellationToken ct = default)
    {
        if (_transport is not WpdPtpTransport wpd)
            throw new InvalidOperationException("Not connected via WPD");
        return wpd.DownloadObjectAsync(objectId, destination, ct);
    }

    /// <summary>Gets the original filename of a WPD object.</summary>
    [SupportedOSPlatform("windows")]
    public string? GetWpdObjectFileName(string objectId)
    {
        return _transport is WpdPtpTransport wpd ? wpd.GetObjectFileName(objectId) : null;
    }

    /// <summary>
    /// Enumerates all objects (files) on the camera via WPD content API.
    /// Returns list of (objectId, fileName) pairs.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public List<(string ObjectId, string? FileName)> EnumerateWpdObjects(bool forceRefresh = false)
    {
        if (_transport is not WpdPtpTransport wpd)
            return [];
        return wpd.EnumerateObjects(forceRefresh);
    }

    public async ValueTask DisposeAsync()
    {
        if (_poller is not null) await _poller.DisposeAsync();
        await _canon.DisposeAsync();
    }
}
