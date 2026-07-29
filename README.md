# FC.SDK — Free Canon SDK

Canon EOS camera control via PTP over USB and WiFi — pure managed C#, no EDSDK binary required.

## Why

Canon's EDSDK is closed-source, non-redistributable, and Windows-only. FC.SDK implements the same camera control capabilities using the PTP protocol (reverse-engineered by the [libgphoto2](https://github.com/gphoto/libgphoto2) project), working over USB and WiFi on any platform.

## Transports

| Transport | Platform | Driver swap? | Notes |
|---|---|---|---|
| **WPD** (Windows Portable Devices) | Windows | None — plug & play | Uses stock MTP driver, AOT compatible |
| **USB** (LibUsbDotNet) | Linux/macOS/Windows | WinUSB on Windows | Cross-platform, lowest latency |
| **PTP/IP** (WiFi) | All | None | TCP port 15740, no cable needed |

## Usage

```csharp
using FC.SDK;
using FC.SDK.Canon;

// Connect via WPD (Windows, zero-install)
await using var camera = CanonCamera.ConnectWpd(deviceId);
await camera.OpenSessionAsync();
Console.WriteLine($"{camera.Model} — serial {camera.SerialNumber}, battery {camera.BatteryLevelPercent}%");

// Typed settings — no magic uint32s
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

// Bulb exposure (mode dial must be on B)
await camera.SetMirrorLockupAsync(EdsMirrorUpSetting.On);
await camera.BulbStartAsync();
await Task.Delay(TimeSpan.FromSeconds(120));
await camera.BulbEndAsync();

// Live view + manual focus
await camera.StartLiveViewAsync();
var (error, jpeg) = await camera.GetLiveViewFrameAsync();
await camera.DriveLensAsync(EdsDriveLensStep.NearSmall);
await camera.StopLiveViewAsync();
```

## Architecture

```
CanonCamera              (public async API)
  CanonPtpSession        (Canon vendor opcodes 0x9xxx)
    CanonPropertyCache   (property mirror fed by the GetEvent stream)
    PtpSession           (transaction management, half-duplex lock)
      IPtpTransport      (WPD / USB / PTP-IP)
```

### How EOS properties are read

EOS bodies expose **no** property-read operation: standard PTP `GetDevicePropValue` (0x1015) is absent from
their supported-operations list, and Canon's 0x9127 is `RequestDevicePropValue` — it asks the camera to *emit*
a value, it does not return one. Values and their selectable-value lists arrive only as `PropValueChanged` /
`AvailListChanged` records in the `GetEvent` (0x9116) stream. `CanonPropertyCache` mirrors that stream and
`GetPropertyAsync` answers out of the mirror, requesting a push when a code has not been seen yet. Same design
as EDSDK and libgphoto2.

Two practical consequences: run `StartEventPolling` for the whole session, and expect `DeviceBusy` on property
writes if you don't (the SDK drains and retries, but the camera really does gate writes on an empty queue).

## Diagnostic viewer

Prebuilt, self-contained binaries are attached to every [release](https://github.com/SharpAstro/FC.SDK/releases)
— `fc-viewer-<rid>.zip` for Windows, `.tar.gz` for Linux and macOS. No .NET install needed. The archive
carries the SDL3 native; the Vulkan loader comes from the OS:

| Platform | Also needs |
|---|---|
| Windows (`win-x64`, `win-arm64`) | nothing — `vulkan-1.dll` ships with any modern GPU driver |
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

## Feature Matrix

| Feature | FC.SDK | Canon.EDSDK (.NET) | Canon EDSDK.dll (native) |
|---------|--------|-------------------|--------------------------|
| Take picture | yes | yes | yes |
| Bulb exposure | yes | yes | yes |
| Download CR2/CR3/JPEG | yes | yes | yes |
| Live view (MJPEG) | yes | yes | yes |
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

Tested with Canon EOS 6D. Should work with any Canon EOS body that supports PTP — the Canon vendor opcodes are
shared across the EOS lineup. Reported on the EOS 200D II / 250D / Rebel SL3 (DIGIC 8) in
[issue #1](https://github.com/SharpAstro/FC.SDK/issues/1); the protocol bugs behind that report are fixed but the
fix is unverified on that body — please run the viewer and attach its log if anything still misbehaves.

## AOT Compatible

The entire library including the WPD transport is NativeAOT compatible — no `dynamic`, no reflection, all COM interop via `[GeneratedComInterface]`.

## License

MIT
