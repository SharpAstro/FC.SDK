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

Two things follow, and they matter for our bug:

1. **EDSDK asks for ~2 MiB per frame regardless of any declared size.** It does not ask the driver
   how big the frame is and then read exactly that. Our WPD path reads exactly
   `WPD_PROPERTY_MTP_EXT_TRANSFER_TOTAL_SIZE` (~267 KB on this body).
2. **There is no separate end-of-transfer phase to hang in.** Our `ENDDATATRANSFER` (MTP ext PID 17)
   never returns for `0x9153` on this body, while it works fine for `GetObject` (0x9104) — 12 MB CR2
   downloads complete through the very same code path.

Hence the leading hypothesis for the live-view hang: WPD's `TOTAL_SIZE` is only an estimate for a
vendor read, we consume exactly that, WPD then believes the transfer is finished (a further
`READ_DATA` returns 0 bytes — measured), but the *device* still has bytes queued, so it never sends
the PTP response container and the end phase waits forever.

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

## Why this is not the first thing to try

The same probe can be pointed at **our own** process: it hooks `DeviceIoControl` on any handle to
`vid_04a9`, so attaching it to `fc-viewer` shows what `PortableDeviceApi.dll` issues underneath our
COM calls — how many bytes our `READ_DATA` really asks the driver for, and whether `END_TRANSFER`
reaches the device at all. That measurement decides the cheap fix (oversize the read) without writing
a new transport, and both traces are directly comparable since it is the same hook on the same driver.

A full ioctl transport is the last resort: Windows-only, undocumented, and a bigger surface to own
than the MTP extension commands we already reverse-engineered. Its one clear advantage over
`UsbPtpTransport` is that it needs no driver swap.
