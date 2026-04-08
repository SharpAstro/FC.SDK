# FC.SDK — Developer Guide

## Build

```
dotnet build
```

Requires .NET 10 SDK. No native dependencies needed to compile — LibUsbDotNet is a NuGet package, WPD COM interop is source-generated.

## Architecture

Four layers, bottom-up:

### Transport (`Transport/`)

`IPtpTransport` is the seam. Three implementations:

- **`WpdPtpTransport`** — Windows only. Uses WPD COM API via `[GeneratedComInterface]` (AOT safe). Talks to the camera through the stock MTP class driver (`wpdmtp.sys`) — no driver replacement needed. Does NOT use the raw `SendAsync`/`ReceiveAsync` path; instead exposes `ExecuteCommandAsync`, `ExecuteCommandReadDataAsync`, `ExecuteCommandWriteDataAsync` which map to the three WPD MTP extension command phases. The COM interfaces are defined in `WpdInterop.cs` with full vtable ordering.

- **`UsbPtpTransport`** — Cross-platform via LibUsbDotNet. Requires WinUSB driver on Windows (Zadig). Claims USB interface 0 (class 0x06 Still Image), uses Bulk-Out (EP2), Bulk-In (EP1), Interrupt-In (EP3). Canon VID = `0x04A9`.

- **`PtpIpTransport`** — WiFi/TCP. Two `TcpClient` connections to port 15740 (command + event). Four-way PTP/IP handshake in `ConnectAsync` using a random GUID. Camera acts as AP at `192.168.0.1`. First connection requires on-camera pairing approval (~1-2s timeout on 6D).

### Protocol (`Protocol/`)

- **`PtpPacket`** — `readonly ref struct` over `Span<byte>` for zero-copy read. Static factory methods write command/data containers. Wire format: 12-byte header (uint32 Length, uint16 Type, uint16 Code, uint32 TxId). All serialization via `BinaryPrimitives`, little-endian.

- **`PtpSession`** — Owns the transaction ID counter (`Interlocked.Increment`). `SemaphoreSlim(1)` enforces PTP half-duplex (one command in flight at a time). Uses `ArrayPool<byte>.Shared` for buffer rental. Three send patterns: command-only, command+data, command+receive-data.

- **`PtpErrorMapper`** — Maps `PtpResponseCode` to `EdsError`. PTP standard codes (0x2xxx) and Canon vendor codes (0xAxxx) both handled.

### Canon (`Canon/`)

- **`CanonPtpSession`** — Wraps all Canon vendor opcodes (0x9xxx range). Session lifecycle: `OpenSession(0x1002)` → `SetRemoteMode(0x9114, 1)` → `SetEventMode(0x9115, 1)`. Capture uses `RemoteReleaseOn/Off(0x9128/0x9129)` with param 0x01=AF, 0x02=shutter. Bulb wraps AF + `BulbStart(0x9125)` / `BulbEnd(0x9126)`.

- **`CanonPropertyMap`** — `FrozenDictionary<EdsPropertyId, (ushort PtpCode, int Size)>` mapping EDSDK property IDs to Canon PTP property codes (0xD1xx). Canon's `SetPropValue(0x9110)` sends an 8-byte data phase: `[propcode:u32][value:u32]`.

- **`EventPoller`** — Background `Task.Run` loop polling `GetEvent(0x9116)` every ~200ms. Events are variable-length records terminated by sentinel `{length=8, type=0}`. Decoded into `CanonPtpEvent` structs.

### Public API (root)

- **`CanonCamera`** — Entry point. Static factories: `ConnectUsb`, `ConnectWifi`. Async session/capture/live-view/property methods. Event handlers (`PropertyChanged`, `ObjectAdded`, `StateChanged`) dispatched from `EventPoller`.

## Canon PTP opcodes reference

| Opcode | Name | Data phase |
|--------|------|-----------|
| 0x1002 | OpenSession | none |
| 0x1003 | CloseSession | none |
| 0x9110 | SetPropValue | write: [propcode:u32][value:u32] |
| 0x9114 | SetRemoteMode | none, param=1 |
| 0x9115 | SetEventMode | none, param=1 |
| 0x9116 | GetEvent | read: event record list |
| 0x9117 | TransferComplete | none, param=objectHandle |
| 0x9125 | BulbStart | none |
| 0x9126 | BulbEnd | none |
| 0x9128 | RemoteReleaseOn | none, param=0x01(AF)/0x02(shutter) |
| 0x9129 | RemoteReleaseOff | none, param=0x01(AF)/0x02(shutter) |
| 0x9104 | GetObject | read: file data |
| 0x9151 | InitiateViewfinder | none |
| 0x9152 | TerminateViewfinder | none |
| 0x9153 | GetViewfinderData | read: JPEG frame (~160KB) |

## Canon PTP property codes

| PTP code | EdsPropertyId | Description |
|----------|--------------|-------------|
| 0xD101 | Av | Aperture |
| 0xD102 | Tv | Shutter speed |
| 0xD103 | ISOSpeed | ISO |
| 0xD105 | AEMode | Shooting mode |
| 0xD11C | SaveTo | Capture destination |
| 0xD1B0 | Evf_OutputDevice | Live view output |
| 0xD1BF | MirrorLockUpState | MLU state |
| 0xD1C1 | MirrorUpSetting | MLU on/off |

## WPD transport internals

The `WpdPtpTransport` uses three WPD MTP extension commands (by PID in the `{4d545058-...}` GUID):
- PID 12: Execute without data phase
- PID 13 → 15 → 17: Execute with data-to-read (initiate → read → end)
- PID 14 → 16 → 17: Execute with data-to-write (initiate → write → end)

COM objects created via `CoCreateInstance` P/Invoke + `StrategyBasedComWrappers`. All interfaces use `[GeneratedComInterface]` for AOT compatibility. No `dynamic`, no reflection.

## Testing

No automated tests yet — the library requires a physical Canon camera. Manual test sequence:
1. Connect camera (USB or WiFi)
2. Open session
3. Read battery level: `GetPropertyAsync(EdsPropertyId.BatteryLevel)`
4. Take picture: `TakePictureAsync()`
5. Live view: `StartLiveViewAsync()` → `GetLiveViewFrameAsync()` → verify JPEG
6. Bulb: `BulbStartAsync()` → delay → `BulbEndAsync()`

First test target: Canon EOS 6D (USB VID=0x04A9, PID=0x3215, WiFi AP at 192.168.0.1).
