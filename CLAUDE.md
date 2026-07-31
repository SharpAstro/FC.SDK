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

- **`CanonPtpSession`** — Wraps all Canon vendor opcodes (0x9xxx range). Session lifecycle: `OpenSession(0x1002)` → `GetDeviceInfo(0x1001)` → `SetRemoteMode(0x9114, 1)` → `SetEventMode(0x9115, 1)` → `SetRequestOLCInfoGroup(0x913D, 0x1fff)` → drain `GetEvent` → `RequestDevicePropValue(0x9127)` for the properties the camera never volunteers → drain again. Capture uses `RemoteReleaseOn/Off(0x9128/0x9129)` with param 0x01=AF, 0x02=shutter. Bulb wraps AF + `BulbStart(0x9125)` / `BulbEnd(0x9126)`.

- **`CanonPropertyMap`** — `FrozenDictionary<EdsPropertyId, (ushort PtpCode, int Size)>` mapping EDSDK property IDs to Canon PTP property codes (0xD1xx), plus the reverse map for naming raw codes in diagnostics dumps.

- **`CanonPropertyCache`** — Mirror of the camera's property state, fed **only** by the event stream. See "Reading and writing EOS properties" below; this is the single most load-bearing thing to understand about the Canon protocol.

- **`EventPoller`** — Background `Task.Run` loop calling `CanonPtpSession.DrainEventsAsync` every ~200ms. Decoding, cache updates and event dispatch all live in `PollEventsAsync`, so every consumer sees the same stream no matter who polled. Events are variable-length records terminated by sentinel `{length=8, type=0}`, decoded into `CanonPtpEvent` structs (which retain the raw payload for records wider than three words).

### Public API (root)

- **`CanonCamera`** — Entry point. Static factories: `ConnectUsb`, `ConnectWifi`, `ConnectWpd`. Async session/capture/live-view/property methods. Event handlers (`PropertyChanged`, `ObjectAdded`, `StateChanged`) are subscribed for the object's whole lifetime, not just while the poller runs, because the open-session drain and property reads also pull events. Diagnostics: `DumpPropertiesAsync`, `SupportedOperations`, `GetRawPropertyAsync`/`SetRawPropertyAsync`, `GetAllowedValuesAsync`.

## Reading and writing EOS properties

**There is no EOS "get property" operation.** EOS bodies do not list standard PTP `GetDevicePropValue` (0x1015) in
their supported-operations set at all, so calling it for a 0xD1xx code answers `OperationNotSupported` →
`EdsError.NotSupported`. Canon 0x9127 is `RequestDevicePropValue`, *not* a getter — it only asks the camera to emit
the value as an event. Values arrive exclusively as `PropValueChanged` (0xC189) records in the `GetEvent` (0x9116)
stream; the selectable-value lists arrive as `AvailListChanged` (0xC18A). Both feed `CanonPropertyCache`, and
`GetPropertyAsync` answers out of it (requesting a push + draining first if the code has not been seen, then falling
back to 0x1015 for pre-vendor-extension PowerShots). This mirrors EDSDK and libgphoto2 — `ptp_canon_eos_getdevicepropdesc`
is a pure cache lookup.

Consequences worth remembering:

- **The event pump is not optional.** An undrained event queue makes the camera answer `DeviceBusy` to
  `SetDevicePropValueEx`, so `SetPropertyUInt32Async` drains-and-retries on busy, and `TakePictureAsync` drains first.
- **`SetDevicePropValueEx` (0x9110) writes a 12-byte record**, not 8: `[size:u32][propCode:u32][value:u32]`, where
  `size` covers the whole record. Verified against libgphoto2 `ptp_canon_eos_setdevicepropvalue` and the decompiled
  EDSDK (`rev/EDSDK_decompiled.c:65711` bounds-checks `*param_3 > 0xb` before reading `param_3[1]` as the prop code).
  Omitting the size word is what made every property write fail with `DeviceBusy`.
- **Setting a property to the value it already holds returns `DeviceBusy`** on at least CaptureDestination, so writes
  are skipped when the cache already agrees.
- **A body answers `OK` to writes of properties it does not have.** A 450D ACKs `SetDevicePropValueEx` for 0xD13A
  (MirrorUpSetting) — a property it never announces — and does nothing. A write's response code therefore proves
  nothing; `SetPropertyUInt32Async` only mirrors a write into the cache when the camera has previously announced the
  property, otherwise the phantom value answers every later read. This masked the C.Fn fallback for a whole debugging
  session: the "property write" path reported success, so the block write below never even ran.
- **`SaveTo` is not passed through.** EDSDK numbers it Camera=1/Host=2/Both=3; the PTP CaptureDestination property
  (0xD11C) uses Host=4 and takes the card value from the body's own allowed-value list (2 in practice). Sending EDSDK's
  Host=2 selects the *card*. `CanonCaptureDestination` holds the wire values and `SetSaveToAsync` translates.
- **Capturing to the host needs `PCHDDCapacity` (0x911A)** with `(0x0FFFFFFF, 0x1000, 1)`, otherwise `AvailableShots`
  stays at 0 and the body refuses to release. `SetCaptureDestinationAsync` sends it automatically.
- **`SetRequestOLCInfoGroup` (0x913D, mask 0x1fff)** is required on newer bodies before they report Tv/Av/ISO and AF
  state via `OLCInfoChanged` (0xC1A5).

## Canon PTP opcodes reference

| Opcode | Name | Data phase |
|--------|------|-----------|
| 0x1002 | OpenSession | none |
| 0x1003 | CloseSession | none |
| 0x910F | RemoteRelease | none — single-shot release on bodies without 0x9128/0x9129 |
| 0x9110 | SetDevicePropValueEx | write: [size:u32][propcode:u32][value:u32] — size covers the record (12 for a scalar) |
| 0x9114 | SetRemoteMode | none, param=1 (EOS M / newer PowerShots want 0x15 — not implemented) |
| 0x9115 | SetEventMode | none, param=1 |
| 0x9116 | GetEvent | read: event record list |
| 0x9117 | TransferComplete | none, param=objectHandle |
| 0x911A | PCHDDCapacity | none, params=(freeClusters, bytesPerSector, reset) |
| 0x911B / 0x911C | SetUILock / ResetUILock | none |
| 0x911D | KeepDeviceOn | none |
| 0x9125 | BulbStart | none |
| 0x9126 | BulbEnd | none |
| 0x9127 | RequestDevicePropValue | none, param=propcode — a *request to emit*, NOT a getter |
| 0x9128 | RemoteReleaseOn | none, param=0x01(AF)/0x02(shutter) |
| 0x9129 | RemoteReleaseOff | none, param=0x01(AF)/0x02(shutter) |
| 0x9130 | ResetMirrorLockupState | none |
| 0x913D | SetRequestOLCInfoGroup | none, param=group mask (0x1fff = all) |
| 0x9104 | GetObject | read: file data |
| 0x9151 | InitiateViewfinder | none |
| 0x9152 | TerminateViewfinder | none |
| 0x9153 | GetViewfinderData | read: JPEG frame (~160KB), **params=(0x00200000, 0, 0)** — the first is the max payload size. An empty param list is accepted and answers OK forever with a zero-length frame; see `docs/wpd-ioctl-wire-format.md` |
| 0x9160 | AfCancel | none |

Seen on the wire from EDSDK but not yet identified: 0x100A, 0x100E, 0x9101, 0x9102, 0x9103,
0x9107, 0x9109.

## Canon PTP property codes

All PTP property codes MUST be verified against [libgphoto2 ptp.h](https://github.com/gphoto/libgphoto2/blob/master/camlibs/ptp2/ptp.h) (`PTP_DPC_CANON_EOS_*` defines) before adding to `CanonPropertyMap`. Do not guess codes from EDSDK property IDs — the mapping is not sequential and many codes are unintuitive (e.g. 0xD14B is GPSLogCtrl, not NoiseReduction; 0xD1C1 is AloMode, not MirrorUpSetting).

| PTP code | EdsPropertyId | Description |
|----------|--------------|-------------|
| 0xD101 | Av | Aperture |
| 0xD102 | Tv | Shutter speed |
| 0xD103 | ISOSpeed | ISO |
| 0xD105 | AEMode | Shooting mode |
| 0xD114 | AutoPowerOffSetting | Auto power-off timeout |
| 0xD11C | SaveTo | Capture destination |
| 0xD13A | MirrorUpSetting | MLU on/off |
| 0xD178 | NoiseReduction | High ISO NR |
| 0xD1AB | TempStatus | Sensor/body temperature |
| 0xD1B0 | Evf_OutputDevice | Live view output |
| 0xD1B2 | Evf_DepthOfFieldPreview | DoF preview in LV |
| 0xD1BF | MirrorLockUpState | MLU state |

Long exposure noise reduction has NO direct PTP property on Canon — it is always a Custom Function. Use the `CanonCustomFunctionBlock` API.

### Custom Function block, verified on a 450D

Full write-up in **`docs/canon-custom-functions.md`**: the wire layout confirmed against both a real
450D dump and Canon's own manuals, and why EDSDK is no help for per-model ids (its C.Fn writer takes
the id from its *caller* and linearly searches the block — there is no per-model table in the DLL,
and the whole 0xD180–0xD1A0 range is absent from EDSDK's own property table).

- **Reads and writes both work** via 0xD1A0 (`CustomFuncEx`): the write is 0x9110 with the whole modified block as
  payload (`[8+total_size][0xD1A0][block]`), same as EDSDK. No UILock needed.
- **The group header is three words** — `[group_id][group_size][entry_count]`, entries at `+12`.
  `group_size` excludes its own word and only balances under this reading. EDSDK's writer appears to
  use two words, but it walks an already-parsed in-memory form, not the wire block.
- **Which settings live here is per-model AND per-setting.** From the manuals: a 450D keeps long-exposure
  NR, high-ISO NR and mirror lockup as C.Fn; a 6D has all three as ordinary properties; a 200D II has
  the NR pair as properties but mirror lockup still as C.Fn-5. Do not assume they move together.
- **Extending to a new body needs a menu number, not a raw id.** Group order and entry order match the
  camera's own C.Fn menu (verified item-for-item on a 450D), so a reporter can dump the block — the
  viewer's C.Fn action already logs it — and say which menu number does what.
- **Verify writes with a fresh read-back** (request 0xD1A0 → drain, bypassing the cache) after a ~2.5 s window —
  a response code alone proves nothing on a body that also ACKs phantom property writes. `SetMirrorLockupAsync`
  returns `OperationRefused` when the camera kept its old value.
- **Mirror lockup on the 450D is C.Fn 0x060F** (no 0xD13A/0xD1BF properties). The mapping is per-body:
  `CanonCustomFunctionId.MirrorLockupIdFor(model)` — never guess ids for unverified bodies.
- **High-ISO NR on the 450D is C.Fn 0x0202** (no 0xD178 property), and it is two-state Off/On rather than the
  four-level `EdsHighIsoNR` scheme. The SDK translates by meaning: Off ↔ `Disable`, On ↔ `Standard`.
- **A 450D silently ignores RemoteRelease (0x910F) while MLU is enabled**: the command answers OK, no mirror moves,
  no event is emitted, no exposure ever arrives. Remote MLU capture is not possible on this body — the viewer warns
  instead of pretending a second press will expose. MLU state on such bodies is Enable/Disable derived from the
  setting; there is no way to know the mirror's actual position.

## WPD transport internals

The `WpdPtpTransport` uses three WPD MTP extension commands (by PID in the `{4d545058-...}` GUID):
- PID 12: Execute without data phase
- PID 13 → 15 → 17: Execute with data-to-read (initiate → read → end)
- PID 14 → 16 → 17: Execute with data-to-write (initiate → write → end)

COM objects created via `CoCreateInstance` P/Invoke + `StrategyBasedComWrappers`. All interfaces use `[GeneratedComInterface]` for AOT compatibility. No `dynamic`, no reflection.

### Critical WPD requirement: always include operation params

The WPD MTP driver **requires** `WPD_PROPERTY_MTP_EXT_OPERATION_PARAMS` (an `IPortableDevicePropVariantCollection`) to be present in the command property bag for ALL MTP extension commands, **even when there are no PTP parameters**. Without the empty collection, vendor data-READ commands (GetEvent 0x9116, GetObject 0x9104, etc.) fail with `ELEMENT_NOT_FOUND (0x80070490)`. Vendor no-data and data-WRITE are unaffected.

This was discovered by tracing `PortableDeviceApi.dll!SendCommand` and comparing EDSDK's property bags to ours. EDSDK always includes an empty `IPortableDevicePropVariantCollection` for the params property. No registry changes, no special drivers, no special client info needed — just the empty collection.

In `SetOperationParams`, the collection is always created and attached regardless of whether `params` is empty.

### Below the COM API: the raw ioctl path

EDSDK does not use the WPD COM API at all — it opens the same device-interface path with
`CreateFileW` and drives the MTP driver with `DeviceIoControl`. Two docs cover this:

- **`docs/canon-windows-transports.md`** — how EDSDK reaches the body (measured), the two
  ioctl codes, the async I/O shape, and the six dead hypotheses behind the current per-frame
  device-object fix in `ExecuteViewfinderRead`.
- **`docs/wpd-ioctl-wire-format.md`** — the ioctl buffer format, decoded. It is *not* a Canon
  format: it is Microsoft's own undocumented WPD property-bag serialization (a recursive
  PROPVARIANT using real OLE VARTYPEs), confirmed by diffing our own COM traffic against EDSDK's
  ioctls for identical calls. Every PROPERTYKEY involved is already a constant in `WpdInterop.cs`.

**This is proven, not theoretical.** `rev/RawIoctlPoc` (git-ignored, needs the camera exclusively)
drives a 450D end to end with zero COM — session setup, property writes via the three-phase
data-WRITE, and live view at **11.4 fps sustained over one handle**, versus ~5 fps through COM.

The payoff if it ever gets adopted: `ExecuteViewfinderRead` currently creates, opens, closes and
releases a whole `IWpdDevice` **per live-view frame**, purely because an unfinished transfer
poisons a COM device object while `END_DATA_TRANSFER` itself never returns. Neither happens over
raw ioctl — the same end phase completes in 0–22 ms — so the poisoning is a COM-layer artifact, not
a `wpdmtp.sys` one.

If picked up, keep the scope narrow: session, properties, capture and events work fine over COM and
should stay there; only the viewfinder read is worth moving, and `ExecuteCommandReadDataAsync`
already special-cases `CanonGetViewfinderData` onto its own path as the seam. Two things are
untested: whether a COM device object and a raw handle to the same node coexist, and whether any of
this generalises beyond a DIGIC III 450D.

## Diagnostic viewer (`src/FC.SDK.Viewer`)

`dotnet run --project src/FC.SDK.Viewer [output-directory]` — an SDL3 + Vulkan GUI (sibling
`../SdlVulkan.Renderer` + `../DIR.Lib`) that exposes every mapped control, every action, and the raw
event-stream property cache side by side, and writes a timestamped log of every PTP exchange to
`<output-directory>/fc-viewer-*.log`. That log is what to ask a bug reporter for.

- Layout is **declarative only** — `Layout.Node` trees painted through `PixelWidgetBase.RenderLayout`,
  which binds each click region to the rect it drew. No hand-rolled hit rectangles. Row virtualization
  is delegated to `ListScrollController`; the only arithmetic in the widget is letterboxing an image,
  which depends on image dimensions rather than layout.
- **Fonts are a trio, not one file** (`ViewerFonts`, same shape as `drawboard/pdf-viewer`): a platform
  text face, a symbol face, an emoji face. A platform UI face is narrower than it looks — Segoe UI
  carries `→ — ·` but *none* of `◀ ▶ ☑ ☐ ✓ ✗ ⟵ ⟶ ⏳ 📷`, all of which live in Segoe UI Symbol.
  Coverage is read from each candidate's cmap via `OpenTypeFont.GetGlyphId`, so `ViewerGlyphs` returns
  the real glyph when something can draw it and an ASCII stand-in otherwise — never a blank box.
- **`FontResolver.ResolveInstalledFont` cannot find these faces by family.** It consults a hard-coded
  standard-family table and otherwise probes only `<family>.ttf`, so it resolves `Segoe UI` but never
  `Segoe UI Symbol` (the file is `seguisym.ttf`). `ViewerFonts` therefore falls back to matching *file
  names* against `FontResolver.EnumerateInstalledFonts()`. Same class of gap as pdf-viewer's issue #111
  for macOS `.ttc` collections.
- **Symbol glyphs go through the `Fill` escape hatch**, not a text leaf: `PaintLayout` draws a whole
  text leaf with one font, and per-run fallback in that path is
  [DIR.Lib#29](https://github.com/SharpAstro/DIR.Lib/issues/29). `ViewerWidget.Glyph(...)` emits a
  `Fill` leaf whose rect still comes from arrange; only the font is chosen by the widget. Once #29
  lands these can collapse back into ordinary `Text` leaves.
  Supplementary-plane pictographs (📷, U+1F4F7) need none of this — `PixelWidgetBase.EmojiFontPath`
  routes them automatically, but *only* above U+FFFF (it tests for a surrogate pair).
- **A nested `RenderLayout` must forward `drawFill`.** Without it a `Fill` leaf still arranges and
  reserves its space, then nothing paints it — a silent blank, no error. This bit the panel rows once.
- `DebugInspector.Attach` is wired under `#if SIBLING_DEBUG_INSPECTORS` — **not** plain `DEBUG`. The
  type is `#if DEBUG` upstream, so it exists only in a Debug-compiled sibling; the published package is
  Release and does not contain it. See `src/Directory.Build.props`. It lets the app answer `screenshot` /
  `describe_ui` / synthesized clicks over loopback, which is how the UI gets verified without a camera.

## Sibling working copies

`src/Directory.Build.props` owns one `UseLocalSiblings` switch for the whole repo: when the Codecs,
SdlVulkan.Renderer and DIR.Lib working copies all exist next to this one, every project uses
`ProjectReference`; otherwise all of them use `PackageReference`. All-or-nothing on purpose — a half-local
graph resolves the same assembly from both a project and a package. Override with
`-p:UseLocalSiblings=false`, and check that override builds in **Debug** too, since that is the
combination the `SIBLING_DEBUG_INSPECTORS` guard exists for.

`SIBLING_DEBUG_INSPECTORS` is defined in `src/Directory.Build.targets`, **not** `.props`:
`Directory.Build.props` is imported before the SDK assigns `$(Configuration)` its default, so a
`'$(Configuration)' == 'Debug'` test there reads empty on any build that did not pass `-c` explicitly —
and the constant silently goes missing on a plain `dotnet build`. Any other Configuration-dependent
property in this repo belongs in `.targets` for the same reason.

## Versioning and releasing

Two schemes, because they answer to different audiences:

- **Libraries** (NuGet) are `major.minor.<run_number>` — every push to main publishes a distinct
  version, so the run number rides along as the revision.
- **The viewer, the release tag, and anything quoted to a user** are *only* `major.minor`: `1.5`, then
  `1.6`. `VERSION_LINE` in the workflow is the one number a human picks.

To release: run the workflow manually (`gh workflow run dotnet.yml`, or the Actions UI). That triggers
`publish-viewer` (NativeAOT, six RIDs) and `release`, which attaches `.zip` for Windows RIDs and
`.tar.gz` for the rest, and creates or **updates** the `v$VERSION_LINE` release. Updating in place is
deliberate: the download URLs get quoted in issues, so re-publishing binaries into `1.5` has to keep
the same links working.

Do **not** push a release tag by hand — the tag comes from `VERSION_LINE`, so a hand-pushed `v1.6`
against `VERSION_LINE: '1.5'` would trigger a run that creates a release called `v1.5`. Bump
`VERSION_LINE` (and the `VERSION_PREFIX` stem alongside it) instead.

The NuGet `publish` job is gated to `push` on `main` only. It used to have no condition at all, so every
pull request published a package from unreviewed code and consumed the version number the merge would
have used — that cost `1.5.531`. NuGet versions can be unlisted but never replaced, so keep that gate.

Two things to know about the AOT build:

- **`LibUsbDotNet` 2.2.75 is not trim/AOT annotated** and emits IL2104 + IL3053 on every publish. The
  warnings are left visible because the risk is real: the cross-platform USB transport may fail at
  runtime in an AOT binary. WPD (Windows) and PTP/IP (WiFi) go through `[GeneratedComInterface]` and
  sockets respectively and are AOT-clean. Verify USB against a real body before claiming it works.
- **No `Enum.GetValues(Type)`** in viewer code — it is `RequiresDynamicCode` (IL3050). Use
  `Enum.GetValuesAsUnderlyingType(Type)`, which is what `CameraControl.CycleValues` does.

## Testing

Automated tests cover `FC.SDK.Raw` only; the device layer needs a physical Canon body. Manual sequence
(all of it available as buttons in the viewer):
1. Connect camera (WPD, USB or WiFi)
2. Open session — check the log's operation-support list, especially whether 0x1015 is absent
3. Read all properties and confirm the event cache is populated
4. Take picture: `TakePictureAsync()`
5. Live view: `StartLiveViewAsync()` → `GetLiveViewFrameAsync()` → verify JPEG
6. Bulb: `BulbStartAsync()` → delay → `BulbEndAsync()`

Test targets so far: Canon EOS 6D (USB VID=0x04A9, PID=0x3215, WiFi AP at 192.168.0.1) and
EOS 450D (USB PID=0x3145) — the 450D is the body most of the WPD, live-view, C.Fn and raw-ioctl
findings were verified on, and being DIGIC III it is the strictest of the three.
Reported working under EDSDK but previously failing here: EOS 200D II / 250D / Rebel SL3 (DIGIC 8) —
see the property-read and property-write notes above; both symptoms in issue #1 trace back to them.

**A device-layer result measured against an idle camera is worthless.** Two separate times this
session a change looked confirmed over hundreds of iterations that were all no-ops — an empty
parameter list made `GetViewfinderData` answer OK forever with zero-length frames, and
`InitiateViewfinder` without `Evf_OutputDevice=PC` left the mirror down while every read "succeeded".
Assert on real bytes (a JPEG SOI, a non-zero length, a plausible frame rate), never on a response
code or an iteration count.

## Reverse-engineering material (`rev/`, git-ignored)

`rev/` holds decompiles, API-tracing probes, raw captures and throwaway test projects. It is in
`.gitignore` on purpose — the *conclusions* belong in `docs/`, the raw material stays local. Notable
tooling, all of which expects the camera to be present and not held by another app:

- `extract_propid_map.py` — pulls EDSDK's PTP-code ↔ `EdsPropertyId` table out of `.rdata`
  (234 pairs; corroborates `CanonPropertyMap`, 0 conflicts). Needs a local `EDSDK.dll`.
- `edsdk_ioctl_bytes_probe.js` + `decode_wpd_ioctl.py` — capture and decode raw ioctl buffers.
- `wpd_raw_ioctl_poc.py` / `RawIoctlPoc/` — the working raw-ioctl transport proof (see the WPD
  section above). The C# one is the maintained one.
