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

    /// <summary>
    /// Standard PTP battery level (0–100%). Read on session open via standard PTP 0x1015/0x5001.
    /// Works on all transports including WPD (no VendorExtID needed).
    /// </summary>
    public byte? BatteryLevelPercent => _canon.BatteryLevelPercent;

    /// <summary>Camera serial number from PTP GetDeviceInfo. Available after session open.</summary>
    public string? SerialNumber => _canon.SerialNumber;

    /// <summary>Camera model name from PTP GetDeviceInfo. Available after session open.</summary>
    public string? Model => _canon.Model;

    /// <summary>
    /// PTP operation codes the camera advertises. Empty until the session is open. Handy when
    /// diagnosing "not supported" errors: EOS bodies omit GetDevicePropValue (0x1015) entirely.
    /// </summary>
    public IReadOnlySet<ushort> SupportedOperations => _canon.SupportedOperations;

    internal CanonCamera(IPtpTransport transport, ILogger<CanonCamera> logger)
    {
        _transport = transport;
        _ptp = new PtpSession(transport);
        _canon = new CanonPtpSession(_ptp);
        _logger = logger;

        // Subscribed for the whole lifetime, not just while the poller runs: the initial session
        // drain and property reads also pull events, and dropping those on the floor would hide
        // ObjectAdded notifications that arrive outside a polling window.
        _canon.EventReceived += OnCanonEvent;
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

    // --- Typed property setters ---

    public Task<EdsError> SetISOAsync(EdsISOSpeed iso, CancellationToken ct = default) =>
        SetPropertyAsync(EdsPropertyId.ISOSpeed, (uint)iso, ct);

    public Task<EdsError> SetShutterSpeedAsync(EdsTv tv, CancellationToken ct = default) =>
        SetPropertyAsync(EdsPropertyId.Tv, (uint)tv, ct);

    public Task<EdsError> SetApertureAsync(EdsAv av, CancellationToken ct = default) =>
        SetPropertyAsync(EdsPropertyId.Av, (uint)av, ct);

    /// <summary>
    /// Sets where captured images go, translating the EDSDK <see cref="EdsSaveTo"/> value to the
    /// Canon PTP CaptureDestination numbering the camera actually expects.
    /// </summary>
    /// <remarks>
    /// Selecting <see cref="EdsSaveTo.Host"/> also reports host free space via PCHDDCapacity
    /// (0x911A). Without that the camera keeps AvailableShots at zero and refuses to release.
    /// </remarks>
    public async Task<EdsError> SetSaveToAsync(EdsSaveTo target, CancellationToken ct = default)
    {
        var (resolveErr, destination) = await ResolveCaptureDestinationAsync(target, ct);
        if (resolveErr is not EdsError.OK) return resolveErr;

        return await SetCaptureDestinationAsync(destination, ct);
    }

    /// <summary>
    /// Sets the Canon PTP CaptureDestination property (0xD11C) directly, using wire values rather
    /// than EDSDK's numbering.
    /// </summary>
    public async Task<EdsError> SetCaptureDestinationAsync(CanonCaptureDestination destination, CancellationToken ct = default)
    {
        var err = await SetPropertyAsync(EdsPropertyId.SaveTo, (uint)destination, ct);
        if (err is not EdsError.OK) return err;

        if (destination is CanonCaptureDestination.Host or CanonCaptureDestination.Both)
        {
            var capacityErr = await _canon.PcHddCapacityAsync(ct: ct);
            if (capacityErr is not EdsError.OK)
                _logger.LogWarning("PCHDDCapacity failed: {Error} — camera may report zero available shots", capacityErr);
        }

        return EdsError.OK;
    }

    /// <summary>
    /// Maps an EDSDK save target onto a wire value, preferring what the camera says it accepts
    /// over a hard-coded number. Only "host" is fixed by the protocol.
    /// </summary>
    private async Task<(EdsError Error, CanonCaptureDestination Destination)> ResolveCaptureDestinationAsync(
        EdsSaveTo target, CancellationToken ct)
    {
        if (target is EdsSaveTo.Host)
            return (EdsError.OK, CanonCaptureDestination.Host);

        var allowed = await GetAllowedValuesAsync(EdsPropertyId.SaveTo, ct);

        if (target is EdsSaveTo.Camera)
        {
            // Anything the body offers that is not "host" is the card slot.
            var cardValue = allowed?.FirstOrDefault(v => v != (uint)CanonCaptureDestination.Host, 0u) ?? 0u;
            return (EdsError.OK, cardValue is 0
                ? CanonCaptureDestination.Card
                : (CanonCaptureDestination)cardValue);
        }

        // Both: no body has been confirmed to accept it, so only use it if this one lists it.
        if (allowed is not null && !allowed.Contains((uint)CanonCaptureDestination.Both))
        {
            _logger.LogWarning(
                "SaveTo=Both is not in the camera's allowed values [{Allowed}] — refusing to guess",
                string.Join(", ", allowed));
            return (EdsError.InvalidParameter, default);
        }
        return (EdsError.OK, CanonCaptureDestination.Both);
    }

    /// <summary>
    /// The values the camera currently accepts for a property, as reported by its
    /// AllowedValuesChanged events. Null when the camera has not described the property — for a
    /// read-only property (AvailableShots, TempStatus) that is normal.
    /// </summary>
    public async Task<uint[]?> GetAllowedValuesAsync(EdsPropertyId id, CancellationToken ct = default)
    {
        if (!CanonPropertyMap.TryGetPtpCode(id, out ushort ptpCode, out _))
            return null;

        var allowed = _canon.Properties.GetAllowedValues(ptpCode);
        if (allowed is not null) return allowed;

        // The description arrives on the event stream like everything else.
        await _canon.DrainEventsAsync(ct);
        return _canon.Properties.GetAllowedValues(ptpCode);
    }

    public Task<EdsError> SetWhiteBalanceAsync(EdsWhiteBalance wb, CancellationToken ct = default) =>
        SetPropertyAsync(EdsPropertyId.WhiteBalance, (uint)wb, ct);

    public Task<EdsError> SetDriveModeAsync(EdsDriveMode mode, CancellationToken ct = default) =>
        SetPropertyAsync(EdsPropertyId.DriveMode, (uint)mode, ct);

    public Task<EdsError> SetMirrorLockupAsync(EdsMirrorUpSetting setting, CancellationToken ct = default) =>
        SetPropertyAsync(EdsPropertyId.MirrorUpSetting, (uint)setting, ct);

    public Task<EdsError> SetAFModeAsync(EdsAFMode mode, CancellationToken ct = default) =>
        SetPropertyAsync(EdsPropertyId.AFMode, (uint)mode, ct);

    public Task<EdsError> SetHighIsoNRAsync(EdsHighIsoNR nr, CancellationToken ct = default) =>
        SetPropertyAsync(EdsPropertyId.NoiseReduction, (uint)nr, ct);

    public Task<EdsError> SetColorTemperatureAsync(uint kelvin, CancellationToken ct = default) =>
        SetPropertyAsync(EdsPropertyId.ColorTemperature, kelvin, ct);

    /// <summary>Sets the auto power-off timeout. Set to 0 to disable (keep camera awake for long sessions).</summary>
    public Task<EdsError> SetAutoPowerOffAsync(uint seconds, CancellationToken ct = default) =>
        SetPropertyAsync(EdsPropertyId.AutoPowerOffSetting, seconds, ct);

    public Task<EdsError> SetEvfDepthOfFieldPreviewAsync(EdsEvfDepthOfFieldPreview preview, CancellationToken ct = default) =>
        SetPropertyAsync(EdsPropertyId.Evf_DepthOfFieldPreview, (uint)preview, ct);

    // --- Typed property getters ---

    public async Task<(EdsError Error, EdsISOSpeed Value)> GetISOAsync(CancellationToken ct = default)
    { var (e, v) = await GetPropertyAsync(EdsPropertyId.ISOSpeed, ct); return (e, (EdsISOSpeed)v); }

    public async Task<(EdsError Error, EdsTv Value)> GetShutterSpeedAsync(CancellationToken ct = default)
    { var (e, v) = await GetPropertyAsync(EdsPropertyId.Tv, ct); return (e, (EdsTv)v); }

    public async Task<(EdsError Error, EdsAv Value)> GetApertureAsync(CancellationToken ct = default)
    { var (e, v) = await GetPropertyAsync(EdsPropertyId.Av, ct); return (e, (EdsAv)v); }

    public async Task<(EdsError Error, EdsAEMode Value)> GetAEModeAsync(CancellationToken ct = default)
    { var (e, v) = await GetPropertyAsync(EdsPropertyId.AEMode, ct); return (e, (EdsAEMode)v); }

    public async Task<(EdsError Error, EdsHighIsoNR Value)> GetHighIsoNRAsync(CancellationToken ct = default)
    { var (e, v) = await GetPropertyAsync(EdsPropertyId.NoiseReduction, ct); return (e, (EdsHighIsoNR)v); }

    public async Task<(EdsError Error, uint Kelvin)> GetColorTemperatureAsync(CancellationToken ct = default) =>
        await GetPropertyAsync(EdsPropertyId.ColorTemperature, ct);

    /// <summary>Number of shots remaining at current quality/card capacity. Read-only.</summary>
    public async Task<(EdsError Error, uint Shots)> GetAvailableShotsAsync(CancellationToken ct = default) =>
        await GetPropertyAsync(EdsPropertyId.AvailableShots, ct);

    /// <summary>Current auto power-off timeout in seconds. 0 = disabled.</summary>
    public async Task<(EdsError Error, uint Seconds)> GetAutoPowerOffAsync(CancellationToken ct = default) =>
        await GetPropertyAsync(EdsPropertyId.AutoPowerOffSetting, ct);

    /// <summary>Sensor/body temperature status. Value encoding is camera-specific.</summary>
    public async Task<(EdsError Error, uint Value)> GetTempStatusAsync(CancellationToken ct = default) =>
        await GetPropertyAsync(EdsPropertyId.TempStatus, ct);

    /// <summary>Current lens name string. Read-only. Returns raw uint — use GetDeviceInfo for string.</summary>
    public async Task<(EdsError Error, uint Value)> GetLensNameRawAsync(CancellationToken ct = default) =>
        await GetPropertyAsync(EdsPropertyId.LensName, ct);

    public async Task<EdsError> TakePictureAsync(CancellationToken ct = default)
    {
        _logger.LogDebug("Taking picture");

        // Clear the event queue first — the camera reports busy and can drop the release if it
        // still has records waiting to be read.
        await _canon.DrainEventsAsync(ct);

        // DIGIC III bodies (450D, 40D, 1000D era) have no RemoteReleaseOn/Off pair — only the
        // single-shot RemoteRelease (0x910F), which fires outright with no half-press stage. Verified
        // on a 450D, whose operation list carries 0x910F and omits 0x9128/0x9129.
        if (!_canon.SupportsRemoteReleasePair)
        {
            _logger.LogDebug("Body has no RemoteReleaseOn (0x9128); using single-shot RemoteRelease (0x910F)");
            var singleShot = await _canon.RemoteReleaseAsync(ct);
            if (singleShot is not EdsError.OK)
                _logger.LogWarning("RemoteRelease failed: {Error}", singleShot);
            return singleShot;
        }

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

    /// <summary>
    /// Starts a bulb exposure. Requires the physical mode dial set to B (Bulb).
    /// Returns <see cref="EdsError.OperationRefused"/> if not in Bulb mode.
    /// Call <see cref="BulbEndAsync"/> to finish the exposure.
    /// </summary>
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

    // --- Custom Function block (older cameras) ---

    /// <summary>
    /// Reads the packed Custom Function data block from the camera.
    /// On older bodies, settings like LENR and mirror lockup live here instead of as direct properties.
    /// Use <see cref="CanonCustomFunctionId"/> for well-known function IDs.
    /// </summary>
    public async Task<(EdsError Error, CanonCustomFunctionBlock? Block)> GetCustomFunctionBlockAsync(CancellationToken ct = default) =>
        await _canon.GetCustomFunctionBlockAsync(ct);

    /// <summary>
    /// Writes a modified Custom Function data block back to the camera.
    /// Read the block first with <see cref="GetCustomFunctionBlockAsync"/>, modify via
    /// <see cref="CanonCustomFunctionBlock.SetValue"/>, then write back.
    /// </summary>
    public Task<EdsError> SetCustomFunctionBlockAsync(CanonCustomFunctionBlock block, CancellationToken ct = default) =>
        _canon.SetCustomFunctionBlockAsync(block, ct);

    /// <summary>
    /// Drives the lens focus motor by the specified step. Requires live view to be active.
    /// </summary>
    public Task<EdsError> DriveLensAsync(EdsDriveLensStep step, CancellationToken ct = default) =>
        _canon.DriveLensAsync(step, ct);

    /// <summary>
    /// Queries the camera for the original filename of a captured object (e.g. "IMG_1234.CR2", "IMG_1234.CR3").
    /// Uses Canon GetObjectInfo (0x9103). Call after receiving an ObjectAdded event.
    /// </summary>
    public async Task<(EdsError Error, string? FileName)> GetObjectFileNameAsync(uint objectHandle, CancellationToken ct = default) =>
        await _canon.GetObjectInfoAsync(objectHandle, ct);

    /// <summary>Downloads the JPEG thumbnail for an object. Much faster than full CR2/CR3 download.</summary>
    public async Task<(EdsError Error, byte[] JpegData)> GetThumbAsync(uint objectHandle, CancellationToken ct = default) =>
        await _canon.GetThumbAsync(objectHandle, ct);

    public Task<EdsError> DownloadAsync(uint objectHandle, Stream destination, CancellationToken ct = default) =>
        _canon.GetObjectAsync(objectHandle, destination, ct);

    public async Task<EdsError> TransferCompleteAsync(uint objectHandle, CancellationToken ct = default) =>
        await _canon.TransferCompleteAsync(objectHandle, ct);

    /// <summary>Cancels an in-progress transfer. Use when a download is stuck or unwanted.</summary>
    public Task<EdsError> CancelTransferAsync(uint objectHandle, CancellationToken ct = default) =>
        _canon.CancelTransferAsync(objectHandle, ct);

    /// <summary>Resets a failed transfer so it can be retried.</summary>
    public Task<EdsError> ResetTransferAsync(uint objectHandle, CancellationToken ct = default) =>
        _canon.ResetTransferAsync(objectHandle, ct);

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

    /// <summary>
    /// Starts the background GetEvent pump. Strongly recommended for the whole session on EOS
    /// bodies: property values only ever arrive through this stream, and an undrained event queue
    /// makes the camera answer <see cref="EdsError.DeviceBusy"/> to property writes.
    /// </summary>
    public void StartEventPolling(TimeSpan? interval = null)
    {
        if (_poller is not null) return;

        _poller = new EventPoller(_canon, interval ?? DefaultPollInterval);
        _poller.Start();
    }

    public async Task StopEventPollingAsync()
    {
        if (_poller is null) return;
        await _poller.DisposeAsync();
        _poller = null;
    }

    /// <summary>
    /// Polls GetEvent until the camera has nothing queued, refreshing the property mirror and
    /// firing the corresponding events. Returns the number of records processed. Call this before
    /// a property write if the background poller is not running.
    /// </summary>
    public Task<int> DrainEventsAsync(CancellationToken ct = default) => _canon.DrainEventsAsync(ct);

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

    /// <summary>
    /// Cancels a running autofocus operation. Useful after a half-press that never resolved.
    /// </summary>
    public Task<EdsError> CancelAutoFocusAsync(CancellationToken ct = default) => _canon.AfCancelAsync(ct);

    /// <summary>
    /// Resets a stuck mirror-lockup state (Canon 0x9130), e.g. after an aborted MLU exposure.
    /// </summary>
    public Task<EdsError> ResetMirrorLockupStateAsync(CancellationToken ct = default) =>
        _canon.ResetMirrorLockupStateAsync(ct);

    /// <summary>
    /// Resets the camera's auto-power-off countdown without changing any setting. Cheaper and less
    /// invasive than <see cref="SetAutoPowerOffAsync"/> for keeping a body awake between exposures.
    /// </summary>
    public Task<EdsError> KeepDeviceOnAsync(CancellationToken ct = default) => _canon.KeepDeviceOnAsync(ct);

    /// <summary>
    /// Locks or unlocks the camera's physical controls. Some bodies need the UI locked before they
    /// accept certain property writes.
    /// </summary>
    public Task<EdsError> SetUILockAsync(bool locked, CancellationToken ct = default) =>
        _canon.SetUILockAsync(locked, ct);

    /// <summary>
    /// Reports host free space to the camera (Canon 0x911A). Sent automatically when the capture
    /// destination is set to host; call it again if a long session drives AvailableShots to zero.
    /// </summary>
    public Task<EdsError> ReportHostCapacityAsync(CancellationToken ct = default) => _canon.PcHddCapacityAsync(ct: ct);

    // --- Diagnostics ---

    /// <summary>
    /// Everything the camera has told us about itself through the event stream: PTP property code,
    /// current value, the mapped <see cref="EdsPropertyId"/> when one exists, and the accepted
    /// values when the camera described them.
    /// </summary>
    /// <remarks>
    /// This is the authoritative view of an EOS body's state — richer than
    /// <see cref="EdsPropertyId"/> covers, because the camera reports many codes the SDK has no
    /// name for yet. Drains pending events first so the snapshot is current.
    /// </remarks>
    public async Task<IReadOnlyList<CanonPropertySnapshot>> DumpPropertiesAsync(CancellationToken ct = default)
    {
        await _canon.DrainEventsAsync(ct);

        return [.. _canon.Properties.Snapshot()
            .Select(p => new CanonPropertySnapshot(
                p.PtpCode,
                CanonPropertyMap.TryGetPropertyId(p.PtpCode),
                p.Value,
                p.AllowedValues))];
    }

    /// <summary>
    /// Reads a raw Canon PTP property code, bypassing the <see cref="EdsPropertyId"/> mapping.
    /// For probing codes the SDK does not map yet.
    /// </summary>
    public Task<(EdsError Error, uint Value)> GetRawPropertyAsync(ushort ptpPropertyCode, CancellationToken ct = default) =>
        _canon.GetPropertyUInt32Async(ptpPropertyCode, ct);

    /// <summary>
    /// Writes a raw Canon PTP property code, bypassing the <see cref="EdsPropertyId"/> mapping.
    /// </summary>
    public Task<EdsError> SetRawPropertyAsync(ushort ptpPropertyCode, uint value, CancellationToken ct = default) =>
        _canon.SetPropertyUInt32Async(ptpPropertyCode, value, ct);

    /// <summary>
    /// Asks the camera to push a property value into the event stream (Canon 0x9127) without
    /// waiting for it. Reads do this on demand; use it directly to pre-warm a batch.
    /// </summary>
    public Task<EdsError> RequestPropertyPushAsync(ushort ptpPropertyCode, CancellationToken ct = default) =>
        _canon.RequestDevicePropValueAsync(ptpPropertyCode, ct);

    /// <summary>Connects the transport without opening a PTP session.</summary>
    public Task ConnectTransportAsync(CancellationToken ct = default) => _transport.ConnectAsync(ct);

    /// <summary>Tests a vendor data-read command. Returns description of result.</summary>
    public async Task<string> TestVendorDataReadAsync(ushort opCode)
    {
        try
        {
            var (resp, data) = await _ptp.SendCommandReceiveDataAsync((PtpOperationCode)opCode, default);
            return $"PTP response=0x{(ushort)resp.Code:X4} dataLen={data.Length}";
        }
        catch (Exception ex)
        {
            return $"Exception: {ex.Message}";
        }
    }

    /// <summary>Tests a standard PTP data-read command with one parameter.</summary>
    public async Task<string> TestStandardDataReadAsync(ushort opCode, uint param)
    {
        try
        {
            var (resp, data) = await _ptp.SendCommandReceiveDataAsync((PtpOperationCode)opCode, default, param);
            if (data.Length > 0)
            {
                var hex = string.Join("", data.Select(b => b.ToString("X2")));
                return $"PTP response=0x{(ushort)resp.Code:X4} dataLen={data.Length} data={hex}";
            }
            return $"PTP response=0x{(ushort)resp.Code:X4} dataLen={data.Length}";
        }
        catch (Exception ex)
        {
            return $"Exception: {ex.Message}";
        }
    }

    /// <summary>Queries WPD MTP EXT supported vendor opcodes or extension description.</summary>
    [SupportedOSPlatform("windows")]
    public string TestWpdMtpExtCommand(uint commandPid)
    {
        if (_transport is not WpdPtpTransport wpd)
            return "Not WPD transport";
        return wpd.TestMtpExtCommand(commandPid);
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
        _canon.EventReceived -= OnCanonEvent;
        await _canon.DisposeAsync();
    }
}

/// <summary>
/// One entry from the camera's device-property state, as reported through the event stream.
/// </summary>
/// <param name="PtpCode">Canon PTP property code, e.g. 0xD103 for ISO.</param>
/// <param name="PropertyId">The mapped EDSDK property ID, or null if the SDK has no name for this code.</param>
/// <param name="Value">Current value. Composite properties (strings, structs) report their first word.</param>
/// <param name="AllowedValues">Values the camera accepts, or null if it never described them.</param>
public sealed record CanonPropertySnapshot(
    ushort PtpCode,
    EdsPropertyId? PropertyId,
    uint Value,
    uint[]? AllowedValues)
{
    public override string ToString()
    {
        var name = PropertyId?.ToString() ?? "(unmapped)";
        var allowed = AllowedValues is null
            ? ""
            : $" allowed=[{string.Join(", ", AllowedValues.Select(v => $"0x{v:X}"))}]";
        return $"0x{PtpCode:X4} {name} = 0x{Value:X8} ({Value}){allowed}";
    }
}
