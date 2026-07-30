# How EDSDK actually talks to an EOS body on Windows

Everything here was measured on 2026-07-30 against a **Canon EOS 450D** (VID 0x04A9, PID 0x3145)
with NINA 3.x driving Canon's `EDSDK.dll`, by Frida-hooking NINA's process. It is recorded because it
answers a question our own WPD transport keeps running into: why EDSDK can stream live view from a
DIGIC III body and we cannot.

## Headline: EDSDK does not use the WPD COM API, and does not use WinUSB either

Both `PortableDeviceApi.dll` and `WinUSB.DLL` are *loaded* in the process, which is misleading — no
traffic goes through either:

| Hook | Result during connect + live view |
|------|-----------------------------------|
| `IPortableDevice::SendCommand` (vtable slot 4) | **never called** |
| `WinUsb_WritePipe` / `WinUsb_ReadPipe` | **never called** |
| `ole32!CoCreateInstance` | called once (not for the device) |
| `kernelbase!CreateFileW` | **opens the device directly** |
| `kernelbase!DeviceIoControl` | **all camera traffic** |

So EDSDK opens the WPD *device interface* itself and drives the MTP stack with ioctls, bypassing the
`IPortableDevice` user-mode API. No driver replacement is involved: the stock MTP class driver stays
bound, the camera stays visible to Explorer, and EDSDK still works. That is the crucial difference
from our `UsbPtpTransport`, which needs WinUSB bound via Zadig.

## The device path

```
\\?\usb#vid_04a9&pid_3145#6&fd1ea69&1&3#{6ac27878-a6fa-4155-ba85-f98f491d4f33}
```

`{6AC27878-A6FA-4155-BA85-F98F491D4F33}` is the WPD device-interface class GUID. This is byte-for-byte
the same string our own `WpdPtpTransport` uses as its WPD device id (see any `fc-viewer-*.log` line
`WPD: Canon EOS 450D (\\?\usb#vid_04a9...)`), so both stacks address the identical device node.

## The two ioctl codes

Only two codes were ever observed, 1329 calls in one live-view session:

| Code | Count | DeviceType | Access | Function | Method |
|------|------:|-----------|--------|----------|--------|
| `0x404108` | 1 | `0x40` | `FILE_READ_ACCESS` (1) | `0x42` (66) | `METHOD_BUFFERED` (0) |
| `0x40c108` | 1328 | `0x40` | read+write (3) | `0x42` (66) | `METHOD_BUFFERED` (0) |

Decoded with the standard `CTL_CODE` layout `(DeviceType << 16) | (Access << 14) | (Function << 2) | Method`.
So it is *one* driver function (`0x42`) on device type `0x40`, issued either read-only (once, probably
a capability/open probe) or read-write (everything else).

## Asynchronous by construction

Every logged call returned `ok=0` with `bytesReturned=-1` (our probe reports `got=-1` when the
`lpBytesReturned` pointer was not written), i.e. **`ERROR_IO_PENDING`** — overlapped I/O completing
later, not synchronously. Nothing blocks a thread waiting for the camera. This is why EDSDK streams
~10 fps cleanly, and it is the shape a future managed transport should copy: open with
`FILE_FLAG_OVERLAPPED`, bind via `ThreadPoolBoundHandle`, complete through an `IOCompletionCallback`.

## Buffer sizes, and the live-view pattern

Input-buffer size histogram for one connect + ~10 s of live view:

```
    1  in=260          <- first call, with code 0x404108
  204  in=270
    5  in=278
  230  in=294
    6  in=302
  436  in=340
    6  in=342
    5  in=410
    1  in=426
  436  in=2097548      <- 2 MiB + 460
```

`outCap` grew from `1048836` (1 MiB + 68) to `3146124` (3 MiB + 76) after live view started.

The **436/436 pairing is the live-view signature**: per frame, one `in=2097548` request (2 MiB + a
~460-byte header) followed by one small `in=340` request. Two ioctls per frame, and *no third
"end transfer" round trip*. The small ones (270/294/340) dominate outside live view and are ordinary
commands plus their response reads.

One thing follows immediately: **there is no separate end-of-transfer phase for EDSDK to hang in.**
Our `ENDDATATRANSFER` (MTP ext PID 17) never returns for `0x9153` on this body, while it works fine
for `GetObject` (0x9104) — 12 MB CR2 downloads complete through the very same code path.

The 2 MiB request per frame looks like the other clue, but it is not what it seems — see below.

## What the same probe said about *our* process, and three dead hypotheses

The probe hooks `DeviceIoControl` on any handle to `vid_04a9`, so it can be pointed at `fc-viewer`
too, showing what `PortableDeviceApi.dll` issues underneath our COM calls. Doing that, plus two code
experiments, killed every remaining WPD-level explanation:

1. **"We send a bogus input buffer."** Our per-frame ioctl input was the size of the frame, because
   `READ_DATA` marshals a caller-allocated `byte[]` in via `WPD_PROPERTY_MTP_EXT_TRANSFER_DATA` —
   which the docs list as a *result*. Removing it broke even a 341-byte `GetDeviceInfo` read, so that
   property is really an in/out: the caller allocates the landing buffer, the driver fills it.
   **This also explains EDSDK's `in=2097548`:** same pre-allocated buffer one layer down. EDSDK is not
   told the frame size and reading exactly that — it over-requests 2 MiB and takes a short read.
2. **"`TOTAL_SIZE` is only an estimate for a vendor read, so the driver still holds bytes."** Tested by
   asking for `max(totalSize, 2 MiB)` per frame the way EDSDK does. The driver returned *exactly* the
   declared size every frame (265879, 266890, 268101, …). `TOTAL_SIZE` is exact, the data phase is
   exhausted, nothing is left in the pipe. An earlier drain loop of extra `READ_DATA` calls had
   already returned 0 bytes; both are now gone from the code.
3. **"The end phase is merely slow, so live view could run at a lower frame rate."** Instrumented to
   log a phase that settles *late*, then left running 80 s after streaming stopped. **Not one of 20
   pending end phases ever completed.** It does not return, at all.

Against the documentation, our end phase is exactly to spec: `END_DATA_TRANSFER` takes only
`WPD_PROPERTY_MTP_EXT_TRANSFER_CONTEXT` as input, which is what we send. There is no missing
parameter to find, and passing an empty operation-params collection (which vendor *read* commands do
require — see `CLAUDE.md`) changes nothing here.

Two further attempts, both measured, both negative:

4. **"One context streams many frames, and the drain simply asked too early."** A source producing ~10
   frames a second would return 0 bytes to a read issued microseconds after the previous frame. Tested
   by re-reading the same context after 100, 250 and 500 ms: **0 bytes every time.** One initiate
   yields exactly one frame; the camera does not keep feeding an open context.
5. **`IPortableDevice::Cancel` to reclaim the parked thread.** Returns `S_OK` and does *not* release a
   `SendCommand` already blocked in the driver — the phase never settles afterwards either.

The most useful way to think about the whole thing: **`END_DATA_TRANSFER` is the response phase of a
PTP transaction, and a viewfinder pull on this body has no response phase.** It is meaningful for
`GetObject` (finite object, response container, done) and semantically empty for `0x9153` — which is
why EDSDK issues nothing of the kind per frame and why libgphoto2, talking raw PTP, has no such phase
to begin with. WPD's misfortune is that it *books* transfer contexts and will only recycle one when
asked to end it, so it forces us to make a call whose reply is never coming.

What *is* known to work around it is unappealing: issuing the end phase and abandoning the wait keeps
the driver accepting new initiates, so frames keep arriving complete and decodable — but each
abandoned phase holds a thread forever, so streaming stops at a fixed frame count (20 on the current
bound) rather than at anything the camera did. Skipping the end phase entirely is worse: two frames,
then every later initiate reports `totalSize=0`.

## What is still unknown

* The **layout of the ioctl input buffer**. Our probe logged sizes, not bytes; the first 12 bytes are
  not a PTP container (0 records decoded as `cmd/data/resp/event`), so the PTP container is wrapped in
  a driver-specific header. Getting it is a bounded diffing exercise: hexdump the input for a command
  whose bytes we already know (e.g. `GetEvent` 0x9116, or `OpenSession` 0x1002 with its session id)
  and locate the container inside.
* Whether `0x404108` (the single read-only call) is a handshake or a capability query.
* Whether the small `in=340` follow-up per frame is the response container read, a completion
  acknowledgement, or an event poll.

## Reproducing the capture

The probes and the raw 1329-call trace are kept in `rev/`, which is **git-ignored** — it holds local
reverse-engineering artifacts that are not ours to redistribute. If you are reproducing this from a
clean clone you will need to write them yourself; they are small, and this document describes exactly
what they hook:

* `frida_wpd_hook.js` — `IPortableDevice::SendCommand` (WPD COM level), the hook that originally found
  the empty-operation-params requirement documented in `CLAUDE.md`.
* `edsdk_transport_probe.js` — `WinUsb_*Pipe` vs `CoCreateInstance` vs `CreateFileW`, i.e. *which*
  transport a host app is really using.
* `edsdk_ioctl_probe.js` — `CreateFileW` + `DeviceIoControl` + `ReadFile`/`WriteFile` on any handle to
  `vid_04a9`, with PTP-container decoding.
* `nina_hook.py` — a frida-python driver that attaches to a running PID and streams the script's
  output to a file (see the `set_log_handler` gotcha below, which is why the CLI is not enough).

Two gotchas cost real time:

* **Frida 17 removed `Module.getExportByName(module, name)`.** Use
  `Process.getModuleByName("x.dll").getExportByName("Fn")`.
* **`console.log` from a script does NOT arrive on the `message` channel.** frida-python prints it to
  the driver's own stdout, which block-buffers when redirected and silently loses the capture. Claim
  it explicitly with `script.set_log_handler(lambda level, text: ...)`.

Also: the hook must be attached **before** the camera is connected in the host app, because the
device handle is opened at connect time — reconnect the camera if you attached late.

## Where this leaves live view on a 450D

Stills capture over WPD is unaffected and reliable — 19 MB CR2 downloads run through the same
three-phase read, end phase and all. Only `0x9153` hangs the end phase, and only on this body.

Three options remain, in increasing cost:

1. **`UsbPtpTransport`** (LibUsbDotNet, bulk endpoints). Textbook PTP with no end-of-transfer phase at
   all, so it is structurally immune to this bug — which is also why libgphoto2 streams from these
   bodies. The price is a driver swap: WinUSB has to be bound with Zadig, which takes the camera away
   from the stock MTP driver, and therefore from Explorer, EDSDK and our own WPD transport, until it is
   reverted. Also unverified against real hardware and not AOT-annotated (see `CLAUDE.md`).
2. **A `DeviceIoControl` transport**, i.e. what EDSDK does. No driver swap, works alongside everything
   else, and the ioctl shape is already measured — but the input-buffer layout is undocumented and
   would have to be recovered by diffing (see "What is still unknown"). Windows-only, and a much bigger
   surface to own than the MTP extension commands we already reverse-engineered.
3. **Leave it unsupported on DIGIC III bodies** and say so plainly in the viewer, which is what the
   code currently does.
