# FC.SDK: Free Canon SDK

Canon EOS camera control via PTP over USB, WiFi and Windows MTP. Pure managed C#, no EDSDK binary required.

## Why

Canon's EDSDK is closed-source, non-redistributable, and Windows-only. FC.SDK implements the same camera control capabilities using the PTP protocol (reverse-engineered by the [libgphoto2](https://github.com/gphoto/libgphoto2) project), working over USB and WiFi on any platform.

## Transports

| Transport | Platform | Driver swap? | Notes |
|---|---|---|---|
| **WPD ioctl** (`ConnectWpdIoctl`) | Windows | None (plug & play) | Same path EDSDK itself uses. Preferred |
| **WPD COM** (`ConnectWpd`) | Windows | None (plug & play) | Stock MTP driver via WPD COM API. Adds the WPD Content API |
| **USB** (LibUsbDotNet) | Linux/macOS/Windows | WinUSB on Windows | Cross-platform, lowest latency |
| **PTP/IP** (WiFi) | All | None | TCP port 15740, no cable needed |

On Windows, prefer `ConnectWpdAutoAsync`: it probes the ioctl path with a real `GetDeviceInfo` read and
falls back to COM if that fails, leaving the reason on `CanonCamera.TransportFallbackReason`.

Both Windows transports drive the same stock `wpdmtp.sys`, so neither needs a driver swap and both see
the same camera. They differ in how they reach it. The COM path goes through the documented WPD COM API;
the ioctl path talks to the driver with `DeviceIoControl` the way EDSDK does, which means hand-rolling
Microsoft's undocumented property-bag serialization. Measured on a 450D, 300 live-view frames each:

| | WPD COM | WPD ioctl |
|---|---|---|
| Frame rate | 20.2 fps | 20.3 fps |
| Private working set | 73–119 MiB | 38–47 MiB |

**Throughput is identical**: the body is the limiter. The memory is the reason to prefer ioctl: over COM,
an unfinished transfer poisons a device object while its end phase never returns, so every live-view frame
has to create and release a whole new one. The same end phase completes in 0–22 ms over ioctl.

The COM transport is not strictly worse, though: the WPD **Content API** (`EnumerateWpdObjects`,
`DownloadWpdObjectAsync`) is COM interfaces rather than MTP commands and has no equivalent below the COM
layer. `SupportsWpdContentApi` reports which you have. The PTP equivalents work on every transport.

A session commits to one transport for its whole life; they are alternatives, not layers. See
[`docs/canon-windows-transports.md`](docs/canon-windows-transports.md) for how EDSDK reaches the body and
[`docs/wpd-ioctl-wire-format.md`](docs/wpd-ioctl-wire-format.md) for the decoded buffer format.

## Usage

```csharp
using FC.SDK;
using FC.SDK.Canon;

// Connect via WPD (Windows, zero-install). Picks the ioctl transport when the body accepts it.
var (deviceId, _) = CanonCamera.EnumerateWpdCameras().First();
await using var camera = await CanonCamera.ConnectWpdAutoAsync(deviceId);
await camera.OpenSessionAsync();
Console.WriteLine($"{camera.Model}, serial {camera.SerialNumber}, battery {camera.BatteryLevelPercent}%");

// Typed settings, no magic uint32s
await camera.SetISOAsync(EdsISOSpeed.ISO_800);
await camera.SetShutterSpeedAsync(EdsTv.Tv_1_125);
await camera.SetApertureAsync(EdsAv.Av_2_8);

// Keep the event pump running for the whole session: on EOS bodies property values only ever
// arrive through GetEvent, and an undrained queue makes the camera reject property writes.
camera.StartEventPolling();

// Snap and download (auto-detects CR2, CR3, JPG)
await camera.SetSaveToAsync(EdsSaveTo.Host);
camera.ObjectAdded += async (s, e) =>
{
    var (_, fileName) = await camera.GetObjectFileNameAsync(e.ObjectHandle);
    await using var fs = File.Create(fileName ?? "capture.cr2");
    await camera.DownloadAsync(e.ObjectHandle, fs);
    await camera.TransferCompleteAsync(e.ObjectHandle);
};
await camera.TakePictureAsync();

// Bulb exposure. Either put the mode dial on B, or write the value on bodies that have no B
// position (a 450D flips its top LCD to "buLb" from this alone).
await camera.SetShutterSpeedAsync(EdsTv.Bulb);
await camera.BulbStartAsync();
await Task.Delay(TimeSpan.FromSeconds(120));
await camera.BulbEndAsync();

// Mirror lockup, which is not just a property write: lockup engages only in a self-timer drive,
// so the helper picks a timer off the body's own allowed list, releases, and restores. Do the
// restore after you have the image; see the note below.
await camera.TakePictureWithMirrorLockupAsync();
// ... wait for ObjectAdded, download, TransferCompleteAsync ...
await camera.ApplyPendingMirrorLockupRestoreAsync();

// Live view: frames, magnification, focus
await camera.StartLiveViewAsync();
var (error, jpeg) = await camera.GetLiveViewFrameAsync();

await camera.SetEvfZoomAsync(CanonEvfZoom.X5);        // verified against the real crop, not the ACK
var rect = await camera.GetEvfZoomRectAsync();        // the only honest read-out of magnification
await camera.SetEvfZoomPositionAsync(rect!.Value.X + 200, rect.Value.Y);   // pan, clamped by the body

var hist = await camera.GetEvfHistogramAsync();       // luma + R/G/B, 256 bins, linear in light
Console.WriteLine($"mean {hist?.MeanLevel:F1}, clipped {hist?.ClippedHighlights:P2}");

var (_, focus) = await camera.GetFocusStateAsync();   // can this body autofocus at all right now?
if (focus.AutoFocusAvailable) await camera.AutoFocusLiveViewAsync();
await camera.DriveLensAsync(EdsDriveLensStep.NearSmall);
await camera.StopLiveViewAsync();
```

## Architecture

```
CanonCamera              (public async API)
  CanonPtpSession        (Canon vendor opcodes 0x9xxx)
    CanonPropertyCache   (property mirror fed by the GetEvent stream)
    PtpSession           (transaction management, half-duplex lock)
      IPtpTransport      (USB / PTP-IP: raw send/receive)
      IMtpExtTransport   (WPD COM / WPD ioctl: 3 MTP phases)
```

### How EOS properties are read

EOS bodies expose **no** property-read operation: standard PTP `GetDevicePropValue` (0x1015) is absent from
their supported-operations list, and Canon's 0x9127 is `RequestDevicePropValue`, which asks the camera to *emit*
a value rather than returning one. Values and their selectable-value lists arrive only as `PropValueChanged` /
`AvailListChanged` records in the `GetEvent` (0x9116) stream. `CanonPropertyCache` mirrors that stream and
`GetPropertyAsync` answers out of the mirror, requesting a push when a code has not been seen yet. Same design
as EDSDK and libgphoto2.

Two practical consequences: run `StartEventPolling` for the whole session, and expect `DeviceBusy` on property
writes if you don't (the SDK drains and retries, but the camera really does gate writes on an empty queue).

A third: **a response code proves almost nothing on these bodies.** A 450D answers `OK` to writes of
properties it does not have, and to releases it silently discards. So the SDK verifies against state the
camera cannot fake (a read-back for property writes, the live-view crop for magnification, the drive mode
for mirror lockup), and returns `OperationRefused` when the camera kept its old behaviour.

### Mirror lockup

Not a property write, on either body measured. Lockup engages only in a **self-timer drive**, where the
firmware owns raise → settle → expose; in single-shot drive a 6D just takes the picture, so the caller gets a
frame and believes they had lockup. On a 450D, where lockup is a Custom Function rather than a property,
every remote release is discarded outright while it is armed, including bulb. `SupportsMirrorLockupCapture`
tells the two cases apart by where the body keeps the setting, and `TakePictureAsync` refuses rather than
relaying an ACK it knows is empty.

`TakePictureWithMirrorLockupAsync` handles the working recipe, but **its cleanup is the caller's job.** The
method returns when the release finishes, which is before the image arrives, and a frame still awaiting
`TransferComplete` makes the body reject the property writes that would restore your settings. So it does a
best effort, records the remainder on `PendingMirrorLockupRestore`, and you call
`ApplyPendingMirrorLockupRestoreAsync` once you have the frame. Skip it and the camera is left on a
self-timer, delaying every later exposure in a way that reads as a fault.

## Diagnostic viewer

Prebuilt, self-contained binaries are attached to every [release](https://github.com/SharpAstro/FC.SDK/releases):
`fc-viewer-<rid>.zip` for Windows, `.tar.gz` for Linux and macOS. No .NET install needed. The archive
carries the SDL3 native; the Vulkan loader comes from the OS:

| Platform | Also needs |
|---|---|
| Windows (`win-x64`, `win-arm64`) | nothing; `vulkan-1.dll` ships with any modern GPU driver |
| Linux (`linux-x64`, `linux-arm64`) | `libvulkan.so.1` + an ICD (`mesa-vulkan-drivers`), and a font package (`fonts-dejavu-core`) |
| macOS (`osx-arm64`, `osx-x64`) | MoltenVK (Vulkan-on-Metal) |

Or from source:

```
dotnet run --project src/FC.SDK.Viewer [output-directory]
```

An SDL3 + Vulkan GUI that exposes every control and action the SDK has, shows the raw event-stream property
cache next to the typed values, previews live view and captures, and writes a full timestamped log of every
PTP exchange (including which PTP operations your body advertises). If a body misbehaves, run this and attach
the log from the output directory.

Shortcuts: `F5` read all properties · `Space` capture · `Ctrl+L` live view · `Ctrl+D` dump properties ·
`Ctrl+±` text size.

There are two headless tools alongside it. `FC.SDK.Sample` is the fastest way to exercise a body at all, and
writes a device report every run; `--ioctl` selects the raw transport, so the two Windows paths are directly
comparable in one harness:

```
dotnet run --project src/FC.SDK.Sample -- [--ioctl] [--frames N] [--no-capture]
```

`FC.SDK.Diagnostics` is for questions the sample cannot answer: sequencing a body through an experiment and
judging it on delivered bytes rather than on a response code. Every mode brackets its result with controls,
because a run whose controls did not expose proves nothing:

```
dotnet run --project src/FC.SDK.Diagnostics -- --help
```

## Feature Matrix

| Feature | FC.SDK | Canon.EDSDK (.NET) | Canon EDSDK.dll (native) |
|---------|--------|-------------------|--------------------------|
| Take picture | yes | yes | yes |
| Bulb exposure | yes | yes | yes |
| Download CR2/CR3/JPEG | yes | yes | yes |
| Live view (MJPEG) | yes | yes | yes |
| Live view zoom + pan | yes | yes | yes |
| Live view autofocus | yes | yes | yes |
| Exposure histogram | yes | yes | yes |
| Manual focus (DriveLens) | yes | yes | yes |
| Read/write ISO, Tv, Av | yes | yes | yes |
| Event polling (GetEvent) | yes | yes | yes |
| Mirror lockup control | yes | yes | yes |
| WPD (zero-install, Windows) | yes | no (wraps EDSDK.dll) | internally, not exposed |
| USB (LibUsbDotNet, cross-plat) | yes | no | Windows only |
| WiFi (PTP/IP) | yes | no | yes |
| Linux / macOS | yes (USB, WiFi) | no | no |
| NativeAOT compatible | yes | yes | n/a |
| Redistributable | MIT | LGPL | no (Canon license) |
| Requires vendor binary | no | yes (EDSDK.dll) | yes (EDSDK.dll) |
| Requires driver swap (Zadig) | WPD: no, USB: yes | n/a | no |

**Canon.EDSDK** ([SharpAstro/Canon.EDSDK](https://github.com/SharpAstro/Canon.EDSDK)) is a .NET binding around Canon's native `EDSDK.dll`. It requires the vendor binary and only runs on Windows. FC.SDK reimplements the PTP protocol directly and needs no vendor DLLs.

## Supported Cameras

Verified on hardware: **Canon EOS 6D** (USB, WiFi, WPD) and **EOS 450D** (WPD). The 450D is where most of
the WPD, live-view, Custom Function and raw-ioctl work was proven, being DIGIC III and the strictest of the
two. Should work with any Canon EOS body that supports PTP, since the vendor opcodes are shared across the
lineup. Reported on the EOS 200D II / 250D / Rebel SL3 (DIGIC 8) in
[issue #1](https://github.com/SharpAstro/FC.SDK/issues/1); the protocol bugs behind that report are fixed but the
fix is unverified on that body, so please run the viewer and attach its log if anything still misbehaves.

**The two bodies disagreed on nearly every question asked of them**, which is the single most useful thing to
know before porting to a third. Mirror lockup is a Custom Function on the 450D and an ordinary property on the
6D. With it armed, the 450D discards every remote release including bulb, while the 6D honours it, but only
in a self-timer drive. The 450D has no `RemoteReleaseOn` (0x9128) at all; the 6D has it, and it turns out not
to autofocus there with either documented parameter. Where behaviour is model-specific the SDK infers it
from what the body announces rather than from a model-name table, and the docs say which body a measurement
came from rather than generalising from one.

If you have a body not listed, `CreateDeviceReportAsync` (or the viewer's device report) is the one artefact
worth attaching to an issue. It dumps every advertised operation, every announced property with its allowed
values, and the decoded Custom Function block **in wire order with menu numbers**, which is what makes it
possible to add a model without owning it, since a reporter can read menu numbers off their own camera.

## Docs

The write-ups behind the non-obvious parts, each recording what was measured and on what:

| | |
|---|---|
| [`canon-windows-transports.md`](docs/canon-windows-transports.md) | How EDSDK actually reaches an EOS body on Windows, and the dead hypotheses behind the live-view fix |
| [`wpd-ioctl-wire-format.md`](docs/wpd-ioctl-wire-format.md) | The ioctl buffer format decoded: Microsoft's undocumented property-bag serialization, not a Canon one |
| [`canon-live-view-zoom.md`](docs/canon-live-view-zoom.md) | Magnification is an operation, not a property, and the zoom rect is the only honest read-out |
| [`canon-custom-functions.md`](docs/canon-custom-functions.md) | The C.Fn wire layout, and why EDSDK is no help for per-model ids |
| [`edsdk-feature-gaps.md`](docs/edsdk-feature-gaps.md) | What is still missing against EDSDK. Read before concluding something is absent by design |

## AOT Compatible

Both Windows transports are NativeAOT compatible: no `dynamic`, no reflection, all COM interop via
`[GeneratedComInterface]`, and the ioctl path is plain P/Invoke. PTP/IP is sockets, so it is clean too.

One caveat, deliberately left visible rather than suppressed: **LibUsbDotNet 2.2.75 is not trim or AOT
annotated** and emits IL2104 + IL3053 on every publish. The cross-platform USB transport may therefore fail at
runtime in an AOT binary. Verify it against a real body before relying on it.

## License

MIT
