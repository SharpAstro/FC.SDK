# The WPD ioctl wire format, decoded

`docs/canon-windows-transports.md` established *that* EDSDK talks to the camera by opening the WPD
device-interface path with `CreateFileW` and driving it with raw `DeviceIoControl` calls, bypassing
`IPortableDevice::SendCommand` entirely — and flagged the buffer's byte layout as unknown: the probe
that found the two ioctl codes logged sizes, not bytes. This is that gap closed.

## Not an undocumented Canon format — Microsoft's own, and Microsoft says so

Before reverse-engineering anything: [a Microsoft Learn / MSDN forum thread asking exactly this
question](https://learn.microsoft.com/is-th/answers/) confirms the byte format of WPD ioctls **is
not publicly documented**, and that the payload is an `IPortableDeviceValues` object "that may
contain nested collections, which requires deserialization to fully interpret." Not even the
official `WpdBasicHardwareDriver` sample in
[microsoft/Windows-driver-samples](https://github.com/microsoft/Windows-driver-samples)`/wpd` shows
it — that sample plugs into Microsoft's own WPD class-extension layer, which does this exact
deserialization *for* the driver author. So there is no shortcut; what follows was recovered by
diffing two independent implementations against each other.

## The format

The ioctl buffer is one recursively-serialized `PROPVARIANT`, using the real OLE `VARTYPE` codes
(the same numeric values as `System.Runtime.InteropServices.VarEnum` in .NET):

```
PROPVARIANT ::= [vt:u32][payload]

  vt=13 (VT_UNKNOWN) -- payload self-identifies via an embedded CLSID:
    CLSID_PortableDeviceValues (a keyed dictionary):
        [CLSID:16][count:u32] { [fmtid:16][pid:u32] PROPVARIANT }×count
    CLSID_PortableDevicePropVariantCollection (a plain array):
        [CLSID:16][count:u32] { PROPVARIANT }×count

  vt=19 (VT_UI4):              [u32]
  vt=21 (VT_UI8):               [u64]
  vt=31 (VT_LPWSTR):            [byteLen:u32][UTF-16LE bytes, byteLen incl. NUL]
  vt=72 (VT_CLSID):             [16-byte GUID]

  vt=19|0x1000 (VT_VECTOR|VT_UI4): [count:u32][count × u32]   -- written by symmetry, unconfirmed
  vt=17|0x1000 (VT_VECTOR|VT_UI1): [count:u32][count raw bytes] -- written by symmetry, unconfirmed
```

Every `PROPERTYKEY` (`fmtid`+`pid`) the decoder recognises is already a constant in
`src/FC.SDK/Transport/WpdInterop.cs` — `WPD_COMMAND_COMMON`, `WPD_COMMAND_MTP_EXT`,
`WPD_CLIENT_INFO`, `PID_COMMAND_ID`, `PID_OPERATION_CODE`, `PID_TRANSFER_CONTEXT`, and so on. This
isn't new vocabulary; it's the same property bag our own `WpdPtpTransport` already builds by hand
before handing it to COM, just serialized flat instead of passed as a live interface pointer.

## How this was actually confirmed, not just guessed

`fc-viewer` — this repo's own `WpdPtpTransport`, going through **genuine**
`IPortableDevice::SendCommand` — was captured issuing the exact same two `DeviceIoControl` codes on
the exact same device handle as EDSDK (`rev/edsdk_ioctl_bytes_probe.js`, hooking `CreateFileW` +
`DeviceIoControl`). That alone says something: `PortableDeviceApi.dll`'s own implementation of
`SendCommand` bottoms out at the identical kernel contract EDSDK uses directly. EDSDK's ioctl
shortcut and our COM path are two doors into the same room.

For the WPD client-registration handshake — the first call either app makes, `ioctl #1`, CTL_CODE
`0x404108` — the two captured buffers are **byte-identical except for the two fields that should
legitimately differ**: the client name string (`"EDSDK"` vs `"FC.SDK"`) and the client minor version.
`rev/decode_wpd_ioctl.py` parses both to their exact declared byte length with nothing left over.
That also answers a question `canon-windows-transports.md` left open — whether `0x404108`,
the one read-only call per session, is a handshake or a capability probe: it's the handshake,
carrying `WPD_CLIENT_NAME`/`MAJOR_VERSION`/`MINOR_VERSION`/`REVISION` under `WPD_COMMAND_COMMON`
command id 4.

A full sweep of both captures (~15,800 buffers total, `rev/decode_wpd_ioctl.py sweep`) then
confirms the same scheme across essentially the whole protocol surface CLAUDE.md already documents:

- **`PID_COMMAND_ID` (`WPD_COMMAND_COMMON`, pid 1002)** matches `WpdInterop.cs` exactly: 12
  (`EXECUTE_NO_DATA`), 13 (`EXECUTE_DATA_READ`), 14 (`EXECUTE_DATA_WRITE`), 15 (`READ_DATA`), 16
  (`WRITE_DATA`), 17 (`END_DATA_TRANSFER`) — 2574/2225/2616 hits respectively for the three that
  dominate a live-view session.
- **`PID_OPERATION_CODE` (`WPD_COMMAND_MTP_EXT`, pid 1001)** carries real Canon/PTP opcodes
  wholesale: `0x1001` GetDeviceInfo, `0x1002` OpenSession, `0x1003` CloseSession, `0x1015`
  GetDevicePropValue, `0x9104` GetObject, `0x9110` SetDevicePropValueEx, `0x9114` SetRemoteMode,
  `0x9115` SetEventMode, `0x9116` GetEvent (2417 hits — the event-poll loop), `0x9117`
  TransferComplete, `0x911A` PCHDDCapacity, `0x9127` RequestDevicePropValue, `0x9128`/`0x9129`
  RemoteRelease On/Off, `0x9153` GetViewfinderData (109 hits — live view). Every one matches
  CLAUDE.md's opcode table.
- **`PID_OPERATION_PARAMS`** (pid 1002 under `MTP_EXT`) is a
  `CLSID_PortableDevicePropVariantCollection` of `VT_UI4` values — literally the PTP parameter
  array — confirmed both non-empty (`params=[1]` for SetRemoteMode/SetEventMode, matching "param=1"
  in CLAUDE.md's opcode table) and empty (`params=[]` for GetEvent, which takes none).
- **`PID_TRANSFER_CONTEXT`** (pid 1006 under `MTP_EXT`) is a `VT_LPWSTR` carrying the literal string
  `WpdPtpTransport.cs` already threads through `ReadChunk`/`ExecuteViewfinderRead` as a plain C#
  string (`"CCustomReadContext{GUID}"`), for a real `END_DATA_TRANSFER` call.

Two implementations, agreeing on every field down to real operation codes and real parameter
values, mapped onto property keys this codebase already has names for — that's about as strong a
confirmation as reverse-engineering gets without Microsoft's source.

## Confirmed end-to-end: a working raw-ioctl transport, and live view at 2x the COM frame rate

The format above is not just readable, it is *writable*: `rev/RawIoctlPoc` drives a real EOS 450D
end to end with **zero COM involvement** — no `IPortableDevice`, no `PortableDeviceApi.dll`, just
`CreateFileW` on the device-interface path plus `DeviceIoControl`. The full session works:

```
handshake -> OpenSession -> SetRemoteMode -> SetEventMode -> SetRequestOLCInfoGroup
          -> GetEvent drain -> Evf_Mode=1 -> Evf_OutputDevice=PC -> KeepDeviceOn
          -> InitiateViewfinder -> [ GetViewfinderData ] xN
```

Both property writes go through the complete three-phase data-WRITE
(`EXECUTE_DATA_WRITE` -> `WRITE_DATA` -> `END_DATA_TRANSFER`) carrying the 12-byte
`[size][propCode][value]` record CLAUDE.md documents, and both return PTP `0x2001` (OK). Live view
physically engages — the mirror flips up on the body.

**The headline measurement: 198 of 200 poll cycles carried a real frame, at 11.4 fps sustained,
over ONE handle held open for the whole session, calling `END_DATA_TRANSFER` on every single
cycle.** Each frame is ~140-150 KB with a valid JPEG SOI at offset 8 (the 8-byte Canon envelope
header — the same envelope `CanonViewfinderFrame.ExtractJpeg` already strips). No poisoning, no
hangs, no per-frame device-object churn.

That settles the question this whole exercise existed to answer. `WpdPtpTransport` currently opens
and closes a brand-new `IWpdDevice` **per live-view frame** (`ExecuteViewfinderRead` ->
`OpenViewfinderDevice`), because at the COM layer an unfinished transfer poisons the device object
while the end phase itself never returns. Neither failure mode exists here: the same
`END_DATA_TRANSFER` that never returns through `IPortableDevice::SendCommand` completes in
**0-22 ms** through a raw ioctl, every time. So the poisoning lives in the COM layer's own
bookkeeping, not in `wpdmtp.sys`. And the payoff is not merely tidier: raw ioctl streams at ~11.4
fps against the COM path's ~5 fps, while eliminating the per-frame allocation entirely.

Two traps worth recording, both of which produced convincing-looking but meaningless results first:

- **`GetViewfinderData` (0x9153) takes three parameters, `(0x00200000, 0, 0)`** — the first is the
  2 MiB max payload size, matching the `in=2097548` buffers in EDSDK's own capture. Sending an
  empty parameter list is accepted and answers *successfully* with `TRANSFER_TOTAL_SIZE=0` forever:
  the camera has simply been asked for nothing. An early run of 1500 such cycles looked like a
  triumphant "no poisoning over 1500 frames" and proved nothing at all, because empty no-op cycles
  cannot poison a transfer context. Any test of this must assert on real bytes arriving.
- **`InitiateViewfinder` (0x9151) alone does not start live view.** Without `Evf_Mode=1` and
  `Evf_OutputDevice=PC` first, the body stays on its normal shooting screen with the mirror down
  and every read returns empty. (On DIGIC III bodies like the 450D, `CanonPtpSession` notes 0x9151
  is not even in the supported-operations set — setting `Evf_OutputDevice` to PC *is* the start
  sequence.)

`TRANSFER_TOTAL_SIZE` is reported as 0 on every viewfinder response even when a full frame follows,
so the payload length must come from what `READ_DATA` actually returns, not from the declared size.

## What's still open

- **One property key recurs in every command envelope, still unnamed:** `(WPD_COMMAND_COMMON, pid
  1010)`, always a `VT_LPWSTR` GUID string, different per call and not the same value as the
  transfer context. Reads like a per-request correlation id the WPD stack assigns rather than
  something a caller sets — `RawIoctlPoc` generates a fresh random one per call and the driver
  accepts it, so it is evidently not validated against anything.
- **`(WPD_COMMAND_MTP_EXT, pid 1013)` = 262144** appears on every viewfinder response (exactly
  256 KiB). Not in `WpdInterop.cs`, purpose unknown; ignoring it causes no observable problem.
- **`VT_ERROR` (vt=10)** carries the `PID_HRESULT` status and had to be added to the decoder after
  the fact — worth noting because the response side uses VARTYPEs the request side never does, so
  a decoder built only from request captures will not round-trip responses.
- **Response-side decoding is now empirically confirmed** (`RawIoctlPoc` reads
  `PID_TRANSFER_CONTEXT`, `PID_HRESULT` and the frame payload out of real responses), which
  supersedes the earlier "presumably symmetric" caveat. The `rev/edsdk_ioctl_bytes_probe.js`
  under-capture bug behind that uncertainty — it sized output dumps off `DeviceIoControl`'s
  *allocated capacity* argument, which stays ~1–3 MiB all session, so it always took the
  "large buffer, head only, 64 bytes" branch — is fixed but not re-captured.
- **`VT_VECTOR|VT_UI1`** for `PID_TRANSFER_DATA` is now confirmed by use in both directions: it
  carries the 12-byte property-write record *and* decodes ~145 KB viewfinder frames correctly.
  `VT_VECTOR|VT_UI4` remains written-by-symmetry and unexercised.
- **A handful of opcodes appeared that aren't in CLAUDE.md's table yet:** `0x100A`, `0x100E`,
  `0x9101`, `0x9102`, `0x9103`, `0x9107`, `0x9109`, `0x910F` — the last is `RemoteRelease`, already
  named in CLAUDE.md's Custom-Function section (a 450D "silently ignores `RemoteRelease` (0x910F)
  while MLU is enabled") but missing from the opcode reference table itself.

## Reproducing this

`rev/edsdk_ioctl_bytes_probe.js` (Frida, hooks `CreateFileW`+`DeviceIoControl`, dumps full buffers
and scans for recognisable PTP/PROPVARIANT structure) — attach it to a running EDSDK-based app
*and*, separately, to `fc-viewer` doing the equivalent action, so the two captures can be diffed the
same way this write-up was. `rev/decode_wpd_ioctl.py sweep` reads both logs and reports the
histograms above; `rev/decode_wpd_ioctl.py show <log> "<tag>"` decodes one buffer by its tag (e.g.
`"ioctl 7 IN"`). The raw captures this analysis was built from —
`rev/edsdk-ioctl-bytes-capture.log` and `rev/fcviewer-ioctl-bytes-capture.log` — are kept alongside
the scripts, `.gitignore`d like the rest of `rev/`, so they only exist on a machine that generated
them.

## Why this matters

The original question: *do we have enough information to implement a transport that talks this
ioctl protocol directly, the way EDSDK does?* The answer is now demonstrably yes — `rev/RawIoctlPoc`
does it, against real hardware, including the write path and sustained live view at double the COM
frame rate.

The motivating problem was the per-frame allocation in `WpdPtpTransport.ExecuteViewfinderRead`: a
whole `IWpdDevice` created, opened, closed and released for **every single live-view frame**, purely
to work around COM-layer transfer-context poisoning. A raw-ioctl viewfinder path removes that
entirely — one handle for the session, no COM objects in the hot loop — and measured ~11.4 fps
against the COM path's ~5.

Scope note for whoever picks this up: the intended change is *narrow*. Session setup, properties,
capture and event polling all work fine over COM today and should stay there; only the viewfinder
read needs replacing. `IPtpTransport` already isolates this, and `ExecuteCommandReadDataAsync`
already special-cases `CanonGetViewfinderData` onto its own path, which is exactly the seam a raw
handle would slot into.
