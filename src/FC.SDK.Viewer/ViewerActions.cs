using FC.SDK.Canon;
using Microsoft.Extensions.Logging;
using SharpAstro.Jpeg;

namespace FC.SDK.Viewer;

/// <summary>
/// Every camera operation the UI can trigger. All PTP traffic is funnelled through one gate so the
/// half-duplex protocol is never asked to do two things at once, and every step is logged — the log
/// file is the deliverable when a body misbehaves.
/// </summary>
public sealed class ViewerActions(ViewerState state, ILoggerFactory loggerFactory) : IAsyncDisposable
{
    private static readonly TimeSpan LiveViewFrameInterval = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// Consecutive empty frames before live view stops itself. At ~100 ms a frame this is about
    /// 10 seconds — long enough for a body that is genuinely still waking up, short enough that a
    /// body which will never deliver does not sit there occupying the camera.
    /// </summary>
    private const int MaxLiveViewFailures = 100;

    /// <summary>
    /// Consecutive <see cref="EdsError.WaitTimeoutError"/> frames before giving up — far lower than
    /// <see cref="MaxLiveViewFailures"/> because the two failures mean different things. ObjectNotReady
    /// comes back instantly and is normal between frames; a timeout means the transport itself is
    /// wedged and already cost a full command deadline, so retrying it a hundred times would spin for
    /// tens of minutes rather than ten seconds.
    /// </summary>
    private const int MaxLiveViewTimeouts = 2;

    /// <summary>
    /// Whole-sweep budget for reading every mapped property. Each cache miss costs a request plus an
    /// event drain, and against a wedged transport each can cost a full command deadline — a 68-entry
    /// sweep then runs for minutes with the action gate held, which is what made Disconnect look dead.
    /// </summary>
    private static readonly TimeSpan PropertySweepBudget = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Consecutive timeouts before abandoning the sweep. Once the transport stops answering, the
    /// remaining properties will not answer either; continuing just burns the budget.
    /// </summary>
    private const int MaxSweepTimeouts = 2;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _shutdown = new();

    /// <summary>
    /// Cancels whatever operation currently holds <see cref="_gate"/>. Teardown uses it so a stuck
    /// command cannot make Disconnect look like a dead button: a property sweep against a wedged
    /// transport can run for minutes, and everything clicked meanwhile just queues behind it.
    /// </summary>
    private CancellationTokenSource? _running;
    private readonly ILogger _logger = loggerFactory.CreateLogger("Viewer");
    private Task? _liveViewLoop;

    // Single-flight guard for DisconnectCoreAsync — see its doc comment.
    private readonly Lock _teardownLock = new();
    private Task? _teardown;

    /// <summary>Download an image to <see cref="ViewerState.OutputDirectory"/> as soon as it appears.</summary>
    public bool AutoDownload { get; set; } = true;

    /// <summary>
    /// Runs <paramref name="body"/> on the thread pool, serialized against every other camera
    /// operation. Failures are logged rather than thrown — a debug tool must survive a camera that
    /// disconnects mid-command.
    /// </summary>
    public void Enqueue(string name, Func<CancellationToken, Task> body)
    {
        _ = Task.Run(async () =>
        {
            await _gate.WaitAsync(_shutdown.Token);

            // Published so teardown can cancel this operation instead of queueing behind it.
            using var running = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
            _running = running;

            state.BusyOperation = name;
            state.Invalidate();
            try
            {
                _logger.LogDebug("▶ {Operation}", name);
                await body(running.Token);
            }
            catch (OperationCanceledException)
            {
                _logger.LogDebug("{Operation} cancelled", name);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "{Operation} threw", name);
                state.StatusMessage = $"{name}: {ex.GetType().Name} — {ex.Message}";
            }
            finally
            {
                _running = null;
                state.BusyOperation = null;
                state.Invalidate();
                // DisposeAsync can dispose the gate while a cancelled operation is still unwinding
                // on the pool; its release must be a no-op then, not an unobserved crash.
                try { _gate.Release(); } catch (ObjectDisposedException) { }
            }
        });
    }

    /// <summary>
    /// Cancels the running operation so a teardown can proceed. Without this, Disconnect sat behind
    /// whatever was stuck — a property sweep across a wedged transport takes minutes — and read as a
    /// button that does nothing.
    /// </summary>
    private void CancelRunning(string reason)
    {
        if (_running is not { } running || running.IsCancellationRequested) return;

        _logger.LogInformation("Cancelling '{Operation}' — {Reason}", state.BusyOperation ?? "?", reason);
        try { running.Cancel(); } catch (ObjectDisposedException) { /* already finished */ }
    }

    // --- Discovery and connection ---

    public void Scan() => Enqueue("Scan for cameras", _ =>
    {
        state.Devices.Clear();

        if (OperatingSystem.IsWindows())
        {
            foreach (var (deviceId, friendlyName) in CanonCamera.EnumerateWpdCameras())
            {
                state.Devices.Add(new DiscoveredCamera(TransportKind.Wpd, deviceId, friendlyName));
                _logger.LogInformation("WPD: {Name} ({DeviceId})", friendlyName, deviceId);
            }
        }

        try
        {
            foreach (var usb in CanonCamera.EnumerateUsbCameras())
            {
                var label = $"{usb.Product} ({usb.SerialNumber}) VID_{usb.VendorId:X4}&PID_{usb.ProductId:X4}";
                state.Devices.Add(new DiscoveredCamera(TransportKind.Usb, usb.DevicePath, label, usb));
                _logger.LogInformation("USB: {Label}", label);
            }
        }
        catch (Exception ex)
        {
            // No WinUSB driver bound is the normal case on Windows — WPD covers it.
            _logger.LogDebug("USB enumeration unavailable: {Message}", ex.Message);
        }

        // The camera's own AP always answers on this address; offer it unconditionally since there
        // is nothing to enumerate over WiFi until we connect.
        state.Devices.Add(new DiscoveredCamera(TransportKind.WiFi, "192.168.0.1", "192.168.0.1 (camera AP)"));

        if (state.Devices.Count > 0 && state.SelectedDeviceIndex < 0) state.SelectedDeviceIndex = 0;
        state.StatusMessage = $"Found {state.Devices.Count} candidate connection(s).";
        _logger.LogInformation("Scan complete: {Count} candidates", state.Devices.Count);
        return Task.CompletedTask;
    });

    public void Connect() => Enqueue("Connect", async ct =>
    {
        if (state.SelectedDeviceIndex < 0 || state.SelectedDeviceIndex >= state.Devices.Count)
        {
            state.StatusMessage = "Select a camera first.";
            return;
        }

        await DisconnectCoreAsync();

        var device = state.Devices[state.SelectedDeviceIndex];
        var factory = new CanonCameraFactory(loggerFactory.CreateLogger<CanonCamera>());

        var camera = device switch
        {
            { Transport: TransportKind.Wpd } when OperatingSystem.IsWindows() => factory.ConnectWpd(device.Identifier),
            { Transport: TransportKind.Usb, Usb: { } usb } => factory.ConnectUsb(usb),
            { Transport: TransportKind.WiFi } => factory.ConnectWifi(device.Identifier, "FC.SDK Viewer"),
            _ => throw new PlatformNotSupportedException($"{device.Transport} is not available on this OS"),
        };

        camera.ObjectAdded += OnObjectAdded;
        camera.PropertyChanged += OnPropertyChanged;
        camera.StateChanged += OnStateChanged;

        state.Camera = camera;
        state.ConnectedTo = device;
        state.StatusMessage = $"Connected transport to {device.Label}. Open a session next.";
        _logger.LogInformation("Transport connected: {Device}", device);

        await camera.ConnectTransportAsync(ct);
    });

    public void Disconnect()
    {
        // Cancel first, then queue: the gate frees as soon as the running op observes cancellation,
        // so the teardown starts in about a round trip rather than after a stuck sweep finishes.
        CancelRunning("disconnect requested");
        Enqueue("Disconnect", async _ => await DisconnectCoreAsync());
    }

    /// <summary>
    /// Single-flight teardown. The Disconnect button runs inside the action gate, but Connect's
    /// pre-clean and window-close (<see cref="DisposeAsync"/>) run outside it, so two teardowns
    /// could race over the same camera — the loser's CloseSession then landed on a COM device the
    /// winner had already shut down, which is where the WPD 0x802A0002 ("Shutdown was already
    /// called") warnings at exit came from. A racer now awaits the teardown already in flight.
    /// </summary>
    private Task DisconnectCoreAsync()
    {
        lock (_teardownLock)
        {
            if (_teardown is not { IsCompleted: false })
            {
                _teardown = TearDownAsync();
            }
            return _teardown;
        }
    }

    private async Task TearDownAsync()
    {
        await StopLiveViewLoopAsync();

        if (state.Camera is not { } camera) return;

        camera.ObjectAdded -= OnObjectAdded;
        camera.PropertyChanged -= OnPropertyChanged;
        camera.StateChanged -= OnStateChanged;

        try
        {
            if (state.SessionOpen) await camera.CloseSessionAsync();
            await camera.StopEventPollingAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Error while closing session: {Message}", ex.Message);
        }
        finally
        {
            await camera.DisposeAsync();
            state.Camera = null;
            state.ConnectedTo = null;
            state.SessionOpen = false;
            state.RemoteMode = false;
            state.LiveViewActive = false;
            state.Readings.Clear();
            state.RawProperties = [];
            state.StatusMessage = "Disconnected.";
        }
    }

    // --- Session ---

    public void OpenSession(bool remoteMode) => Enqueue(remoteMode ? "Open session" : "Open session (no remote mode)", async ct =>
    {
        if (state.Camera is not { } camera) { state.StatusMessage = "Connect first."; return; }

        var err = remoteMode
            ? await camera.OpenSessionAsync(ct)
            : await camera.OpenSessionNoRemoteModeAsync(ct);

        _logger.LogInformation("OpenSession({RemoteMode}) = {Error}", remoteMode, err);
        if (err is not EdsError.OK)
        {
            state.StatusMessage = $"OpenSession failed: {err}";
            return;
        }

        state.SessionOpen = true;
        state.RemoteMode = remoteMode;
        state.Model = camera.Model;
        state.SerialNumber = camera.SerialNumber;
        state.BatteryPercent = camera.BatteryLevelPercent;
        state.SupportedOperations = camera.SupportedOperations;

        _logger.LogInformation("Model={Model} Serial={Serial} Battery={Battery}%",
            camera.Model, camera.SerialNumber, camera.BatteryLevelPercent);
        LogOperationSupport(camera);

        if (remoteMode)
        {
            // The event pump is what keeps property reads answerable and writes accepted.
            camera.StartEventPolling(TimeSpan.FromMilliseconds(200));
            await ReadAllCoreAsync(camera, ct);
        }

        state.StatusMessage = $"Session open on {camera.Model ?? "camera"}.";
    });

    private void LogOperationSupport(CanonCamera camera)
    {
        var ops = camera.SupportedOperations;
        if (ops.Count == 0)
        {
            _logger.LogWarning("Camera reported no operation list — GetDeviceInfo may have failed");
            return;
        }

        // These four explain most "why doesn't X work" reports on their own.
        (ushort Code, string Name)[] interesting =
        [
            (0x1015, "GetDevicePropValue"),
            (0x9110, "SetDevicePropValueEx"),
            (0x9116, "GetEvent"),
            (0x9127, "RequestDevicePropValue"),
            (0x913D, "SetRequestOLCInfoGroup"),
            (0x911A, "PCHDDCapacity"),
            (0x9128, "RemoteReleaseOn"),
            (0x9125, "BulbStart"),
            (0x9153, "GetViewFinderData"),
        ];

        _logger.LogInformation("Camera advertises {Count} PTP operations", ops.Count);
        foreach (var (code, name) in interesting)
        {
            _logger.LogInformation("  0x{Code:X4} {Name}: {Supported}", code, name,
                ops.Contains(code) ? "yes" : "NO");
        }
    }

    public void CloseSession()
    {
        CancelRunning("close session requested");
        CloseSessionQueued();
    }

    private void CloseSessionQueued() => Enqueue("Close session", async ct =>
    {
        if (state.Camera is not { } camera) return;

        await StopLiveViewLoopAsync();
        await camera.StopEventPollingAsync();
        var err = await camera.CloseSessionAsync(ct);
        _logger.LogInformation("CloseSession = {Error}", err);
        state.SessionOpen = false;
        state.RemoteMode = false;
        state.StatusMessage = $"Session closed ({err}).";
    });

    public void SetRemoteMode(bool enabled) => Enqueue(enabled ? "Enter remote mode" : "Exit remote mode", async ct =>
    {
        if (state.Camera is not { } camera) return;

        var err = enabled ? await camera.EnterRemoteModeAsync(ct) : await camera.ExitRemoteModeAsync(ct);
        _logger.LogInformation("SetRemoteMode({Enabled}) = {Error}", enabled, err);
        state.RemoteMode = enabled && err is EdsError.OK;
    });

    // --- Properties ---

    public void ReadAll() => Enqueue("Read all properties", async ct =>
    {
        if (state.Camera is not { } camera) return;
        await ReadAllCoreAsync(camera, ct);
    });

    private async Task ReadAllCoreAsync(CanonCamera camera, CancellationToken ct)
    {
        // Bounded as a whole, not just per property: see PropertySweepBudget.
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(PropertySweepBudget);
        ct = budget.Token;

        // One drain up front means the per-property reads mostly hit the cache instead of each
        // paying for a request/drain round trip.
        var drained = await camera.DrainEventsAsync(ct);
        _logger.LogDebug("Drained {Count} pending events before property sweep", drained);

        int ok = 0, failed = 0, timeouts = 0;
        foreach (var control in CameraControls.All)
        {
            if (ct.IsCancellationRequested)
            {
                _logger.LogWarning("Property sweep stopped after {Budget}s ({Ok} read, {Failed} unavailable)",
                    PropertySweepBudget.TotalSeconds, ok, failed);
                break;
            }

            if (timeouts >= MaxSweepTimeouts)
            {
                _logger.LogError("Property sweep abandoned: the transport stopped answering after {Ok} properties", ok);
                state.StatusMessage = "Property sweep abandoned — the camera stopped answering.";
                break;
            }

            var (err, value) = await camera.GetPropertyAsync(control.PropertyId, ct);
            timeouts = err is EdsError.WaitTimeoutError ? timeouts + 1 : 0;
            var allowed = err is EdsError.OK ? await camera.GetAllowedValuesAsync(control.PropertyId, ct) : null;

            state.Readings[control.PropertyId] = new ControlReading(err, value, allowed);

            if (err is EdsError.OK)
            {
                ok++;
                _logger.LogInformation("{Label} ({Property}) = {Formatted} [raw 0x{Raw:X}]{Allowed}",
                    control.Label, control.PropertyId, control.Format(value), value,
                    allowed is null ? "" : $" of {allowed.Length} allowed");
            }
            else
            {
                failed++;
                _logger.LogWarning("{Label} ({Property}) read failed: {Error}", control.Label, control.PropertyId, err);
            }
        }

        await ApplyCustomFunctionFallbacksAsync(camera, ct);

        state.RawProperties = await camera.DumpPropertiesAsync(ct);
        state.BatteryPercent = camera.BatteryLevelPercent;
        state.StatusMessage = $"Read {ok} properties, {failed} unavailable; {state.RawProperties.Count} in the event cache.";
        _logger.LogInformation("Property sweep: {Ok} readable, {Failed} unavailable, {Cached} codes in cache",
            ok, failed, state.RawProperties.Count);
        state.Invalidate();
    }

    /// <summary>
    /// Several settings on older bodies are Custom Functions, not properties — the 450D answers
    /// DevicePropNotSupported to 0xD13A/0xD1BF (mirror lockup) and 0xD178 (high-ISO NR). The SDK's
    /// accessors fall back to the C.Fn block where the body's id is known, so when the plain sweep
    /// failed, re-ask through them and overwrite the readings.
    /// </summary>
    private async Task ApplyCustomFunctionFallbacksAsync(CanonCamera camera, CancellationToken ct)
    {
        if (ct.IsCancellationRequested) return;

        if (state.Reading(EdsPropertyId.MirrorUpSetting) is { Ok: false })
        {
            var (err, setting) = await camera.GetMirrorUpSettingAsync(ct);
            if (err is EdsError.OK) // otherwise this body has no known C.Fn id — keep the honest error
            {
                state.Readings[EdsPropertyId.MirrorUpSetting] =
                    new ControlReading(EdsError.OK, (uint)setting, [0u, 1u]);

                var (stateErr, mluState) = await camera.GetMirrorLockupStateAsync(ct);
                if (stateErr is EdsError.OK)
                {
                    state.Readings[EdsPropertyId.MirrorLockUpState] =
                        new ControlReading(EdsError.OK, (uint)mluState, null);
                }

                _logger.LogInformation("Mirror lockup via C.Fn block: {Setting} (state {State})", setting, mluState);
            }
        }

        if (state.Reading(EdsPropertyId.NoiseReduction) is { Ok: false } && !ct.IsCancellationRequested)
        {
            var (err, nr) = await camera.GetHighIsoNRAsync(ct);
            if (err is EdsError.OK)
            {
                // The 450D's C.Fn is two-state; the SDK translates Off → Disable, On → Standard.
                state.Readings[EdsPropertyId.NoiseReduction] = new ControlReading(EdsError.OK, (uint)nr,
                    [(uint)EdsHighIsoNR.Standard, (uint)EdsHighIsoNR.Disable]);
                _logger.LogInformation("High ISO NR via C.Fn block: {Value}", nr);
            }
        }
    }

    /// <summary>
    /// Keeps the "MLU state" reading current on bodies where it is derived from the setting rather
    /// than reported by the camera — no event will ever push it there.
    /// </summary>
    private void RefreshInferredMirrorState(CanonCamera camera)
    {
        if (camera.MirrorLockupEnabled is not { } enabled) return;
        if (state.Reading(EdsPropertyId.MirrorLockUpState) is null or { Ok: false }) return;

        var mluState = enabled ? EdsMirrorLockupState.Enable : EdsMirrorLockupState.Disable;
        state.Readings[EdsPropertyId.MirrorLockUpState] = new ControlReading(EdsError.OK, (uint)mluState, null);
        state.Invalidate();
    }

    public void SetControl(CameraControl control, uint value) =>
        Enqueue($"Set {control.Label}", async ct =>
        {
            if (state.Camera is not { } camera) return;

            EdsError err;
            if (control.PropertyId is EdsPropertyId.SaveTo)
            {
                // Goes through the dedicated path so the host-capacity handshake happens too.
                err = await camera.SetCaptureDestinationAsync((CanonCaptureDestination)value, ct);
            }
            else if (control.PropertyId is EdsPropertyId.MirrorUpSetting)
            {
                // C.Fn-aware path: falls back to the block write on bodies without the property.
                err = await camera.SetMirrorLockupAsync((EdsMirrorUpSetting)value, ct);
            }
            else if (control.PropertyId is EdsPropertyId.NoiseReduction)
            {
                // Same C.Fn fallback as mirror lockup.
                err = await camera.SetHighIsoNRAsync((EdsHighIsoNR)value, ct);
            }
            else
            {
                err = await camera.SetPropertyAsync(control.PropertyId, value, ct);
            }

            _logger.LogInformation("Set {Label} = {Formatted} [raw 0x{Raw:X}] → {Error}",
                control.Label, control.Format(value), value, err);

            // Re-read either way: on failure to show what the camera kept, on success to confirm.
            EdsError readErr;
            uint readValue;
            if (control.PropertyId is EdsPropertyId.MirrorUpSetting)
            {
                (readErr, var setting) = await camera.GetMirrorUpSettingAsync(ct);
                readValue = (uint)setting;
                RefreshInferredMirrorState(camera);
            }
            else if (control.PropertyId is EdsPropertyId.NoiseReduction)
            {
                (readErr, var nr) = await camera.GetHighIsoNRAsync(ct);
                readValue = (uint)nr;
            }
            else
            {
                (readErr, readValue) = await camera.GetPropertyAsync(control.PropertyId, ct);
            }
            var allowed = await camera.GetAllowedValuesAsync(control.PropertyId, ct) ?? control.PropertyId switch
            {
                EdsPropertyId.MirrorUpSetting => [0u, 1u],
                EdsPropertyId.NoiseReduction => [(uint)EdsHighIsoNR.Standard, (uint)EdsHighIsoNR.Disable],
                _ => null,
            };
            state.Readings[control.PropertyId] = new ControlReading(readErr, readValue, allowed);

            state.StatusMessage = err switch
            {
                EdsError.OK => $"{control.Label} → {control.Format(readValue)}",
                EdsError.OperationRefused when control.PropertyId is EdsPropertyId.MirrorUpSetting =>
                    "Mirror lockup: this body reverts remote changes — set C.Fn 9 in the camera menu instead.",
                _ => $"{control.Label} failed: {err}",
            };
            state.Invalidate();
        });

    public void DumpProperties() => Enqueue("Dump properties to file", async ct =>
    {
        if (state.Camera is not { } camera) return;

        var snapshot = await camera.DumpPropertiesAsync(ct);
        state.RawProperties = snapshot;

        var path = Path.Combine(state.OutputDirectory, $"properties-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
        Directory.CreateDirectory(state.OutputDirectory);

        await using var writer = new StreamWriter(path);
        await writer.WriteLineAsync($"# FC.SDK property dump — {DateTime.Now:O}");
        await writer.WriteLineAsync($"# Model: {camera.Model}  Serial: {camera.SerialNumber}  Battery: {camera.BatteryLevelPercent}%");
        await writer.WriteLineAsync($"# Supported PTP operations ({camera.SupportedOperations.Count}): " +
            string.Join(" ", camera.SupportedOperations.Order().Select(o => $"0x{o:X4}")));
        await writer.WriteLineAsync();
        foreach (var entry in snapshot)
        {
            await writer.WriteLineAsync(entry.ToString());
        }

        state.StatusMessage = $"Wrote {snapshot.Count} properties to {Path.GetFileName(path)}";
        _logger.LogInformation("Property dump written to {Path} ({Count} entries)", path, snapshot.Count);
    });

    public void DrainEvents() => Enqueue("Drain events", async ct =>
    {
        if (state.Camera is not { } camera) return;
        var count = await camera.DrainEventsAsync(ct);
        state.RawProperties = await camera.DumpPropertiesAsync(ct);
        state.StatusMessage = $"Drained {count} event record(s).";
        _logger.LogInformation("Manual drain returned {Count} records", count);
    });

    // --- Capture ---

    public void TakePicture() => Enqueue("Take picture", async ct =>
    {
        if (state.Camera is not { } camera) return;

        var err = await camera.TakePictureAsync(ct);
        _logger.LogInformation("TakePicture = {Error}", err);
        state.StatusMessage = err switch
        {
            not EdsError.OK => $"TakePicture failed: {err}",
            // Verified on a 450D: with MLU on, RemoteRelease answers OK and then nothing happens —
            // no mirror, no events, no image. Say so instead of "waiting" forever.
            EdsError.OK when camera.MirrorLockupEnabled == true =>
                "Released, but MLU is enabled — this body ignores remote releases in MLU mode; disable MLU to shoot remotely.",
            _ => "Shutter released; waiting for the image…",
        };
    });

    public void InitiateCapture() => Enqueue("InitiateCapture (standard PTP)", async ct =>
    {
        if (state.Camera is not { } camera) return;
        var err = await camera.InitiateCaptureAsync(ct);
        _logger.LogInformation("InitiateCapture = {Error}", err);
        state.StatusMessage = $"InitiateCapture: {err}";
    });

    public void HalfPress(bool press) => Enqueue(press ? "Half-press shutter" : "Release shutter", async ct =>
    {
        if (state.Camera is not { } camera) return;
        var err = press ? await camera.PressShutterHalfwayAsync(ct) : await camera.ReleaseShutterAsync(ct);
        _logger.LogInformation("{Action} = {Error}", press ? "Half-press" : "Release", err);
        state.StatusMessage = $"{(press ? "Half-press" : "Release")}: {err}";
    });

    public void Bulb(bool start) => Enqueue(start ? "Bulb start" : "Bulb end", async ct =>
    {
        if (state.Camera is not { } camera) return;
        var err = start ? await camera.BulbStartAsync(ct) : await camera.BulbEndAsync(ct);
        _logger.LogInformation("Bulb{Action} = {Error}", start ? "Start" : "End", err);
        state.StatusMessage = err is EdsError.OperationRefused
            ? "Bulb refused — the mode dial has to be on B."
            : $"Bulb {(start ? "start" : "end")}: {err}";
    });

    public void CancelAutoFocus() => Enqueue("Cancel AF", async ct =>
    {
        if (state.Camera is not { } camera) return;
        var err = await camera.CancelAutoFocusAsync(ct);
        _logger.LogInformation("AfCancel = {Error}", err);
        state.StatusMessage = $"AF cancel: {err}";
    });

    public void DriveLens(EdsDriveLensStep step) => Enqueue($"Drive lens {step}", async ct =>
    {
        if (state.Camera is not { } camera) return;
        var err = await camera.DriveLensAsync(step, ct);
        _logger.LogInformation("DriveLens({Step}) = {Error}", step, err);
        state.StatusMessage = $"Drive lens {step}: {err}";
    });

    public void KeepDeviceOn() => Enqueue("Keep device on", async ct =>
    {
        if (state.Camera is not { } camera) return;
        var err = await camera.KeepDeviceOnAsync(ct);
        _logger.LogInformation("KeepDeviceOn = {Error}", err);
        state.StatusMessage = $"Keep-alive: {err}";
    });

    public void SetUILock(bool locked) => Enqueue(locked ? "Lock camera UI" : "Unlock camera UI", async ct =>
    {
        if (state.Camera is not { } camera) return;
        var err = await camera.SetUILockAsync(locked, ct);
        _logger.LogInformation("SetUILock({Locked}) = {Error}", locked, err);
        state.StatusMessage = $"UI {(locked ? "lock" : "unlock")}: {err}";
    });

    public void ReportHostCapacity() => Enqueue("Report host capacity", async ct =>
    {
        if (state.Camera is not { } camera) return;
        var err = await camera.ReportHostCapacityAsync(ct);
        _logger.LogInformation("PCHDDCapacity = {Error}", err);
        state.StatusMessage = $"Host capacity: {err}";
    });

    public void ResetMirrorLockup() => Enqueue("Reset mirror lockup state", async ct =>
    {
        if (state.Camera is not { } camera) return;
        var err = await camera.ResetMirrorLockupStateAsync(ct);
        _logger.LogInformation("ResetMirrorLockupState = {Error}", err);
        state.StatusMessage = $"Reset MLU: {err}";
    });

    public void ReadCustomFunctions() => Enqueue("Read custom-function block", async ct =>
    {
        if (state.Camera is not { } camera) return;

        var (err, block) = await camera.GetCustomFunctionBlockAsync(ct);
        if (err is not EdsError.OK || block is null)
        {
            _logger.LogWarning("Custom-function block unavailable: {Error}", err);
            state.StatusMessage = $"C.Fn block: {err}";
            return;
        }

        // The raw uint32 words are the primary record. libgphoto2 does not decode this block either
        // (ptp_unpack_EOS_CustomFuncEx just renders it as comma-separated hex), so the structured
        // parse below is a hypothesis, not a spec — dump the words so a reading can be diffed against
        // the same camera with one setting toggled. That diff is how a C.Fn offset gets identified.
        _logger.LogInformation("Custom-function block: {Bytes} bytes ({Words} words)",
            block.RawData.Length, block.RawData.Length / 4);

        for (int offset = 0; offset < block.RawData.Length; offset += 32)
        {
            var words = new List<string>();
            for (int w = offset; w < Math.Min(offset + 32, block.RawData.Length & ~3); w += 4)
            {
                words.Add($"{System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(block.RawData.AsSpan(w)):x8}");
            }
            if (words.Count > 0)
                _logger.LogInformation("  raw[{Index,3}] {Words}", offset / 4, string.Join(" ", words));
        }

        _logger.LogInformation("Structured parse (UNVERIFIED layout — {Count} entries):", block.Functions.Count);
        foreach (var (functionId, value) in block.Functions.OrderBy(f => f.Key))
        {
            _logger.LogInformation("  C.Fn 0x{Id:X4} = {Value}", functionId, value);
        }

        state.StatusMessage = $"C.Fn block: {block.RawData.Length} bytes dumped to the log.";
    });

    // --- Download ---

    public void DownloadLast() => Enqueue("Download last image", async ct =>
    {
        if (state.Camera is not { } camera) return;
        if (state.LastObjectHandle is not { } handle)
        {
            state.StatusMessage = "No image handle yet — take a picture first.";
            return;
        }
        await DownloadCoreAsync(camera, handle, ct);
    });

    private async Task DownloadCoreAsync(CanonCamera camera, uint handle, CancellationToken ct)
    {
        var (_, fileName) = await camera.GetObjectFileNameAsync(handle, ct);
        state.LastFileName = fileName;
        _logger.LogInformation("Object 0x{Handle:X8} filename: {FileName}", handle, fileName ?? "(unknown)");

        // Thumbnail first: it is small, arrives quickly, and confirms the frame really exists.
        var (thumbErr, thumbData) = await camera.GetThumbAsync(handle, ct);
        if (thumbErr is EdsError.OK && thumbData.Length > 0)
        {
            state.LastThumbnail = TryDecodeJpeg(thumbData, "thumbnail");
            // The image that just changed is the one to show — but never yank the pane away from a
            // running live view (auto-download fires mid-stream when the camera also saves to card).
            if (!state.LiveViewActive) state.PreviewMode = PreviewPane.Capture;
            _logger.LogInformation("Thumbnail: {Bytes:N0} bytes JPEG", thumbData.Length);
            state.Invalidate();
        }
        else
        {
            _logger.LogWarning("Thumbnail unavailable: {Error}", thumbErr);
        }

        Directory.CreateDirectory(state.OutputDirectory);
        var extension = Path.GetExtension(fileName) is { Length: > 0 } ext ? ext : ".cr2";
        var path = Path.Combine(state.OutputDirectory, $"capture-{DateTime.Now:yyyyMMdd-HHmmss}{extension}");

        await using (var fs = File.Create(path))
        {
            var err = await camera.DownloadAsync(handle, fs, ct);
            state.LastSavedBytes = fs.Length;
            _logger.LogInformation("Download = {Error} ({Bytes:N0} bytes → {Path})", err, fs.Length, path);
            if (err is not EdsError.OK)
            {
                state.StatusMessage = $"Download failed: {err}";
                return;
            }
        }

        state.LastSavedPath = path;
        var complete = await camera.TransferCompleteAsync(handle, ct);
        _logger.LogInformation("TransferComplete = {Error}", complete);

        state.StatusMessage = $"Saved {Path.GetFileName(path)} ({state.LastSavedBytes:N0} bytes)";
        state.Invalidate();
    }

    // --- Live view ---

    public void StartLiveView() => Enqueue("Start live view", async ct =>
    {
        if (state.Camera is not { } camera) return;

        var err = await camera.StartLiveViewAsync(ct);
        _logger.LogInformation("StartLiveView = {Error}", err);
        if (err is not EdsError.OK)
        {
            state.StatusMessage = $"Live view failed: {err}";
            return;
        }

        state.LiveViewActive = true;
        state.LiveViewFrameCount = 0;
        state.PreviewMode = PreviewPane.LiveView;
        state.StatusMessage = "Live view running.";
        _liveViewLoop = Task.Run(() => LiveViewLoopAsync(camera, _shutdown.Token));
    });

    public void StopLiveView() => Enqueue("Stop live view", async ct =>
    {
        if (state.Camera is not { } camera) return;

        await StopLiveViewLoopAsync();
        var err = await camera.StopLiveViewAsync(ct);
        _logger.LogInformation("StopLiveView = {Error}", err);
        state.StatusMessage = $"Live view stopped ({err}).";
    });

    private async Task StopLiveViewLoopAsync()
    {
        state.LiveViewActive = false;
        if (_liveViewLoop is { } loop)
        {
            _liveViewLoop = null;
            try { await loop; } catch (OperationCanceledException) { }
        }
    }

    /// <summary>
    /// Pulls viewfinder frames on its own cadence, taking the same gate as user actions so a click
    /// mid-stream never interleaves two PTP transactions.
    /// </summary>
    private async Task LiveViewLoopAsync(CanonCamera camera, CancellationToken ct)
    {
        int consecutiveFailures = 0;
        int consecutiveTimeouts = 0;
        int framesSinceKeepAlive = 0;

        // Half the 450D's shortest metering timer (16 s): libgphoto2 sends KeepDeviceOn with every
        // preview frame because a body whose live-view subsystem times out stops answering
        // GetViewFinderData entirely. Every frame is overkill on a half-duplex link; every few
        // seconds keeps the timer fed at 1% of the traffic.
        const int KeepAliveEveryFrames = 80; // ~8 s at the 100 ms cadence

        while (state.LiveViewActive && !ct.IsCancellationRequested)
        {
            try
            {
                await _gate.WaitAsync(ct);
                EdsError err;
                byte[] jpeg;
                try
                {
                    if (++framesSinceKeepAlive >= KeepAliveEveryFrames)
                    {
                        framesSinceKeepAlive = 0;
                        await camera.KeepDeviceOnAsync(ct);
                    }
                    (err, jpeg) = await camera.GetLiveViewFrameAsync(ct);
                }
                finally
                {
                    _gate.Release();
                }

                if (err is EdsError.OK && jpeg.Length > 0)
                {
                    consecutiveFailures = 0;
                    consecutiveTimeouts = 0;
                    if (TryDecodeJpeg(jpeg, "live view") is { } raster)
                    {
                        state.LiveViewFrame = raster;
                        state.LiveViewFrameCount++;
                        state.Invalidate();
                    }
                }
                else
                {
                    consecutiveFailures++;
                    if (err is EdsError.WaitTimeoutError) consecutiveTimeouts++;

                    // Objects-not-ready is normal between frames; only shout when it persists.
                    if (consecutiveFailures is 1 or 10 or 50)
                        _logger.LogWarning("Live view frame {Error} ({Count} consecutive)", err, consecutiveFailures);
                }

                // Give up rather than hold the camera hostage. A body that advertises
                // GetViewFinderData but never delivers (450D with Live View disabled in its menu, for
                // one) would otherwise keep this loop taking the gate forever, so every button in the
                // UI goes inert while the window still looks perfectly healthy — the worst kind of
                // failure to diagnose from a screenshot.
                //
                // Timeouts get a much lower bound than empty frames: each one already cost a full
                // command deadline, so retrying a hundred of them would spin for tens of minutes
                // instead of the ten seconds MaxLiveViewFailures is scaled for.
                var timedOut = consecutiveTimeouts >= MaxLiveViewTimeouts;
                if (timedOut || consecutiveFailures >= MaxLiveViewFailures)
                {
                    _logger.LogError(
                        "Live view produced no frame in {Count} attempts ({Timeouts} timed out, last: {Error}). " +
                        "Stopping. If the body is a 450D-era Rebel, check " +
                        "Set-up 2 > Live View function settings > LV func. setting > Enable.",
                        consecutiveFailures, consecutiveTimeouts, err);

                    state.LiveViewActive = false;
                    // Not "the camera stopped answering": on a 450D it answers every frame, in full.
                    // What never answers is the WPD driver's end-of-transfer phase for 0x9153, and
                    // each unanswered one costs a thread — so streaming stops at a fixed frame count
                    // rather than on anything the camera did. Say so, or the next person re-debugs it.
                    state.StatusMessage = timedOut
                        ? $"Live view stopped after {state.LiveViewFrameCount} frames — this body's WPD driver never "
                          + $"completes a frame's end-of-transfer phase ({err})."
                        : $"Live view gave up after {consecutiveFailures} empty frames ({err}).";
                    state.Invalidate();
                    break;
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Live view loop error");
                break;
            }

            await Task.Delay(LiveViewFrameInterval, ct).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }

    public void SaveLiveViewFrame() => Enqueue("Save live view frame", async ct =>
    {
        if (state.Camera is not { } camera) return;

        var (err, jpeg) = await camera.GetLiveViewFrameAsync(ct);
        if (err is not EdsError.OK || jpeg.Length == 0)
        {
            state.StatusMessage = $"No live view frame: {err}";
            return;
        }

        Directory.CreateDirectory(state.OutputDirectory);
        var path = Path.Combine(state.OutputDirectory, $"liveview-{DateTime.Now:yyyyMMdd-HHmmss}.jpg");
        await File.WriteAllBytesAsync(path, jpeg, ct);
        _logger.LogInformation("Live view frame saved: {Path} ({Bytes:N0} bytes)", path, jpeg.Length);
        state.StatusMessage = $"Saved {Path.GetFileName(path)}";
    });

    private Raster? TryDecodeJpeg(byte[] jpeg, string what)
    {
        try
        {
            var image = JpegDecoder.Decode(jpeg);
            return new Raster(image.Width, image.Height, image.Pixels);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Could not decode {What} JPEG ({Bytes} bytes): {Message}", what, jpeg.Length, ex.Message);
            return null;
        }
    }

    // --- Camera events ---

    private void OnObjectAdded(object? sender, CanonObjectAddedEventArgs e)
    {
        _logger.LogInformation("ObjectAdded: handle=0x{Handle:X8}", e.ObjectHandle);
        state.LastObjectHandle = e.ObjectHandle;
        // The exposure completed, so the SDK just cleared its inferred mirror-up flag.
        if (state.Camera is { } eventCamera) RefreshInferredMirrorState(eventCamera);
        state.Invalidate();

        if (AutoDownload && state.Camera is { } camera)
        {
            Enqueue("Auto-download", ct => DownloadCoreAsync(camera, e.ObjectHandle, ct));
        }
    }

    private void OnPropertyChanged(object? sender, CanonPropertyChangedEventArgs e)
    {
        // Far too chatty for the info level — the camera re-announces dozens of properties per second
        // while live view runs — but invaluable at debug level in the log file.
        _logger.LogDebug("PropertyChanged: {PropertyId} = 0x{Value:X}", e.PropertyId, e.Value);
    }

    private void OnStateChanged(object? sender, CanonStateChangedEventArgs e)
    {
        _logger.LogDebug("Event {EventType} param=0x{Param:X}", e.EventType, e.Param);
    }

    public async ValueTask DisposeAsync()
    {
        await _shutdown.CancelAsync();
        await StopLiveViewLoopAsync();
        await DisconnectCoreAsync();
        _shutdown.Dispose();
        _gate.Dispose();
    }
}
