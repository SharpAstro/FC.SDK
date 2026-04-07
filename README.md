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

// Connect via WiFi
await using var camera = CanonCamera.ConnectWifi("192.168.0.1");
await camera.OpenSessionAsync();

// Set ISO and take a picture
await camera.SetPropertyAsync(EdsPropertyId.ISOSpeed, 0x00000068); // ISO 800
await camera.TakePictureAsync();

// Live view
await camera.StartLiveViewAsync();
var (error, jpeg) = await camera.GetLiveViewFrameAsync();
await camera.StopLiveViewAsync();

// Bulb exposure
await camera.BulbStartAsync();
await Task.Delay(TimeSpan.FromSeconds(30));
await camera.BulbEndAsync();

// Events
camera.ObjectAdded += (s, e) => Console.WriteLine($"New image: {e.ObjectHandle}");
camera.StartEventPolling();
```

## Architecture

```
CanonCamera              (public async API)
  CanonPtpSession        (Canon vendor opcodes 0x9xxx)
    PtpSession           (transaction management, half-duplex lock)
      IPtpTransport      (WPD / USB / PTP-IP)
```

## Supported Cameras

Tested with Canon EOS 6D. Should work with any Canon EOS body that supports PTP — the Canon vendor opcodes are shared across the EOS lineup.

## AOT Compatible

The entire library including the WPD transport is NativeAOT compatible — no `dynamic`, no reflection, all COM interop via `[GeneratedComInterface]`.

## License

MIT
