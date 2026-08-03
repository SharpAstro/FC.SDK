# What EDSDK does that FC.SDK does not

Measured 2026-08-03 against FC.SDK 2.0. The EDSDK side of the comparison is its published API
surface plus `rev/edsdk-propid-ptp-map.md` — the 234-pair PTP-code ↔ `EdsPropertyId` table
`rev/extract_propid_map.py` pulls out of `EDSDK.dll`'s `.rdata`. The FC.SDK side is the source, and
every claim below cites the file that establishes it.

The capture path is closed: session lifecycle, release, bulb, mirror lockup (including refusing it
where the body discards it), capture destination, download, transfer hygiene, the Custom Function
block, and the event pump. What remains clusters in four places, and the first one is the reason two
of the others exist.

## 1. Every property is a `uint32` — the root cause, and a live bug

> **CLOSED in 3.0.** `CanonPropertyMap` carries a `CanonPropertyType`; `GetPropertyBytesAsync` /
> `GetPropertyStringAsync` and their raw counterparts are public; `GetPropertyAsync` refuses a
> non-scalar instead of answering `OK` with nonsense; `GetLensNameRawAsync` is gone. Verified on a
> 6D: `LensName` reads `"EF50mm f/1.8 STM"`, `BodyIDEx` reads `"195020000089"`. Note those two are
> *different identifiers*, not two readings of one — `GetDeviceInfo`'s serial is a 32-hex-digit
> internal id, while 0xD1AF is the number printed on the body. The point/rect part turned out not to
> be needed; see §2.

`CanonCamera.GetPropertyAsync` / `SetPropertyAsync` (`src/FC.SDK/CanonCamera.cs:302`, `:313`) take
and return a `uint`, and the layer beneath them is the same shape:
`CanonPtpSession.SetPropertyUInt32Async` (`src/FC.SDK/Canon/CanonPtpSession.cs:247`) builds a
fixed 12-byte record `[size][propCode][value]` and `GetPropertyUInt32Async` (`:286`) answers a
single word out of the cache.

`CanonPropertyCache` already keeps the full bytes — `GetRawValue`
(`src/FC.SDK/Canon/CanonPropertyCache.cs:44`), which is how the C.Fn block is read — but it is
`internal` and nothing public reaches it. The write side has a precedent too:
`SetCustomFunctionBlockAsync` (`CanonPtpSession.cs:597`) already sends an arbitrary-length payload
through 0x9110. So both halves of a general accessor exist; they are just not generalized or
exposed.

**This is not only a missing feature — four properties are wrong today.** `CanonPropertyMap`
declares these with size 4 despite being strings:

| Property | PTP code | `CanonPropertyMap.cs` |
|---|---|---|
| `OwnerName` | 0xD115 | `:26` |
| `LensName` | 0xD1D8 | `:34` |
| `Artist` | 0xD1D0 | `:39` |
| `Copyright` | 0xD1D1 | `:40` |

A read of any of them reinterprets the first four bytes of a string as an integer and returns it
with `EdsError.OK`. `GetLensNameRawAsync` (`CanonCamera.cs:596`) half-admits this in its own
summary — *"Returns raw uint — use GetDeviceInfo for string"* — except `GetDeviceInfo` supplies the
model and serial, not the lens. Same class of problem for the non-scalar types EDSDK returns
through the same call: `EdsTime` (`DateTime`, 0xD113 — mapped as size 4), `FocusInfo`,
`PictureStyleDesc`, `WhiteBalanceShift`.

**Fix shape:** make the raw bytes public (`GetRawPropertyBytesAsync`), generalize the write to a
`byte[]` payload, and add typed accessors over both — string, `EdsPoint`, `EdsRect`, `EdsTime`.
Correct the four sizes at the same time. This is the highest-value item in the document because it
also unblocks §2.

## 2. Live view is JPEG-only — no zoom, no AF

> **CLOSED in 3.0**, and the premise below is wrong in an instructive way: zoom is an **operation**
> (0x9158 / 0x9159), not a property, so the point/rect accessor TianWen was waiting for was never
> needed. What actually blocks magnification is `Evf_AFMode` (0xD1BA), which silently discards the
> zoom in its default setting. Full measurements in **`docs/canon-live-view-zoom.md`**. Live-view AF
> (`AutoFocusLiveViewAsync`, 0x9154) works too. Still undecoded: envelope record types 4, 5, 7, 12.

Functionally the biggest gap, and the one a consumer has already hit (see below): **5×/10× EVF zoom
is how you focus on a star, and it is the only planetary regime a DSLR has.**

- **The envelope is parsed for the image record and nothing else.**
  `CanonViewfinderFrame.ExtractJpeg` (`src/FC.SDK/Canon/CanonViewfinderFrame.cs:34`) walks
  `[length][type][payload]` records looking for types 1/9/11 and steps past the rest. Its own
  comment names what it discards: focus points, histogram, zoom metadata. EDSDK surfaces these as
  properties on the EVF image ref.
- **No zoom control.** `Evf_Zoom` (0x507), `Evf_ZoomPosition` (0x508), `Evf_ZoomRect` (0x541) and
  `Evf_CoordinateSystem` (0x540) are all present in `EdsPropertyId` (`:59`, `:60`, `:70`, `:69`) and
  **absent from `CanonPropertyMap`**. `PtpOperationCode.CanonZoom` (0x9158,
  `src/FC.SDK/Protocol/PtpOperationCode.cs:78`) is declared with zero uses.
- **No autofocus in live view.** `PtpOperationCode.CanonDoAf` (0x9154, `:73`) — declared, zero uses.
  `Evf_AFMode` (0x50E) unmapped. We have `AfCancelAsync` (0x9160) and `DriveLensAsync` (manual focus
  stepping), but nothing that says "focus now". `CanonDepthOfFieldPreview` (0x9156, `:75`) is
  likewise unused, though the property path covers DoF preview.

### TianWen has this blocked and specced

`../tianwen` shipped Phase E core on 2026-07-16 — `CanonCameraDriver : IVideoCameraDriver`,
full-frame EVF JPEG streaming — and **deferred the zoom regime specifically on this SDK**. From
`tianwen/docs/plans/planetary-native-video.md` (Phase E.3) and `tianwen/docs/todo/drivers.md`:

> only the zoom *level* `Evf_Zoom` is a plain `uint` reachable via the generic `SetPropertyAsync`.
> `Evf_ZoomPosition` (the actual pan actuator) is an `EdsPoint` (two int32s) and `Evf_ZoomRect` is an
> `EdsRect` — 8+ byte payloads. FC.SDK's only generic accessor […] reads/writes just the first
> uint32, so it cannot round-trip a point/rect.

So §1 and §2 are one piece of work, not two. The zoom crop is also TianWen's host-side ROI jog:
with it, `CanJogRoi` flips to true when zoomed and the recenter loop pans the crop instead of
nudging the mount. Without it, `CanJogRoi` is false and a drifting planet moves the telescope.

Their asked-for surface, verbatim in intent:

- `SetEvfZoomAsync` — zoom level (1×/5×/10×)
- `SetEvfZoomPositionAsync` — `byte[]`-payload `SetPropValue`, mirroring `SetCustomFunctionBlockAsync`
- an `Evf_ZoomRect` read

Their docs call this "FC.SDK 1.5"; that naming predates the 2.0 release, so it lands in **2.1**.
Per-body zoom-position units vary and must be verified on hardware — the 450D and 6D are both to
hand, and TianWen's per-axis cap bounds a wrong guess to a small mis-pan rather than a runaway.

## 3. No filesystem over PTP

Declared in `PtpOperationCode`, **all with zero uses**:

| Op | Code | Line |
|---|---|---|
| `GetStorageIDs` | 0x1004 | `:9` |
| `GetStorageInfo` | 0x1005 | `:10` |
| `GetNumObjects` | 0x1006 | `:11` |
| `GetObjectHandles` | 0x1007 | `:12` |
| `DeleteObject` | 0x100B | `:16` |
| `CanonGetPartialObject` | 0x9107 | `:27` |

The only browse path is the WPD Content API (`EnumerateWpdObjects`, `DownloadWpdObjectAsync`,
`RegisterWpdObjectAddedCallback`), and `CanonCamera.SupportsWpdContentApi`
(`src/FC.SDK/CanonCamera.cs:1140`) is `_transport is WpdPtpTransport` — **false on the ioctl
transport**, which `ConnectWpdAutoAsync` prefers. So card browsing, delete, and chunked or resumable
download have no equivalent to EDSDK's volume → folder → directory-item walk on the transport we
default to. `EdsFormatVolume` has no equivalent at all.

Capture and download of a *just-taken* frame are unaffected — those go through the event handle, not
the filesystem.

## 4. The property table covers 30 of 234

`CanonPropertyMap` maps 30 properties. `rev/edsdk-propid-ptp-map.md` records the extraction result:

> **4 confirm `CanonPropertyMap`, 0 conflict, 227 are new to us.**

Zero conflicts is the reassuring half — nothing we map is wrong (the four string *sizes* in §1 are a
separate matter from the codes). Most of the 227 tail is model-specific and will never matter, but
it is the authoritative place to look when a body wants something we do not have, and it beats
guessing codes from EDSDK property IDs — which `CLAUDE.md` forbids for good reason.

## Smaller gaps

| Gap | Evidence / note |
|---|---|
| Movie recording | `Record` (0x510) is in `EdsPropertyId.cs:64`, unmapped. No start/stop. See "Not gaps" below for why this is *not* a live-video gap |
| Hotplug notification | `EnumerateUsbCameras` / `EnumerateWpdCameras` are polls; EDSDK has `EdsSetCameraAddedHandler` |
| Direct transfer | `Enter`/`ExitDirectTransfer` status commands absent |
| `CanonGetDeviceInfoEx` | 0x9108, declared (`PtpOperationCode.cs:28`), zero uses |
| `0x9128` second parameter | We send one where EDSDK sends two — the standing lead for an arbitrary mirror settle (see below) |
| Property size metadata | No equivalent of `EdsGetPropertySize`; the fixed size in `CanonPropertyMap` is our own assertion, and §1 shows it can be wrong |

## Not gaps

Worth stating so the list stops growing:

- **Movie recording as a live-video source.** TianWen's own Phase E.4 argues this is intrinsic to
  the format, not an SDK shortfall: movies are temporally compressed (H.264/HEVC smears exactly the
  per-frame detail lucky imaging selects on), camera-processed rather than raw, and card-resident
  rather than a host stream. The legitimate use is offline batch ingest of a recorded file, which is
  a TianWen file-ingest feature. Driving the `Record` property is still a small real gap; making it
  a stream is not.
- **`EdsImageRef` image processing.** EDSDK's RAW→RGB with Canon's own pipeline, EXIF read/write,
  save-as-JPEG. `FC.SDK.Raw` is the deliberate answer and has its own roadmap in
  `src/FC.SDK.Raw/TODO.md` (tone mapping, highlight recovery, Adobe DCP). Not measured here.
- **`GetDevicePropValue` (0x1015) for vendor codes.** Not a gap — EOS bodies refuse it, and a 450D
  advertising the op while refusing every 0xD1xx code is documented in `CLAUDE.md`. The event-fed
  cache is the correct design and matches both EDSDK and libgphoto2.

## Adjacent open items

Not EDSDK-parity, but tracked here so there is one list:

- **A mirror-lockup capture helper.** `TakePictureAsync` currently *refuses* when lockup is armed on
  a body that discards the release, but nothing performs a lockup exposure where it works. The 6D
  recipe is verified: `0xD13A` = 1 → drive = `Timer_2sec` (0x11, ~2.9 s settle) → `TakePictureAsync`
  → restore drive. Held because the settle is the body's own timer rather than a parameter, and the
  `0x9128` second-parameter lead above may offer an arbitrary one — worth resolving before freezing
  an API around the timer.
- **`EdsDriveMode.Timer_10sec_RemoteControl` (0x07) ships with an unverified name.** Only the 450D
  offers it; it produced a ~10 s delay and then a **six-frame burst**, which is not understood.
  Its two neighbours were renamed in 2.0 from measurement; this one was not.
- **`CanonDeviceReport` says nothing about mirror-lockup capability**, despite
  `SupportsMirrorLockupCapture` being inferable at report time.

## Suggested order

§1 and §2 shipped together as **3.0** (major, because the string properties changed behaviour and
`GetLensNameRawAsync` was removed rather than deprecated).

What is left is breadth rather than blockers: §3 the PTP filesystem, §4 the property-table tail, and
the smaller table above. Take them on demand.

One caveat carried forward from §2's measurements: everything there was verified with an AF lens
attached. The magnification unlock works by writing `Evf_AFMode`, and whether that is writable with
**no electronic contacts — a camera on a telescope** — is unmeasured. That is precisely the
configuration the feature exists for, so it is the next thing to check on hardware.
