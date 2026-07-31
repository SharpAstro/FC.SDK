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

## What's still open

- **The response/output side is unconfirmed.** The probe that produced these captures had a real
  bug: it sized the output dump off `outSize` (`DeviceIoControl`'s *allocated capacity* argument),
  which stays ~1–3 MiB for the whole session (the same preallocated buffer
  `canon-windows-transports.md` already measured) — so it always hit the "large buffer, head only,
  64 bytes" branch and never captured enough of a real response to decode. Fixed in
  `rev/edsdk_ioctl_bytes_probe.js` (always dump from the front regardless of capacity); not
  re-captured yet. The format is presumably symmetric — `PID_RESPONSE_CODE` (1003) and
  `PID_RESPONSE_PARAMS` (1004) are already named in `WpdInterop.cs` — but that's an inference, not
  something decoded from real bytes.
- **`VT_VECTOR|VT_UI1`** (a raw byte buffer — needed for `PID_TRANSFER_DATA`, i.e. the actual JPEG
  frame bytes) never appeared in a buffer that fully parsed. The decoder's case for it is written by
  symmetry with the confirmed `VT_VECTOR|VT_UI4`, not verified.
- **One property key recurs in every command envelope, still unnamed:** `(WPD_COMMAND_COMMON, pid
  1010)`, always a `VT_LPWSTR` GUID string, different per call and not the same value as the
  transfer context. Reads like a per-request correlation id the WPD stack assigns rather than
  something a caller sets.
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

The original question this answered: *do we have enough information to, in principle, implement a
transport that talks this ioctl protocol directly, the way EDSDK does?* Before this, the answer was
no — we knew which ioctl codes and the async I/O shape, but not what to put in the buffer. Now the
envelope, the command dispatch, the parameter encoding, and a working transfer-teardown call are all
confirmed against two independent real implementations. What remains (the response side, byte
buffers, one unnamed correlation field) is refinement, not an unknown wrapper format standing in the
way. Per CLAUDE.md, reimplementing this is still explicitly a last resort — the WPD COM path already
works, live view included — but the "we don't understand the format" reason not to is no longer
true.
