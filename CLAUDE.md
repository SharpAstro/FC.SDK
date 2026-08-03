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

- **`CanonPtpSession`** — Wraps all Canon vendor opcodes (0x9xxx range). Session lifecycle: `OpenSession(0x1002)` → `GetDeviceInfo(0x1001)` → `SetRemoteMode(0x9114, 1)` → `SetEventMode(0x9115, 1)` → `SetRequestOLCInfoGroup(0x913D, 0x1fff)` → drain `GetEvent` → `RequestDevicePropValue(0x9127)` for the properties the camera never volunteers → drain again. Capture uses `RemoteReleaseOn/Off(0x9128/0x9129)`; param1 is the press stage (1=half, 2=full, 3=half+full), and we do not yet send param2 (0=AF, 1=MF). Bulb wraps AF + `BulbStart(0x9125)` / `BulbEnd(0x9126)`.

- **`CanonPropertyMap`** — `FrozenDictionary<EdsPropertyId, (ushort PtpCode, int Size)>` mapping EDSDK property IDs to Canon PTP property codes (0xD1xx), plus the reverse map for naming raw codes in diagnostics dumps.

- **`CanonPropertyCache`** — Mirror of the camera's property state, fed **only** by the event stream. See "Reading and writing EOS properties" below; this is the single most load-bearing thing to understand about the Canon protocol.

- **`EventPoller`** — Background `Task.Run` loop calling `CanonPtpSession.DrainEventsAsync` every ~200ms. Decoding, cache updates and event dispatch all live in `PollEventsAsync`, so every consumer sees the same stream no matter who polled. Events are variable-length records terminated by sentinel `{length=8, type=0}`, decoded into `CanonPtpEvent` structs (which retain the raw payload for records wider than three words).

### Public API (root)

- **`CanonCamera`** — Entry point. Static factories: `ConnectUsb`, `ConnectWifi`, `ConnectWpd`, `ConnectWpdIoctl`. Async session/capture/live-view/property methods. Event handlers (`PropertyChanged`, `ObjectAdded`, `StateChanged`) are subscribed for the object's whole lifetime, not just while the poller runs, because the open-session drain and property reads also pull events. Diagnostics: `DumpPropertiesAsync`, `SupportedOperations`, `GetRawPropertyAsync`/`SetRawPropertyAsync`, `GetAllowedValuesAsync`, `CreateDeviceReportAsync`.

- **`CanonDeviceReport`** — the one artefact to ask a bug reporter for. Markdown: model, transport, every advertised operation, every announced property with its allowed values, and the **decoded Custom Function block in wire order with menu numbers**. That last part is the whole point — a reporter cannot know wire ids, but can read menu numbers off their own camera, and `CanonCustomFunctionBlock.Entries` preserves the order that maps between them. It also refuses to be trusted on a flat battery: a body reporting `0xD111` level ≤ 1 gets a loud warning, because at that level a 450D has been seen to drop live view mid-stream and announce dial movements nobody made. (It does *not* stop capturing — that symptom turned out to be mirror lockup; see the Testing section.)

## What is still missing vs EDSDK

**`docs/edsdk-feature-gaps.md`** is the tracked list — read it before concluding that something is
absent by design, and update it when a gap closes. Still open at 3.0: no PTP filesystem (six opcodes
declared, zero uses), 31 of EDSDK's 234 property pairs mapped, no movie control, no hotplug.

## Live-view magnification, verified on a 6D

Full write-up in **`docs/canon-live-view-zoom.md`**. Three things that are easy to get wrong:

- **Zoom is an operation, not a property.** `0x9158 Zoom` (1 arg) and `0x9159 ZoomPosition` (2 args:
  x, y). EDSDK exposes them as `Evf_Zoom` (0x507) / `Evf_ZoomPosition` (0x508) and **no such PTP
  property exists** — do not map those ids. `../tianwen` deferred its whole DSLR planetary mode
  waiting for a point/rect property accessor that was never needed.
- **`Evf_AFMode` (0xD1BA, libgphoto2 `LvAfSystem`) can silently gate magnification** — and it is a
  condition, not a rule. *With a lens attached*, the factory-default `LiveFace` makes the zoom
  operation answer `OK` while the feed stays at full frame; `Live` and `Quick` crop. *With no lens*
  (a body on a telescope) the same body magnifies in all three, and the pan inset disappears too. So
  do not encode the rule: `SetEvfZoomAsync` verifies against the zoom rect and answers
  `OperationRefused` only when the frame really did not crop — same pattern as the mirror-lockup
  refusal, and it gets both configurations right without knowing which it is in. Set `Live` anyway;
  it worked in every configuration measured.
- **The zoom rect is the only honest read-out**, and it comes from the live-view envelope, not a
  property: record type 18 is `[x][y][w][h]`, type 14 the sensor size. The factor is a *threshold*
  (5–8 all give 5×) and "5×" is really 4.96×, so read `GetEvfZoomRectAsync` rather than trusting what
  was asked for. Panning is exact and silently clamped, and the body reports where it landed.

**Allowed values are what you may *write*, not what a property can *report*.** `Evf_AFMode`/`AFMode`
(0xD108) follows the lens's own AF/MF switch and reads `ManualFocus` when it is at MF — a value
absent from its own allowed-value list. Reading that list as "the values this property can hold" is
what made the switch look undetectable. `GetFocusStateAsync` combines the focus mode with lens
presence (from `LensName`, empty on a bare mount) to answer the question that actually matters:
**can this camera autofocus at all right now** — false on a telescope and false at MF, and in both
cases an AF command answers `OK` and moves nothing.

**Property reads are typed now.** `CanonPropertyMap` carries a `CanonPropertyType` instead of a dead
`Size` field, and `GetPropertyAsync` refuses a non-scalar rather than answering `OK` with the first
four bytes of a string read as an integer — which is what `OwnerName`, `LensName`, `Artist` and
`Copyright` did before 3.0. Use `GetPropertyStringAsync` / `GetPropertyBytesAsync`; EOS strings are
plain null-terminated ASCII, **not** PTP length-prefixed UTF-16.

## Reading and writing EOS properties

**There is no EOS "get property" operation.** Calling standard PTP `GetDevicePropValue` (0x1015) for a 0xD1xx code
answers `OperationNotSupported` → `EdsError.NotSupported`. Note this is *not* the same as the operation being absent:
a 450D advertises 0x1015 in its supported-operations set and still refuses every vendor code, so its presence in a
device report proves nothing and must not be read as "the property path will work here".
Canon 0x9127 is `RequestDevicePropValue`, *not* a getter — it only asks the camera to emit
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
- **The destination decides which event announces the image, and both must be handled.** A card-destination frame
  arrives as `ObjectAddedEx` (0xC181/0xC1A7); a host-destination frame as `RequestObjectTransfer` (0xC186/0xC1A9),
  because the body is holding it in RAM for the host to fetch. `CanonCamera.AnnouncesNewImage` covers all four and
  `ObjectAdded` fires for any of them — matching EDSDK's `kEdsObjectEvent_DirItemCreated` /
  `_DirItemRequestTransfer` pair. Listening only for the card family is a silent failure with a nasty tail: the
  exposure happens, no callback fires, and **the un-fetched frame occupies the body until `TransferComplete` (0x9117)
  arrives**. Two of them were enough to make a 450D answer `DeviceBusy` to every later release, drop out of live view
  immediately after entering it, and refuse to finish powering off (the top LCD froze on the remaining-shots count) —
  recoverable only by pulling the battery. Diagnosed for most of a session as a camera that had stopped releasing.
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
| 0x9128 | RemoteReleaseOn | none, **two** params per libgphoto2: p1 = 1 half-press / 2 full-press / 3 half+full in one go, p2 = 0 AF / 1 MF. We send only p1 — so every release implies AF, and **`p2=1` is the un-wired way to release without it**, which is what a manual lens or a telescope wants |
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
  no event is emitted, no exposure ever arrives. Re-confirmed 2026-08-02 on clean evidence (battery level 1, body
  demonstrably still exposing minutes earlier): the discard is recognisable by timing — 0x910F returns in ~20 ms
  against the ~2.1 s a real release sequence takes. Also discarded in 2-s self-timer drive (the manual's own
  MLU workaround for the physical button — it does NOT extend to PTP releases) and on a double press. The viewer
  warns instead of pretending a second press will expose. MLU state on such bodies is Enable/Disable derived from
  the setting; there is no way to know the mirror's actual position.
- **Remote bulb WORKS on a 450D — verified end to end.** `EdsTv.Bulb` is **0x0C** (0x04 is the old
  PowerShot-protocol value); it is the 53rd entry in the body's own allowed list and an ordinary property write
  flips the top LCD to buLb. Bare `BulbStart`/`BulbEnd` (0x9125/0x9126) then delivered a real exposure whose own
  EXIF read 4.0 s — exactly the start→end interval. No UILock needed. What made bulb look unsupported for a whole
  session: `BulbStartAsync` unconditionally sent an AF prelude via 0x9128, which this body does not have, so the
  wrapper failed with NotSupported *before 0x9125 was ever sent*. The prelude is now gated on 0x9128 support.
- **MLU discards bulb too, and no two-command sequence gets around it.** Settled on a *fully charged* battery
  (level 2) with `rev/MluMatrix`, six sequences bracketed by controls that both delivered 11.2 MB CR2s minutes
  either side:

  | | MLU armed | MLU off (control) |
  |---|---|---|
  | `0x910F` | nothing | 11.2 MB |
  | `0x910F`, 3 s, `0x910F` (the two-press analogue) | nothing | — |
  | `BulbStart`, 1 s, `0x910F`, 4 s, `BulbEnd` | nothing | — |
  | `0x910F`, 2 s, `BulbStart`, 4 s, `BulbEnd` | nothing | — |
  | `BulbStart`, 2 s, `BulbStart`, 4 s, `BulbEnd` | nothing | — |
  | `BulbStart`, 4 s, `BulbEnd`, 1.5 s, `0x910F` | nothing | — |
  | self-timer drive `0x10` (2 s) | nothing | **11.2 MB** |
  | self-timer drive `0x07` (10 s) | nothing | **11.2 MB ×6 burst** |
  | self-timer drive `0x11` (unnamed) | nothing | **11.2 MB** |
  | `BulbStart`, 3 s, `BulbEnd` | — | 11.2 MB |

  **A 6D is the opposite case, and it needs a self-timer.** Where a 450D discards, a 6D honours mirror
  lockup over PTP — but *only* in a self-timer drive mode, which supplies the settle. In `Single`
  drive the same body simply shoots: an armed release delivers a frame in 0.4 s, and two presses give
  two frames rather than raise-then-expose. So **mirror lockup + self-timer is the working recipe, and
  mirror lockup alone is silently ignored** — the worse failure of the two, since the caller gets a
  frame and believes they had lockup. Working recipe, measured:

  | 6D drive | `EdsDriveMode` name | real settle | lockup engages? |
  |---|---|---|---|
  | `0x10` | `Timer_2sec_RemoteControl` | **10.3 s** | yes |
  | `0x11` | *unnamed by the enum* | **2.9 s** | yes |

  **The enum's names are simply wrong here** — `0x10` is the ten-second timer and the unnamed `0x11`
  is the two-second one. `0x11` is therefore the one to use: native firmware sequencing (the body
  owns raise → settle → expose, so a dead host cannot strand the mirror up) with ~2 s of dead time.

  **`OLCInfoChanged = 0x15` at +0.2 s is the lockup marker**, and it is the only thing that
  distinguishes the two cases — delivered files, frame sizes, `0xD1BF`, and total timing are all
  identical. With lockup off the same slot carries `0x10`→`0xD`. Reproduced on both drive modes, so
  it is independent of timer length. Corroborated three ways on `0x10`: the marker, an audible mirror
  at T+0, and a **viewfinder that stayed black for the entire countdown** — that last one is the
  cheapest conclusive probe and should be the first one reached for next time. Note `0x15` is an OLC
  group mask, not a documented mirror flag; it earns its meaning from the visual confirmation.

  This is the mechanism behind NINA's "mirror lockup delay", except NINA's delay is user-configurable
  and this one is whatever the body's timer is. It cannot be otherwise: 0x9128's second parameter was
  the standing hope for an arbitrary settle, and libgphoto2 documents it as AF/MF, not a delay. There is
  no known way to ask an EOS for a settle of one's choosing over PTP.

  **The SDK now refuses rather than relaying the camera's empty ACK.** `TakePictureAsync` and
  `BulbStartAsync` answer `OperationRefused` in 0 ms — before anything reaches the wire — when mirror
  lockup is armed and `CanonCamera.SupportsMirrorLockupCapture` is false. That flag is inferred from
  **where the body keeps the setting**: a Custom Function means no mirror-lockup capture over PTP, the
  real `0xD13A` property means it is fine. Both bodies measured agree, and NINA draws the same line
  with the same advice ("turn MLU off under the camera's Custom Function menu"), which is independent
  corroboration from an EDSDK client. The inference is over n=2, so the property has a setter to
  overrule it, and an un-probed camera is assumed capable — refusing on no evidence would be worse
  than the bug it replaces.

  **The self-timer rows have their own controls and need them.** "Nothing with MLU on" would prove
  nothing if the body simply ignored self-timer drive on a PTP release — so each was re-run with
  lockup off, and all three delivered. The timer genuinely self-releases over PTP; only lockup stops
  it. Two incidental findings from those controls: drive `0x07` fires a **six-shot burst**, not one
  frame (enough un-fetched frames to wedge the body — see the capture-destination note above), and
  `0x910F` blocks ~5 s in timer modes regardless of the countdown length, so its duration is not a
  countdown measurement. Take drive values off the body's own allowed list, never from
  `EdsDriveMode`: a 450D offers `0x11`, which that enum does not name.

  The physical body needs two shutter presses in MLU — raise, then expose — so every row is some way of spelling
  that over PTP, including a release *inside* an open bulb window. **All six are discarded**, and the mechanism is
  visible rather than inferred: with MLU off `BulbStart` immediately emits `BulbExposureTime` ticks (0,1,2,3…);
  with MLU on it emits none at all, so the exposure never begins — the body is not starting-and-failing, it is
  refusing at the door. Each test was preceded by a 35 s settle, because a 450D drops a raised mirror by itself
  after ~30 s and without the gap one test's "first press" is really the previous one's second. **Every remote
  release path is firmware-discarded while C.Fn mirror lockup is armed on this body**; the viewer warns rather
  than pretending a second press will expose.
- **A silent command is not evidence until something mechanical is shown to work in the same minutes.** This body
  has now twice produced a whole session of confident wrong conclusions from ACKs alone. Live view start is the
  loudest cheap vitality probe (mirror flip + real frames); a control exposure either side of a negative result is
  better. Battery level in particular proves nothing on its own — level 0 streamed live view and delivered bulb
  exposures, and the level-0 MLU results reproduced exactly at level 2.

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

### Below the COM API: the raw ioctl path (`WpdIoctlPtpTransport`)

EDSDK does not use the WPD COM API at all — it opens the same device-interface path with
`CreateFileW` and drives the MTP driver with `DeviceIoControl`. **FC.SDK now does this too**, as a
sibling transport: `CanonCamera.ConnectWpdIoctl(deviceId)` next to `ConnectWpd(deviceId)`. Two docs
cover the reverse engineering behind it:

- **`docs/canon-windows-transports.md`** — how EDSDK reaches the body (measured), the two
  ioctl codes, the async I/O shape, and the six dead hypotheses behind the current per-frame
  device-object fix in `ExecuteViewfinderRead`.
- **`docs/wpd-ioctl-wire-format.md`** — the ioctl buffer format, decoded. It is *not* a Canon
  format: it is Microsoft's own undocumented WPD property-bag serialization (a recursive
  PROPVARIANT using real OLE VARTYPEs), confirmed by diffing our own COM traffic against EDSDK's
  ioctls for identical calls. Every PROPERTYKEY involved is already a constant in `WpdInterop.cs`.

How it is put together:

- **`WpdPropertyBag.cs`** — the encoder/decoder. `WpdBagWriter` builds a request into a caller-owned
  array (so the transport keeps one buffer per session rather than allocating per command);
  `WpdBagReader` is a ref struct that *seeks* one key rather than materialising a dictionary, so a
  frame's payload is copied exactly once out of the driver's own bytes. `WpdCommands` builds the six
  commands. `WpdPropertyBagTests` pins all of this **byte-exact against `rev/RawIoctlPoc`**, which was
  itself verified against captured EDSDK traffic — that reference is the only thing standing between
  this encoder and silent drift, since the driver's sole feedback is a failed command.
- **`WpdIoctlDevice.cs`** — the handle. Bound to the thread pool's I/O completion port, so a pending
  ioctl parks no thread; the COM path cannot do this, which is why `CommandGate` exists at all. Both
  ioctl codes are documented constants (`PortableDevice.h`), and which one a command takes is decided
  by the driver's own command-access map — that is why the client-info handshake uses the READ code
  and everything else uses READ/WRITE.
- **`IMtpExtTransport`** — the seam. `PtpSession` routes to the three phases through this interface
  instead of naming a concrete transport, so COM and ioctl are interchangeable and never mixed.

**Do not cross the streams.** A session commits to one transport for its whole life. Each opens the
device independently and the camera holds one PTP session, so these are alternatives, not layers.

`CanonCamera.ConnectWpdAutoAsync` picks between them, and picks **once** — before `OpenSession`, the
last moment at which the two are interchangeable. After that, state is spread across the transaction
counter, the camera's own session and remote-mode flags, and any open transfer context (which
belongs to the handle and dies with it), so failing over would reconnect into a body that still
believes it is mid-transfer with a client that has gone. That is a deliberate reconnect, not a
fallback, and it does not belong inside a transport.

The probe is a real `GetDeviceInfo` (0x1001) read, not the connect handshake — `ConnectAsync` only
opens a handle and introduces the client, exercising none of the property-bag encoding this
transport hand-rolls, so the failure most worth guarding against would sail straight past it. 0x1001
runs initiate → read → end and is legal outside a session, so a failed probe costs nothing.
**Verified on a 450D: it answers 0x1001 with no session open.** A fallback is logged at warning
level, left on `CanonCamera.TransportFallbackReason`, and printed in the device report — silence
would produce bug reports about live-view memory from people who do not know they are on COM.

What the ioctl path is actually worth, measured on a 450D, 300 frames each through the same sample:

| | COM | raw ioctl |
|---|---|---|
| Real frames | 300 (20.2 fps) | 300 (20.3 fps) |
| Private working set | 73–119 MiB | 38–47 MiB |

**Throughput is identical** — the body is the limiter, and roughly half of all polls answer
`ObjectNotReady` on both. An earlier note here claimed 11.4 fps against ~5; that did not survive
driving both paths through one harness, and the memory figure is the honest headline. It comes from
`ExecuteViewfinderRead` having to create, open, close and release a whole `IWpdDevice` **per frame**,
because an unfinished transfer poisons a COM device object while `END_DATA_TRANSFER` never returns.
Neither happens over raw ioctl — the same end phase completes in 0–22 ms — so the poisoning is a
COM-layer artifact, not a `wpdmtp.sys` one.

Not a superset: the WPD **Content API** (`EnumerateWpdObjects`, `DownloadWpdObjectAsync`,
`RegisterWpdObjectAddedCallback`) is COM interfaces rather than MTP extension commands and has no
equivalent below the COM layer. `CanonCamera.SupportsWpdContentApi` reports this; the PTP equivalents
(`DownloadAsync`, `GetObjectFileNameAsync`, `ObjectAdded`) work on every transport. Still untested:
whether any of this generalises beyond a DIGIC III 450D.

## Diagnostic viewer (`src/FC.SDK.Viewer`)

`dotnet run --project src/FC.SDK.Viewer [output-directory]` — an SDL3 + Vulkan GUI (sibling
`../SdlVulkan.Renderer` + `../DIR.Lib`) that exposes every mapped control, every action, and the raw
event-stream property cache side by side, and writes a timestamped log of every PTP exchange to
`<output-directory>/fc-viewer-*.log`. That log is what to ask a bug reporter for.

- **The capture preview decodes the real file, not the embedded thumbnail.** A camera thumbnail is a
  few hundred pixels and exists to prove the frame happened. After download, `QueueFullPreview`
  renders the CR2/CR3 through `FC.SDK.Raw` (bilinear — AHD's edge quality is invisible once
  subsampled to a pane, and costs 2–5× the time) and replaces it, subsampling to 1600 px.
  Deliberately **not** on the `Enqueue` gate: a 450D frame takes ~2.3 s to decode and that would
  stall live view and event draining behind work needing nothing from the camera. The pane label
  says which preview is showing, because "why is my capture soft" has one answer worth checking
  first.
- **An exposure is tracked separately from `BusyOperation`.** `TakePictureAsync` does not return
  until the body finishes the release — 2.1 s on a 450D — and the image arrives later still, via
  `ObjectAdded`. `ViewerState.Exposure` spans shutter-press to image-arrival and drives a ticking
  counter on the capture button, which is disabled and amber (not grey — see `Button`'s
  `disabledBackground`) throughout. It is started **before** awaiting the release, or the button
  would look idle and clickable for those first two seconds. It carries a deadline because a body
  that accepts a release and produces nothing is a real state, and the tick that refreshes the
  counter is also what enforces it — `CheckNeedsRedraw` only redraws on invalidation, and during an
  exposure nothing else invalidates.
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

Automated tests cover `FC.SDK.Raw`, the command gate, and the ioctl wire format; everything else in
the device layer needs a physical Canon body.

**`src/FC.SDK.Sample` is the fastest way to exercise a body** — no GUI, one command, and it writes a
`CanonDeviceReport` every run:

```
dotnet run --project src/FC.SDK.Sample -- [--ioctl] [--frames N] [--no-capture]
```

`--ioctl` selects `WpdIoctlPtpTransport`; without it you get the COM transport, which makes the two
directly comparable on the same body in the same harness. Run it from a scratch directory — it writes
the report, the last live-view frame, and any capture into the working directory.

**`src/FC.SDK.Diagnostics` is for questions the sample cannot answer** — sequencing a body through an
experiment and judging it on delivered bytes. It lives in `src/`, not `rev/`, because the experiments
outlive the sessions that prompted them and get re-run whenever a claim is challenged:

```
dotnet run --project src/FC.SDK.Diagnostics -- --help     # every experiment, with its options
dotnet run --project src/FC.SDK.Diagnostics -- evf [--host]
```

Each experiment is a **System.CommandLine subcommand** (`matrix`, `diag`, `mlucheck`, `mluself`,
`clack`, `evf`, `zoom`, `zoompix`, `lens`), so `--help` is the list and a typo is an error. That last
part is not cosmetic: the old `args.Contains("--evf")` dispatch treated an unrecognised argument as
"no mode given" and therefore ran the **default** one, so a mistyped `--evff` fired the mirror-lockup
matrix — minutes of shutter actuations — instead of reading a few properties.

Every mode brackets its result with controls and prints per-command timings, because on these bodies
the response code is the least informative thing available. Three of its habits are worth copying
into any new mode:

- **A control either side of a negative result.** A run whose controls did not expose proves nothing,
  and the harness says so rather than reporting six tidy failures.
- **A settle between mirror-lockup tests** (default 35 s): a 450D drops a raised mirror by itself
  after ~30 s, so without the gap one test's first press is really the previous test's second.
- **`--release <handles>`** hands back frames a crashed run left the body holding — the recovery path
  for the `DeviceBusy` wedge described under capture destinations above.

Manual sequence (all of it also available as buttons in the viewer):
1. Connect camera (WPD COM, WPD ioctl, USB or WiFi)
2. Open session — check the operation-support list; note that 0x1015 being *present* does not mean
   property reads work (see "Reading and writing EOS properties")
3. Read all properties and confirm the event cache is populated
4. Take picture: `TakePictureAsync()`
5. Live view: `StartLiveViewAsync()` → `GetLiveViewFrameAsync()` → verify JPEG
6. Bulb: `BulbStartAsync()` → delay → `BulbEndAsync()`

**Check mirror lockup first, then the battery.** A dead capture on a 450D has two candidate
explanations and the tempting one is wrong more often than not:

- **MLU enabled is the usual cause.** The body answers OK to `RemoteRelease` and then does nothing —
  no mirror, no event, no image. `TakePictureAsync` cannot tell this apart from a fault, so check
  `Mirror lockup` in the viewer or the C.Fn table in a device report before anything else. This was
  once written up here as a battery symptom for a whole session, because the property that would
  have answered the question (`0xD1BF`) does not exist on this body and the old
  `GetMirrorLockupStateAsync` returned a default instead of consulting the C.Fn block.
- **Low battery is real but narrower than previously claimed.** Capture at `0xD111` level 1 works
  fine with MLU off — verified repeatedly, 11.2 MB CR2s delivered in ~4 s. What *has* been observed
  at level 1 is phantom `PropValueChanged` records for Tv that no one caused, and one session where
  live view stopped producing frames while still answering every read. `CanonDeviceReport` still
  warns at level ≤ 1, and that is still worth heeding — just do not let it end the investigation.

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

`rev/` holds decompiles, API-tracing probes, raw captures and throwaway test projects. A probe that
starts answering the same question more than once belongs in `src/FC.SDK.Diagnostics` instead — that
is where the mirror-lockup matrix went, having been re-derived from scratch twice. It is in
`.gitignore` on purpose — the *conclusions* belong in `docs/`, the raw material stays local. Notable
tooling, all of which expects the camera to be present and not held by another app:

- `extract_propid_map.py` — pulls EDSDK's PTP-code ↔ `EdsPropertyId` table out of `.rdata`
  (234 pairs; corroborates `CanonPropertyMap`, 0 conflicts). Needs a local `EDSDK.dll`.
- `edsdk_ioctl_bytes_probe.js` + `decode_wpd_ioctl.py` — capture and decode raw ioctl buffers.
- `wpd_raw_ioctl_poc.py` / `RawIoctlPoc/` — the working raw-ioctl transport proof (see the WPD
  section above). The C# one is the maintained one.
