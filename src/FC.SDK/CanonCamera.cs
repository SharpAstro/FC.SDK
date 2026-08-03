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

    /// <inheritdoc cref="CanonCamera.ConnectWpdIoctl(string)"/>
    [SupportedOSPlatform("windows")]
    public CanonCamera ConnectWpdIoctl(string wpdDeviceId) =>
        new(new WpdIoctlPtpTransport(wpdDeviceId), logger);

    /// <inheritdoc cref="CanonCamera.ConnectWpdAutoAsync(string, CancellationToken)"/>
    [SupportedOSPlatform("windows")]
    public Task<CanonCamera> ConnectWpdAutoAsync(string wpdDeviceId, CancellationToken ct = default) =>
        CanonCamera.ConnectWpdAutoCoreAsync(wpdDeviceId, logger, ct);
}

public sealed class CanonCamera : IAsyncDisposable
{
    private static readonly TimeSpan DefaultPollInterval = TimeSpan.FromMilliseconds(200);

    private readonly IPtpTransport _transport;
    private readonly PtpSession _ptp;
    private readonly CanonPtpSession _canon;
    private readonly ILogger<CanonCamera> _logger;
    private EventPoller? _poller;
    private int _disposed;

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
    /// Which transport this camera is connected through, for diagnostics and bug reports. Behaviour
    /// can differ between them, so a report that does not say is a report that has to be re-asked for.
    /// </summary>
    public string TransportName => _transport switch
    {
        WpdPtpTransport => "WPD (COM)",
        WpdIoctlPtpTransport => "WPD (raw ioctl)",
        UsbPtpTransport => "USB (WinUSB/libusb)",
        PtpIpTransport => "PTP/IP (WiFi)",
        var other => other.GetType().Name,
    };

    /// <summary>
    /// Why <see cref="ConnectWpdAutoAsync"/> fell back to the COM transport, or null if it did not
    /// have to — including on every connection made through any other factory.
    /// </summary>
    /// <remarks>
    /// Carried so a device report can say it out loud. "This body is on COM" invites the question
    /// "why", and the answer is only knowable at the moment the probe was rejected.
    /// </remarks>
    public string? TransportFallbackReason { get; private init; }

    /// <summary>
    /// Builds a Markdown description of this body — advertised operations, announced properties and
    /// the decoded Custom Function block. See <see cref="CanonDeviceReport"/> for why it exists and
    /// what a bug reporter should do with it.
    /// </summary>
    public Task<string> CreateDeviceReportAsync(CancellationToken ct = default) =>
        CanonDeviceReport.CreateAsync(this, ct);

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
    /// Creates a connection to the same MTP driver as <see cref="ConnectWpd"/>, but through
    /// <c>DeviceIoControl</c> on one long-lived handle rather than the WPD COM API — the road EDSDK
    /// itself takes.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Worth choosing for live view, though not for the reason you might expect. Frame rate is the
    /// same — a 450D streams 20 fps either way, because the body is the limiter. What differs is
    /// cost: the COM path opens and discards an entire device object per frame (an unfinished
    /// viewfinder transfer poisons the one it used, and its end phase never returns), and that showed
    /// up as 73–119 MiB of private working set against 38–47 MiB here over the same 300 frames.
    /// </para>
    /// <para>
    /// Not a superset. The WPD Content API surface — <see cref="EnumerateWpdObjects"/>,
    /// <see cref="DownloadWpdObjectAsync"/>, <see cref="RegisterWpdObjectAddedCallback"/> — is COM
    /// only; see <see cref="SupportsWpdContentApi"/> for what to use instead. Everything reached over
    /// PTP itself, capture and downloads included, works on both. Verified on an EOS 450D; other
    /// bodies are untried.
    /// </para>
    /// </remarks>
    [SupportedOSPlatform("windows")]
    public static CanonCamera ConnectWpdIoctl(string wpdDeviceId) =>
        new(new WpdIoctlPtpTransport(wpdDeviceId), NullLogger<CanonCamera>.Instance);

    /// <summary>
    /// Connects through <see cref="ConnectWpdIoctl"/> when this driver accepts it, and through
    /// <see cref="ConnectWpd"/> when it does not.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The choice is made once, before any PTP session exists, and never revisited. That is not
    /// timidity — it is the only point where the two are interchangeable. Once
    /// <see cref="OpenSessionAsync"/> has run, state is spread across the transaction counter, the
    /// camera's own session and remote-mode flags, and any open transfer context, which belongs to
    /// the handle and dies with it. Failing over at that point would reconnect into a body that
    /// still believes it is mid-transfer with a client that has gone; recovering from that is a
    /// deliberate reconnect, not something a transport should do behind the caller's back.
    /// </para>
    /// <para>
    /// The probe is a real <c>GetDeviceInfo</c> read rather than a bare connect, because a bare
    /// connect proves almost nothing this fallback exists to guard against — see
    /// <c>WpdIoctlPtpTransport.TryConnectAsync</c>.
    /// </para>
    /// <para>
    /// A fallback is logged at warning level and left on <see cref="TransportFallbackReason"/>, and
    /// <see cref="TransportName"/> always says which road was taken. Silence here would produce bug
    /// reports about live-view memory from people with no way to know they were on COM.
    /// </para>
    /// </remarks>
    [SupportedOSPlatform("windows")]
    public static Task<CanonCamera> ConnectWpdAutoAsync(string wpdDeviceId, CancellationToken ct = default) =>
        ConnectWpdAutoCoreAsync(wpdDeviceId, NullLogger<CanonCamera>.Instance, ct);

    [SupportedOSPlatform("windows")]
    internal static async Task<CanonCamera> ConnectWpdAutoCoreAsync(
        string wpdDeviceId, ILogger<CanonCamera> logger, CancellationToken ct)
    {
        var (ioctl, failure) = await WpdIoctlPtpTransport.TryConnectAsync(wpdDeviceId, ct: ct);
        if (ioctl is not null)
        {
            logger.LogDebug("WPD raw ioctl accepted for {DeviceId}", wpdDeviceId);
            return new CanonCamera(ioctl, logger);
        }

        logger.LogWarning(
            "WPD raw ioctl rejected for {DeviceId} ({Failure}); falling back to the COM transport, "
            + "which costs roughly twice the working set during live view.", wpdDeviceId, failure);

        return new CanonCamera(new WpdPtpTransport(wpdDeviceId), logger) { TransportFallbackReason = failure };
    }

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
        // Before the session goes, not after: a handle only means anything inside it.
        await ReleasePendingTransfersAsync(ct);
        return await _canon.CloseAsync(ct);
    }

    public async Task<(EdsError Error, uint Value)> GetPropertyAsync(EdsPropertyId id, CancellationToken ct = default)
    {
        var ptpCode = CanonPropertyMap.GetPtpCodeOrThrow(id);

        // Refuse rather than answer with the first four bytes of a string read as an integer, which
        // is what this returned before the map carried a type. It was not a visible failure: the
        // call answered OK with a plausible-looking number.
        if (CanonPropertyMap.TypeOf(id) is var type && type is not CanonPropertyType.UInt32)
        {
            _logger.LogWarning(
                "GetProperty {PropertyId} is a {Type} property — use {Alternative} instead", id, type,
                type is CanonPropertyType.String ? nameof(GetPropertyStringAsync) : nameof(GetPropertyBytesAsync));
            return (EdsError.PropertiesMismatch, 0);
        }

        var (err, value) = await _canon.GetPropertyUInt32Async(ptpCode, ct);
        if (err is not EdsError.OK)
        {
            _logger.LogDebug("GetProperty {PropertyId} failed: {Error}", id, err);
        }
        return (err, value);
    }

    /// <summary>
    /// The full value bytes of a property. Needed for anything the uint32 accessor cannot express —
    /// strings and packed structures, where the first word is meaningless.
    /// </summary>
    /// <param name="refresh">
    /// Bypass the mirror and ask the camera to re-emit the value first. Needed to verify a write:
    /// the camera echoes a written value before deciding whether to keep it.
    /// </param>
    public Task<(EdsError Error, byte[] Value)> GetPropertyBytesAsync(
        EdsPropertyId id, bool refresh = false, CancellationToken ct = default) =>
        _canon.GetPropertyBytesAsync(CanonPropertyMap.GetPtpCodeOrThrow(id), refresh, ct);

    /// <summary>Writes a property whose value is not a scalar — a string, or a packed structure.</summary>
    public Task<EdsError> SetPropertyBytesAsync(
        EdsPropertyId id, ReadOnlyMemory<byte> value, CancellationToken ct = default) =>
        _canon.SetPropertyBytesAsync(CanonPropertyMap.GetPtpCodeOrThrow(id), value, ct);

    /// <summary>
    /// Reads a string property — owner, artist, copyright, lens name, body serial.
    /// </summary>
    /// <remarks>
    /// EOS bodies store these as plain null-terminated ASCII, <b>not</b> the PTP length-prefixed
    /// UTF-16 form; see <see cref="CanonPropertyType.String"/> for the corroboration. Trailing bytes
    /// after the terminator are padding and are discarded.
    /// </remarks>
    public async Task<(EdsError Error, string? Value)> GetPropertyStringAsync(
        EdsPropertyId id, bool refresh = false, CancellationToken ct = default)
    {
        var (err, bytes) = await GetPropertyBytesAsync(id, refresh, ct);
        if (err is not EdsError.OK) return (err, null);

        return (EdsError.OK, DecodeAsciiZ(bytes));
    }

    /// <summary>Writes a string property, null-terminated, ASCII.</summary>
    public Task<EdsError> SetPropertyStringAsync(EdsPropertyId id, string value, CancellationToken ct = default)
    {
        // The terminator is part of the value: a body handed an unterminated buffer keeps reading
        // into whatever follows.
        var bytes = new byte[System.Text.Encoding.ASCII.GetByteCount(value) + 1];
        System.Text.Encoding.ASCII.GetBytes(value, bytes);
        return SetPropertyBytesAsync(id, bytes, ct);
    }

    private static string DecodeAsciiZ(ReadOnlySpan<byte> bytes)
    {
        var end = bytes.IndexOf((byte)0);
        return System.Text.Encoding.ASCII.GetString(end < 0 ? bytes : bytes[..end]);
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

    /// <summary>
    /// Enables or disables mirror lockup. Uses the 0xD13A property only when the camera has actually
    /// announced it on the event stream — a 450D answers OK to writes of properties it does not
    /// have, so a bare "try the property, fall back on error" never falls back. Bodies without the
    /// property keep MLU in the Custom Function block; when the body's C.Fn id is known
    /// (<see cref="CanonCustomFunctionId.MirrorLockupIdFor"/>), the setting is written there via
    /// read-modify-write of the whole block, then verified with a fresh read-back.
    /// </summary>
    public async Task<EdsError> SetMirrorLockupAsync(EdsMirrorUpSetting setting, CancellationToken ct = default)
    {
        EdsError err;
        if (IsPropertyAnnounced(EdsPropertyId.MirrorUpSetting))
        {
            err = await SetPropertyAsync(EdsPropertyId.MirrorUpSetting, (uint)setting, ct);
            NoteMirrorLockupSource(customFunction: false);
        }
        else if (CanonCustomFunctionId.MirrorLockupIdFor(Model) is { } cfnId)
        {
            err = await SetCustomFunctionValueAsync(cfnId, (uint)setting, ct);
            NoteMirrorLockupSource(customFunction: true);
        }
        else
        {
            return EdsError.DevicePropNotSupported;
        }

        if (err is EdsError.OK)
        {
            MirrorLockupEnabled = setting is EdsMirrorUpSetting.On;
        }
        return err;
    }

    /// <summary>
    /// Records which of the two homes answered, so <see cref="SupportsMirrorLockupCapture"/> can be
    /// inferred from a write as well as a read — a caller that only ever sets the value should not
    /// have to also read it back before capture knows to refuse.
    /// </summary>
    private void NoteMirrorLockupSource(bool customFunction) => MirrorLockupIsCustomFunction = customFunction;

    /// <summary>
    /// Whether the camera itself has announced this property on the event stream. The write path
    /// must branch on this rather than on a write's response code — a 450D ACKs writes of properties
    /// it does not have.
    /// </summary>
    private bool IsPropertyAnnounced(EdsPropertyId id) =>
        _canon.Properties.TryGetValue(CanonPropertyMap.GetPtpCodeOrThrow(id), out _);

    private async Task<(EdsError Error, uint Value)> GetCustomFunctionValueAsync(uint cfnId, CancellationToken ct)
    {
        var (blockErr, block) = await GetCustomFunctionBlockAsync(ct);
        if (blockErr is not EdsError.OK) return (blockErr, 0);
        return block?.GetValue(cfnId) is { } value ? (EdsError.OK, value) : (EdsError.DevicePropNotSupported, 0);
    }

    /// <summary>Read-modify-write of the whole C.Fn block, verified — see <see cref="VerifyCustomFunctionWriteAsync"/>.</summary>
    private async Task<EdsError> SetCustomFunctionValueAsync(uint cfnId, uint value, CancellationToken ct)
    {
        var (blockErr, block) = await GetCustomFunctionBlockAsync(ct);
        if (blockErr is not EdsError.OK || block is null || !block.SetValue(cfnId, value))
            return blockErr is EdsError.OK ? EdsError.DevicePropNotSupported : blockErr;

        var err = await SetCustomFunctionBlockAsync(block, ct);
        _logger.LogDebug("C.Fn 0x{Id:X4} = {Value}: {Error}", cfnId, value, err);

        if (err is EdsError.OK)
        {
            err = await VerifyCustomFunctionWriteAsync(cfnId, value, ct);
        }
        return err;
    }

    /// <summary>
    /// A C.Fn block write cannot be trusted from its response code alone — the same body answers OK
    /// even to property writes it ignores outright, and gphoto2 has no C.Fn write path to compare
    /// against. So give the camera a moment to apply (or discard) the change and read the block back
    /// fresh, bypassing the cache; a value the camera kept is a real success, anything else is
    /// <see cref="EdsError.OperationRefused"/> (change it in the camera menu instead).
    /// </summary>
    private async Task<EdsError> VerifyCustomFunctionWriteAsync(uint cfnId, uint expected, CancellationToken ct)
    {
        await Task.Delay(CfnRevertWindow, ct);

        var (verifyErr, block) = await _canon.GetCustomFunctionBlockAsync(refresh: true, ct);
        if (verifyErr is not EdsError.OK || block?.GetValue(cfnId) is not { } applied)
            return verifyErr is EdsError.OK ? EdsError.OperationRefused : verifyErr;

        if (applied == expected) return EdsError.OK;

        _logger.LogWarning(
            "C.Fn 0x{Id:X4} write did not stick — the camera reverted to {Value}. This body only changes it via the menu.",
            cfnId, applied);
        return EdsError.OperationRefused;
    }

    /// <summary>How long the camera gets to revert an unaccepted C.Fn write; ~2 s observed on a 450D.</summary>
    private static readonly TimeSpan CfnRevertWindow = TimeSpan.FromSeconds(2.5);

    public Task<EdsError> SetAFModeAsync(EdsAFMode mode, CancellationToken ct = default) =>
        SetPropertyAsync(EdsPropertyId.AFMode, (uint)mode, ct);

    /// <summary>
    /// Sets high-ISO noise reduction. Bodies without the 0xD178 property (450D) keep it in the C.Fn
    /// block as a two-state Off/On, so the four-level value is translated by meaning:
    /// <see cref="EdsHighIsoNR.Disable"/> → Off, anything else → On.
    /// </summary>
    public async Task<EdsError> SetHighIsoNRAsync(EdsHighIsoNR nr, CancellationToken ct = default)
    {
        if (IsPropertyAnnounced(EdsPropertyId.NoiseReduction))
            return await SetPropertyAsync(EdsPropertyId.NoiseReduction, (uint)nr, ct);

        if (CanonCustomFunctionId.HighIsoNrIdFor(Model) is { } cfnId)
            return await SetCustomFunctionValueAsync(cfnId, nr is EdsHighIsoNR.Disable ? 0u : 1u, ct);

        return EdsError.DevicePropNotSupported;
    }

    public Task<EdsError> SetColorTemperatureAsync(uint kelvin, CancellationToken ct = default) =>
        SetPropertyAsync(EdsPropertyId.ColorTemperature, kelvin, ct);

    /// <summary>Sets the auto power-off timeout. Set to 0 to disable (keep camera awake for long sessions).</summary>
    /// <remarks>
    /// <para>
    /// <b>An EOS 6D refuses this outright.</b> It reports 0xD114 fine, announces <i>no</i> allowed
    /// values for it, and answers <see cref="EdsError.DeviceBusy"/> to every write — measured with
    /// the event queue drained, under UILock, and in live view, writing a value that differed from
    /// the one held. So on that body auto power-off is a camera-menu setting and this method cannot
    /// change it.
    /// </para>
    /// <para>
    /// Note the failure is indistinguishable from a transient one by response code alone, which is
    /// how it went unnoticed: a harness logged "AutoPowerOff=off = DeviceBusy" on every run and it
    /// read as noise. To keep a body awake use <see cref="KeepDeviceOnAsync"/> (0x911D), which works.
    /// Only the 6D has been probed; a 450D may well behave differently.
    /// </para>
    /// </remarks>
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

    /// <summary>See <see cref="SetHighIsoNRAsync"/> for the C.Fn translation on 450D-class bodies.</summary>
    public async Task<(EdsError Error, EdsHighIsoNR Value)> GetHighIsoNRAsync(CancellationToken ct = default)
    {
        var (e, v) = await GetPropertyAsync(EdsPropertyId.NoiseReduction, ct);
        if (e is EdsError.OK || CanonCustomFunctionId.HighIsoNrIdFor(Model) is not { } cfnId)
            return (e, (EdsHighIsoNR)v);

        var (cfnErr, cfnValue) = await GetCustomFunctionValueAsync(cfnId, ct);
        return cfnErr is EdsError.OK
            ? (EdsError.OK, cfnValue == 0 ? EdsHighIsoNR.Disable : EdsHighIsoNR.Standard)
            : (e, (EdsHighIsoNR)v);
    }

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

    /// <summary>
    /// Whether the camera can autofocus at all right now, and why not if it cannot.
    /// </summary>
    /// <remarks>
    /// Worth asking before any operation that implies autofocus — a half-press release, or
    /// <see cref="AutoFocusLiveViewAsync"/> — because neither reports the absence of a focus motor
    /// as an error. On a telescope both answer <c>OK</c> and do nothing.
    /// <para>
    /// Verified on an EOS 6D across all three configurations. <see cref="EdsPropertyId.AFMode"/>
    /// (0xD108) tracks the <b>lens's own AF/MF switch</b>, reading <see cref="EdsAFMode.ManualFocus"/>
    /// when it is at MF. Note that value is <i>not</i> in the property's allowed-value list, which
    /// offers only One-Shot / AI Servo / AI Focus — allowed values are what a client may write, not
    /// the values the property can report, and reading the list as the latter is what made this look
    /// undetectable at first. With no lens at all the same property reads One-Shot, so lens presence
    /// has to come from the name.
    /// </para>
    /// </remarks>
    public async Task<(EdsError Error, CanonFocusState State)> GetFocusStateAsync(CancellationToken ct = default)
    {
        var (nameErr, lensName) = await GetLensNameAsync(ct);
        var (modeErr, mode) = await GetPropertyAsync(EdsPropertyId.AFMode, ct);

        var err = nameErr is not EdsError.OK ? nameErr : modeErr;
        return (err, new CanonFocusState(
            LensName: string.IsNullOrWhiteSpace(lensName) ? null : lensName,
            FocusMode: (EdsAFMode)mode));
    }

    /// <summary>The attached lens, as the body names it. Read-only.</summary>
    public Task<(EdsError Error, string? Value)> GetLensNameAsync(CancellationToken ct = default) =>
        GetPropertyStringAsync(EdsPropertyId.LensName, ct: ct);

    /// <summary>
    /// The body serial from property 0xD1AF. <see cref="SerialNumber"/> reports the same thing from
    /// GetDeviceInfo; this is the independent second source.
    /// </summary>
    public Task<(EdsError Error, string? Value)> GetBodyIdAsync(CancellationToken ct = default) =>
        GetPropertyStringAsync(EdsPropertyId.BodyIDEx, ct: ct);


    public async Task<EdsError> TakePictureAsync(CancellationToken ct = default)
    {
        _logger.LogDebug("Taking picture");

        if (MirrorLockupRefusal() is { } refused) return refused;

        // Clear the event queue first — the camera reports busy and can drop the release if it
        // still has records waiting to be read.
        await _canon.DrainEventsAsync(ct);

        // DIGIC III bodies (450D, 40D, 1000D era) have no RemoteReleaseOn/Off pair — only the
        // single-shot RemoteRelease (0x910F), which fires outright with no half-press stage. Verified
        // on a 450D, whose operation list carries 0x910F and omits 0x9128/0x9129.
        if (!_canon.SupportsRemoteReleasePair)
        {
            _logger.LogDebug("Body has no RemoteReleaseOn (0x9128); using single-shot RemoteRelease (0x910F)");
            if (MirrorLockupEnabled == true)
                _logger.LogWarning("Mirror lockup is enabled — a 450D silently ignores RemoteRelease in this mode; expect no exposure");
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
        // Bulb is discarded by mirror lockup exactly as an ordinary release is — verified on a 450D,
        // where an armed body emits no BulbExposureTime ticks at all, so the exposure never begins.
        // Refusing here rather than at BulbEnd keeps a caller from timing a counter against nothing.
        if (MirrorLockupRefusal() is { } refused) return Task.FromResult(refused);
        return _canon.BulbStartAsync(ct);
    }

    public Task<EdsError> BulbEndAsync(CancellationToken ct = default)
    {
        _logger.LogDebug("Bulb end");
        return _canon.BulbEndAsync(ct);
    }

    public Task<EdsError> EnableMirrorLockupAsync(CancellationToken ct = default) =>
        SetMirrorLockupAsync(EdsMirrorUpSetting.On, ct);

    public Task<EdsError> DisableMirrorLockupAsync(CancellationToken ct = default) =>
        SetMirrorLockupAsync(EdsMirrorUpSetting.Off, ct);

    /// <summary>
    /// Last known mirror-lockup setting, from whichever source answered — the 0xD13A property or the
    /// C.Fn block. Null until <see cref="GetMirrorUpSettingAsync"/> or a set has run.
    /// </summary>
    /// <remarks>
    /// Also the "can I even shoot remotely?" signal on C.Fn bodies: a 450D silently ignores
    /// RemoteRelease (0x910F) while mirror lockup is enabled — the command answers OK, no mirror
    /// moves, no event is emitted, no exposure happens. Remote MLU capture is not possible there;
    /// disable lockup for tethered shooting.
    /// </remarks>
    public bool? MirrorLockupEnabled { get; private set; }

    /// <summary>
    /// Current mirror-lockup state. Bodies without the 0xD1BF property cannot report the mirror's
    /// actual position; they fall back to
    /// <see cref="EdsMirrorLockupState.Enable"/>/<see cref="EdsMirrorLockupState.Disable"/> derived
    /// from the setting, wherever that setting happens to live. No press-counting inference: on a
    /// 450D remote releases do nothing at all while lockup is on (see
    /// <see cref="MirrorLockupEnabled"/>), so a guessed "mirror up" would be wrong.
    /// </summary>
    /// <remarks>
    /// Whether a body keeps mirror lockup as a property or as a Custom Function is not the caller's
    /// problem, so this resolves the setting itself rather than reporting failure and leaving them to
    /// discover that <see cref="GetMirrorUpSettingAsync"/> had to be called first. It used to only
    /// consult the cache, which is null until something else populates it — so on every C.Fn body
    /// (a 450D, for one) the obvious call answered <see cref="EdsError.DevicePropNotSupported"/> and
    /// looked like the feature was missing.
    /// </remarks>
    public async Task<(EdsError Error, EdsMirrorLockupState State)> GetMirrorLockupStateAsync(CancellationToken ct = default)
    {
        var (err, val) = await GetPropertyAsync(EdsPropertyId.MirrorLockUpState, ct);
        if (err is EdsError.OK) return (err, (EdsMirrorLockupState)val);

        if (MirrorLockupEnabled is null)
        {
            // Populates MirrorLockupEnabled from the property or the C.Fn block, whichever answers.
            var (settingErr, _) = await GetMirrorUpSettingAsync(ct);
            if (settingErr is not EdsError.OK) return (settingErr, (EdsMirrorLockupState)val);
        }

        return MirrorLockupEnabled switch
        {
            true => (EdsError.OK, EdsMirrorLockupState.Enable),
            false => (EdsError.OK, EdsMirrorLockupState.Disable),
            null => (err, (EdsMirrorLockupState)val),
        };
    }

    /// <summary>
    /// Current mirror-lockup setting. Tries the 0xD13A property first, then the C.Fn block on bodies
    /// whose id is known — see <see cref="SetMirrorLockupAsync"/>.
    /// </summary>
    public async Task<(EdsError Error, EdsMirrorUpSetting Setting)> GetMirrorUpSettingAsync(CancellationToken ct = default)
    {
        var (err, val) = await GetPropertyAsync(EdsPropertyId.MirrorUpSetting, ct);
        if (err is EdsError.OK) MirrorLockupIsCustomFunction = false;

        if (err is not EdsError.OK && CanonCustomFunctionId.MirrorLockupIdFor(Model) is { } cfnId)
        {
            var (cfnErr, cfnValue) = await GetCustomFunctionValueAsync(cfnId, ct);
            if (cfnErr is EdsError.OK)
            {
                (err, val) = (EdsError.OK, cfnValue);
                MirrorLockupIsCustomFunction = true;
            }
        }

        if (err is EdsError.OK) MirrorLockupEnabled = val != 0;
        return (err, (EdsMirrorUpSetting)val);
    }

    /// <summary>
    /// Where this body keeps its mirror-lockup setting: <c>true</c> for the Custom Function block,
    /// <c>false</c> for the <see cref="EdsPropertyId.MirrorUpSetting"/> property, null until one of
    /// them has answered.
    /// </summary>
    /// <remarks>
    /// Also the best available predictor of whether remote capture works at all with lockup armed —
    /// see <see cref="SupportsMirrorLockupCapture"/>.
    /// </remarks>
    public bool? MirrorLockupIsCustomFunction { get; private set; }

    /// <summary>
    /// Whether a remote release can be expected to produce an exposure while mirror lockup is armed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// There is no capability bit for this, so it is inferred from where the setting lives: bodies
    /// that keep mirror lockup as a Custom Function are the older ones, and on those the firmware
    /// discards remote releases entirely while it is armed. Measured on a 450D across nine sequences
    /// — single release, double release, three self-timer drive modes, and four bulb arrangements —
    /// every one silent, against controls that exposed. A 6D, which has the real
    /// <see cref="EdsPropertyId.MirrorUpSetting"/> property, exposes normally instead. NINA draws the
    /// same line and refuses with the same advice ("turn MLU off under the camera's Custom Function
    /// menu"), which is independent corroboration from an EDSDK-based client.
    /// </para>
    /// <para>
    /// It is a heuristic over two measured bodies, not a documented rule, so it is overridable: set
    /// it explicitly to force capture through on a body where it is wrong. Erring toward refusal is
    /// deliberate — the alternative is what this SDK used to do, which is answer OK to a release the
    /// camera silently threw away and leave the caller waiting for an image that never comes.
    /// </para>
    /// </remarks>
    public bool SupportsMirrorLockupCapture
    {
        get => _supportsMirrorLockupCapture ?? MirrorLockupIsCustomFunction is not true;
        set => _supportsMirrorLockupCapture = value;
    }

    private bool? _supportsMirrorLockupCapture;

    /// <summary>
    /// The refusal shared by every capture entry point, or null when the release should go ahead.
    /// </summary>
    private EdsError? MirrorLockupRefusal()
    {
        if (MirrorLockupEnabled is not true || SupportsMirrorLockupCapture) return null;

        _logger.LogError(
            "{Model} is configured with mirror lockup on, but it does not support mirror-lockup "
            + "exposures over PTP: the camera answers OK and then does nothing at all — no mirror, no "
            + "event, no image. Turn mirror lockup off (Custom Function menu) for exposures to "
            + "succeed, or set SupportsMirrorLockupCapture if this body is an exception.",
            Model ?? "This camera");
        return EdsError.OperationRefused;
    }

    // --- Custom Function block (older cameras) ---

    /// <summary>
    /// Reads the packed Custom Function data block from the camera.
    /// On older bodies, settings like LENR and mirror lockup live here instead of as direct properties.
    /// Use <see cref="CanonCustomFunctionId"/> for well-known function IDs.
    /// </summary>
    public async Task<(EdsError Error, CanonCustomFunctionBlock? Block)> GetCustomFunctionBlockAsync(CancellationToken ct = default) =>
        await _canon.GetCustomFunctionBlockAsync(refresh: false, ct);

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

    public async Task<EdsError> TransferCompleteAsync(uint objectHandle, CancellationToken ct = default)
    {
        lock (_pendingHostTransfers) _pendingHostTransfers.Remove(objectHandle);
        return await _canon.TransferCompleteAsync(objectHandle, ct);
    }

    /// <summary>
    /// Handles announced by <see cref="CanonEventType.RequestObjectTransfer"/> that nobody has
    /// finished with yet — frames the body is holding in its own RAM on our behalf.
    /// </summary>
    /// <remarks>
    /// Only the host-destination families are tracked. A card-destination frame belongs to the card
    /// and costs the body nothing to leave alone; a host-destination one occupies it until
    /// <see cref="TransferCompleteAsync"/> arrives, and enough of them will stop it releasing at all.
    /// </remarks>
    private readonly HashSet<uint> _pendingHostTransfers = [];

    /// <summary>
    /// Hands back every frame the body is still holding for us. Best-effort and never throws: this
    /// runs on the way out, where the useful outcome is a camera left able to shoot.
    /// </summary>
    private async Task ReleasePendingTransfersAsync(CancellationToken ct)
    {
        uint[] pending;
        lock (_pendingHostTransfers)
        {
            if (_pendingHostTransfers.Count is 0) return;
            pending = [.. _pendingHostTransfers];
            _pendingHostTransfers.Clear();
        }

        _logger.LogWarning(
            "Closing with {Count} host-destination frame(s) the camera is still holding; releasing them. "
            + "A caller that takes an ObjectAdded handle must call TransferCompleteAsync when done — "
            + "leaving them makes the body answer DeviceBusy to later releases.", pending.Length);

        foreach (var handle in pending)
        {
            try
            {
                await _canon.TransferCompleteAsync(handle, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "TransferComplete for orphaned handle 0x{Handle:X8} failed", handle);
            }
        }
    }

    /// <summary>Cancels an in-progress transfer. Use when a download is stuck or unwanted.</summary>
    public Task<EdsError> CancelTransferAsync(uint objectHandle, CancellationToken ct = default) =>
        _canon.CancelTransferAsync(objectHandle, ct);

    /// <summary>Resets a failed transfer so it can be retried.</summary>
    public Task<EdsError> ResetTransferAsync(uint objectHandle, CancellationToken ct = default) =>
        _canon.ResetTransferAsync(objectHandle, ct);

    public async Task<EdsError> StartLiveViewAsync(CancellationToken ct = default)
    {
        // Mirrors libgphoto2's camera_capture_preview order for EOS bodies. Two steps beyond just
        // "output to PC":
        // - EVF mode must be 1 first, set only when it is not already (a redundant set costs a
        //   DeviceBusy round trip).
        // - KeepDeviceOn afterwards — gphoto2: "Otherwise the camera will auto-shutdown". A body
        //   whose live-view subsystem powers down mid-stream does not answer GetViewFinderData with
        //   an error, it stops answering entirely and takes the USB session with it.
        var (modeErr, mode) = await GetPropertyAsync(EdsPropertyId.Evf_Mode, ct);
        if (modeErr is EdsError.OK && mode != 1)
        {
            var setMode = await SetPropertyAsync(EdsPropertyId.Evf_Mode, 1, ct);
            if (setMode is not EdsError.OK and not EdsError.DeviceBusy) return setMode;
        }

        var err = await SetPropertyAsync(EdsPropertyId.Evf_OutputDevice, (uint)EdsEvfOutputDevice.PC, ct);
        if (err is not EdsError.OK) return err;

        await _canon.KeepDeviceOnAsync(ct);

        return await _canon.InitiateViewfinderAsync(ct);
    }

    public async Task<(EdsError Error, byte[] JpegData)> GetLiveViewFrameAsync(CancellationToken ct = default) =>
        await _canon.GetViewfinderDataAsync(ct);

    /// <summary>
    /// Every record in a live-view frame, image and undecoded metadata alike. For identifying what
    /// the body puts alongside the picture — the zoom rect, focus points, histogram.
    /// </summary>
    public async Task<(EdsError Error, IReadOnlyList<CanonViewfinderRecord> Records)> GetLiveViewRecordsAsync(
        CancellationToken ct = default)
    {
        var (err, envelope) = await _canon.GetViewfinderEnvelopeAsync(ct);
        return err is EdsError.OK
            ? (EdsError.OK, CanonViewfinderFrame.ParseRecords(envelope))
            : (err, []);
    }

    // --- Live view magnification ---
    //
    // Zoom is the DSLR planetary/focusing regime: at 5x or 10x the live feed is a near-1:1-pixel
    // crop of a small sensor region rather than a downscaled whole frame. Both of these are
    // *operations* (0x9158 / 0x9159), not property writes — EDSDK models them as the properties
    // Evf_Zoom (0x507) and Evf_ZoomPosition (0x508), and no such PTP property exists.

    /// <summary>True when the body advertises live-view magnification (0x9158).</summary>
    public bool SupportsEvfZoom => _canon.SupportsEvfZoom;

    /// <summary>True when the body advertises panning the magnified crop (0x9159).</summary>
    public bool SupportsEvfZoomPosition => _canon.SupportsEvfZoomPosition;

    /// <summary>True when the body advertises live-view autofocus (0x9154).</summary>
    public bool SupportsLiveViewAutoFocus => _canon.SupportsDoAf;

    /// <summary>
    /// Sets the live-view magnification. Live view must already be running.
    /// </summary>
    public Task<EdsError> SetEvfZoomAsync(
        CanonEvfZoom zoom, bool verify = true, CancellationToken ct = default) =>
        SetEvfZoomAsync((uint)zoom, verify, ct);

    /// <summary>
    /// Sets the live-view magnification to a raw value, for a body that offers a factor
    /// <see cref="CanonEvfZoom"/> does not name.
    /// </summary>
    /// <param name="verify">
    /// Read the zoom rect back and answer <see cref="EdsError.OperationRefused"/> when the camera
    /// took no notice. On by default: this operation is one of the ones that ACKs unconditionally,
    /// and the caller cannot otherwise tell. Costs up to a second of frame polling. Turn it off for
    /// a body already known to honour zoom, or when live view is not streaming.
    /// </param>
    /// <remarks>
    /// A 6D measured with the factor as a <b>threshold</b> rather than an exact value: 1–4 give 1×,
    /// 5–8 give 5×, 10 and above give 10×. Ask for what you want and read
    /// <see cref="GetEvfZoomRectAsync"/> for what you got.
    /// </remarks>
    public async Task<EdsError> SetEvfZoomAsync(uint zoom, bool verify = true, CancellationToken ct = default)
    {
        var err = await _canon.EvfZoomAsync(zoom, ct);
        if (err is not EdsError.OK)
        {
            _logger.LogWarning("SetEvfZoom({Zoom}) failed: {Error}", zoom, err);
            return err;
        }

        if (!verify) return EdsError.OK;

        // Wait for the rect to REACH the expected state rather than sampling one frame: the body
        // takes about a second to apply a zoom and keeps streaming pre-zoom frames meanwhile, so a
        // single read reliably catches the old rect and calls a working zoom refused.
        var want = zoom > 1;
        var rect = await WaitForZoomRectAsync(r => r.IsMagnified == want, ZoomSettleTimeout, ct);

        if (rect is null)
        {
            // No frame arrived, so there is no evidence either way. Refusing here would be the
            // mistake this whole method exists to prevent, one level up.
            _logger.LogDebug("SetEvfZoom({Zoom}): no live-view frame to verify against", zoom);
            return EdsError.OK;
        }

        if (zoom > 1 && !rect.Value.IsMagnified)
        {
            var (_, afMode) = await GetPropertyAsync(EdsPropertyId.Evf_AFMode, ct);
            _logger.LogError(
                "{Model} accepted zoom {Zoom} and stayed at full frame ({Rect}). The known cause is "
                + "the live-view AF method: Evf_AFMode is {AfMode}, and {Blocking} silently disables "
                + "magnification on a body with a lens attached. Try {Alternative}.",
                Model ?? "The camera", zoom, rect.Value, (CanonEvfAfSystem)afMode,
                nameof(CanonEvfAfSystem.LiveFace), nameof(CanonEvfAfSystem.Live));
            return EdsError.OperationRefused;
        }

        return EdsError.OK;
    }

    /// <summary>
    /// The region of the sensor live view is currently showing, as the body reports it — the only
    /// trustworthy account of whether a zoom or pan actually took effect. Null when no frame
    /// arrives, or when the body does not describe its zoom rect.
    /// </summary>
    /// <param name="ct">Cancellation.</param>
    public Task<CanonEvfZoomRect?> GetEvfZoomRectAsync(CancellationToken ct = default) =>
        WaitForZoomRectAsync(_ => true, ZoomReadTimeout, ct);

    /// <summary>
    /// The camera's own exposure histogram for the current live-view frame — the only live metering
    /// an EOS offers over PTP. Null when no frame arrives in time, or when the body sends no
    /// histogram record.
    /// </summary>
    /// <remarks>
    /// Requires live view to be running. There is no PTP operation that reports a metered exposure
    /// value, so on a body whose dial is at <see cref="EdsAEMode.Manual"/> — where nothing
    /// compensates for a bad guess — this is how to find out whether a frame is exposed without
    /// spending a shutter actuation to look.
    /// </remarks>
    /// <param name="ct">Cancellation.</param>
    public async Task<CanonEvfHistogram?> GetEvfHistogramAsync(CancellationToken ct = default)
    {
        // Same reason WaitForZoomRectAsync polls: roughly half of all live-view reads answer
        // "no frame yet" on the bodies measured, so one read is not a reading.
        var deadline = DateTime.UtcNow + ZoomReadTimeout;
        while (true)
        {
            var (err, records) = await GetLiveViewRecordsAsync(ct);
            if (err is EdsError.OK && records.Count > 0
                && CanonViewfinderFrame.TryGetHistogram([.. records]) is { } histogram)
                return histogram;

            if (DateTime.UtcNow >= deadline) return null;
            await Task.Delay(50, ct);
        }
    }

    /// <summary>How long a zoom change is given to show up in the rect before it counts as ignored.</summary>
    private static readonly TimeSpan ZoomSettleTimeout = TimeSpan.FromSeconds(3);

    /// <summary>Budget for simply reading the current rect — enough to outlast a few empty polls.</summary>
    private static readonly TimeSpan ZoomReadTimeout = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Polls live view until the zoom rect satisfies <paramref name="settled"/>, returning the last
    /// rect seen if it never does. Roughly half of all live-view reads answer "no frame yet" on the
    /// bodies measured, so any single read is unreliable on its own.
    /// </summary>
    private async Task<CanonEvfZoomRect?> WaitForZoomRectAsync(
        Func<CanonEvfZoomRect, bool> settled, TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        CanonEvfZoomRect? last = null;

        while (true)
        {
            var (err, records) = await GetLiveViewRecordsAsync(ct);
            if (err is EdsError.OK && records.Count > 0
                && CanonViewfinderFrame.TryGetZoomRect([.. records]) is { } rect)
            {
                last = rect;
                if (settled(rect)) return rect;
            }

            if (DateTime.UtcNow >= deadline) return last;
            await Task.Delay(100, ct);
        }
    }

    /// <summary>The live-view AF method. Gates magnification — see <see cref="CanonEvfAfSystem"/>.</summary>
    public async Task<(EdsError Error, CanonEvfAfSystem Value)> GetEvfAfSystemAsync(CancellationToken ct = default)
    {
        var (err, value) = await GetPropertyAsync(EdsPropertyId.Evf_AFMode, ct);
        return (err, (CanonEvfAfSystem)value);
    }

    /// <summary>
    /// Sets the live-view AF method. Set this to <see cref="CanonEvfAfSystem.Live"/> before zooming:
    /// the factory default on at least a 6D is <see cref="CanonEvfAfSystem.LiveFace"/>, which
    /// silently blocks magnification.
    /// </summary>
    public Task<EdsError> SetEvfAfSystemAsync(CanonEvfAfSystem system, CancellationToken ct = default) =>
        SetPropertyAsync(EdsPropertyId.Evf_AFMode, (uint)system, ct);

    /// <summary>
    /// Moves the magnified crop across the sensor.
    /// </summary>
    /// <remarks>
    /// Coordinates are the body's own sensor-coordinate space, and the units differ across
    /// generations — libgphoto2 measured "approx 64 pixel steps on the EOS 1000D". Treat a value as
    /// calibrated only against the zoom rect the viewfinder envelope reports on the same body; do
    /// not carry a constant between models. Has no visible effect unless zoom is above 1×.
    /// </remarks>
    /// <param name="verify">
    /// Wait for the move to appear in the zoom rect and return where it landed. On by default: the
    /// body streams pre-move frames for up to a second afterwards, so an immediate read of the rect
    /// reports the <i>previous</i> position — which looks exactly like a pan that did not work.
    /// </param>
    public async Task<(EdsError Error, CanonEvfZoomRect? Landed)> SetEvfZoomPositionAsync(
        uint x, uint y, bool verify = true, CancellationToken ct = default)
    {
        var before = verify ? await GetEvfZoomRectAsync(ct) : null;

        // Clamp into the range the body will even look at. Measured on a 6D: a coordinate up to
        // (sensor - crop) is accepted and then clamped inwards by the body itself, but anything
        // beyond that is DISCARDED — the axis silently keeps its previous value, which reads exactly
        // like a pan that did not work. Asking for "the far corner" with a large number therefore
        // moves nothing at all, so the far corner is computed here instead.
        if (before is { IsMagnified: true } b)
        {
            var (maxX, maxY) = (b.SensorWidth - b.Width, b.SensorHeight - b.Height);
            if (x > maxX || y > maxY)
            {
                _logger.LogDebug(
                    "SetEvfZoomPosition({X},{Y}) is outside the pannable range; asking for ({CX},{CY})",
                    x, y, Math.Min(x, maxX), Math.Min(y, maxY));
                (x, y) = (Math.Min(x, maxX), Math.Min(y, maxY));
            }
        }

        var err = await _canon.EvfZoomPositionAsync(x, y, ct);
        if (err is not EdsError.OK)
        {
            _logger.LogWarning("SetEvfZoomPosition({X},{Y}) failed: {Error}", x, y, err);
            return (err, null);
        }

        if (!verify) return (EdsError.OK, null);

        // Settled means either the exact coordinate asked for, or — when the camera clamped, which
        // it does silently and by an amount that cannot be predicted without knowing the crop size —
        // any origin other than the one it started from.
        var landed = await WaitForZoomRectAsync(
            r => (r.X == x && r.Y == y) || before is not { } b || r.X != b.X || r.Y != b.Y,
            ZoomSettleTimeout, ct);

        if (landed is { } l && (l.X != x || l.Y != y))
            _logger.LogDebug("SetEvfZoomPosition({X},{Y}) clamped to ({LX},{LY})", x, y, l.X, l.Y);

        return (EdsError.OK, landed);
    }

    /// <summary>
    /// Runs contrast-detect autofocus on the live-view image (0x9154).
    /// </summary>
    /// <remarks>
    /// The live-view counterpart to the mirror-path half-press: <see cref="PressShutterHalfwayAsync"/>
    /// drives the phase-detect sensor, which is blind while the mirror is up for live view. Returns
    /// when the camera accepts the command, not when focus is achieved — watch the event stream, and
    /// <see cref="CancelAutoFocusAsync"/> to abort a hunt.
    /// </remarks>
    public async Task<EdsError> AutoFocusLiveViewAsync(CancellationToken ct = default)
    {
        if (!_canon.SupportsDoAf)
        {
            _logger.LogWarning("{Model} does not advertise live-view autofocus (0x9154)", Model ?? "This camera");
            return EdsError.NotSupported;
        }

        var err = await _canon.DoAfAsync(ct);
        if (err is not EdsError.OK)
            _logger.LogWarning("AutoFocusLiveView failed: {Error}", err);
        return err;
    }

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

    /// <summary>
    /// True for every event family that means "an image exists and here is its handle".
    /// </summary>
    /// <remarks>
    /// Which one a body sends is decided by the capture destination, and a caller waiting for a
    /// picture cannot act on the difference: <see cref="CanonEventType.ObjectAddedEx"/> announces a
    /// frame written to the card, <see cref="CanonEventType.RequestObjectTransfer"/> one held in the
    /// body's RAM for the host to fetch. EDSDK raises the same pair
    /// (<c>kEdsObjectEvent_DirItemCreated</c> / <c>kEdsObjectEvent_DirItemRequestTransfer</c>).
    /// <para>
    /// Handling only the card family is what made capture-to-host look like a body that silently
    /// refused to release: on a 450D every exposure really happened and it was the announcement that
    /// got dropped here. Worse than a missed callback — an un-fetched frame occupies the body until
    /// someone calls <see cref="TransferCompleteAsync"/>, so two dropped events were enough to make
    /// it answer <see cref="EdsError.DeviceBusy"/> to every subsequent release and refuse to power
    /// off. A "dead" camera and a whole retracted conclusion about mirror lockup came out of this.
    /// </para>
    /// </remarks>
    internal static bool AnnouncesNewImage(CanonEventType type) => type is
        CanonEventType.ObjectAddedEx or CanonEventType.ObjectAddedEx64
        or CanonEventType.RequestObjectTransfer or CanonEventType.RequestObjectTransfer64;

    private void OnCanonEvent(CanonPtpEvent evt)
    {
        if (evt.Type is CanonEventType.PropertyChanged)
            PropertyChanged?.Invoke(this, new CanonPropertyChangedEventArgs((EdsPropertyId)evt.Param1, evt.Param2));
        else if (AnnouncesNewImage(evt.Type))
        {
            if (evt.Type is CanonEventType.RequestObjectTransfer or CanonEventType.RequestObjectTransfer64)
                lock (_pendingHostTransfers) _pendingHostTransfers.Add(evt.Param1);
            ObjectAdded?.Invoke(this, new CanonObjectAddedEventArgs(evt.Param1));
        }
        else
            StateChanged?.Invoke(this, new CanonStateChangedEventArgs(evt.Type, evt.Param1));
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
    /// The values the camera accepts for a raw PTP property code. The raw counterpart to
    /// <see cref="GetAllowedValuesAsync"/> — for a code with no <see cref="EdsPropertyId"/> mapping,
    /// which is where guessing a value space is most tempting and least safe.
    /// </summary>
    public async Task<uint[]?> GetRawAllowedValuesAsync(ushort ptpPropertyCode, CancellationToken ct = default)
    {
        if (_canon.Properties.GetAllowedValues(ptpPropertyCode) is { } known) return known;

        await _canon.RequestDevicePropValueAsync(ptpPropertyCode, ct);
        await _canon.DrainEventsAsync(ct);
        return _canon.Properties.GetAllowedValues(ptpPropertyCode);
    }

    /// <summary>
    /// Reads the full value bytes of a raw Canon PTP property code, bypassing the
    /// <see cref="EdsPropertyId"/> mapping. The byte-level counterpart to
    /// <see cref="GetRawPropertyAsync"/>, for probing a code whose layout is not known yet.
    /// </summary>
    public Task<(EdsError Error, byte[] Value)> GetRawPropertyBytesAsync(
        ushort ptpPropertyCode, bool refresh = false, CancellationToken ct = default) =>
        _canon.GetPropertyBytesAsync(ptpPropertyCode, refresh, ct);

    /// <summary>
    /// Writes arbitrary value bytes to a raw Canon PTP property code, bypassing the
    /// <see cref="EdsPropertyId"/> mapping.
    /// </summary>
    public Task<EdsError> SetRawPropertyBytesAsync(
        ushort ptpPropertyCode, ReadOnlyMemory<byte> value, CancellationToken ct = default) =>
        _canon.SetPropertyBytesAsync(ptpPropertyCode, value, ct);

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
    /// Whether this camera is connected through Windows Portable Devices — either
    /// <see cref="ConnectWpd"/> or <see cref="ConnectWpdIoctl"/>.
    /// </summary>
    public bool IsWpdTransport => _transport is IMtpExtTransport;

    /// <summary>
    /// Whether the WPD Content API is available — object enumeration, downloads by object ID, and
    /// driver-pushed object-added callbacks.
    /// </summary>
    /// <remarks>
    /// True only for <see cref="ConnectWpd"/>. That API is COM interfaces
    /// (<c>IPortableDeviceContent</c>, <c>IStream</c>) rather than MTP extension commands, so it has
    /// no equivalent below the COM layer and <see cref="ConnectWpdIoctl"/> cannot offer it. Nothing
    /// is lost that matters: the PTP equivalents — <see cref="DownloadAsync"/>,
    /// <see cref="GetObjectFileNameAsync"/>, and <see cref="ObjectAdded"/> off the event pump — work
    /// on every transport.
    /// </remarks>
    public bool SupportsWpdContentApi => _transport is WpdPtpTransport;

    /// <summary>
    /// Registers for WPD object-added events. The callback receives the WPD object ID.
    /// Only works when <see cref="SupportsWpdContentApi"/> is true; use <see cref="ObjectAdded"/>
    /// otherwise.
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
    /// Only works when <see cref="SupportsWpdContentApi"/> is true.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public Task DownloadWpdObjectAsync(string objectId, Stream destination, CancellationToken ct = default)
    {
        if (_transport is not WpdPtpTransport wpd)
            throw new InvalidOperationException(
                "The WPD Content API needs the COM transport (ConnectWpd). Use DownloadAsync, which "
                + "goes through PTP GetObject and works on every transport.");
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
        // Idempotent: a consumer's own disconnect action and its process-exit cleanup can both get
        // here, and the second pass would stop a poller and close a transport that are already gone.
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

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
