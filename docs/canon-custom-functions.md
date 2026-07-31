# Canon Custom Functions: the block layout, and why the ids are still ours to find

Two things came out of reading `EDSDK.dll` (13.18.40.6400, the copy N.I.N.A. ships at
`External\x64\Canon\EDSDK.dll`) rather than only the Ghidra decompile of it: the block's group
header is 3 words wide, not 2 as EDSDK's own writer suggests at a glance, and EDSDK has **no**
per-model table that resolves a setting like "mirror lockup" to a wire id. Both are settled by
reading actual bytes, not by inference.

## The block layout

`CanonCustomFunctionBlock.Parse` (`src/FC.SDK/Canon/CanonCustomFunction.cs`) reads each group
header as three words — `[group_id][group_size][entry_count]`, entries starting at `+12` — derived
from a real 450D dump before this was verified against EDSDK. EDSDK's C.Fn writer,
`FUN_1800614e0` (`rev/EDSDK_decompiled.c:83574`), walks the *cached, already-parsed* form of the
block and only ever touches `[group_id][entry_count]` plus the entries — it never reads a
`group_size` word, because by the time it runs the block has already been split into per-group
sub-fetches by an earlier stage. That is not evidence our 3-word read is wrong; it means EDSDK's
in-memory representation and the wire representation are not the same shape, so the writer's field
count doesn't transfer.

The wire format is settled by a real 450D capture (`fc-viewer-*.log`, group dump logged via
`ViewerActions.cs`'s "Custom-function block" trace):

```
000000d4 00000004                          212 bytes total, 4 groups
  00000001 00000020 00000002               group 1: id=1, size=0x20, 2 entries
    00000101 00000001 00000000                C.Fn 0x0101 = 0
    0000010f 00000001 00000000                C.Fn 0x010f = 0
  00000002 00000038 00000004               group 2: id=2, size=0x38, 4 entries
    00000201 00000001 00000000                C.Fn 0x0201 = 0   (menu C.Fn 3: long exposure NR)
    00000202 00000001 00000000                C.Fn 0x0202 = 0   (menu C.Fn 4: high-ISO NR)
    00000203 00000001 00000000                C.Fn 0x0203 = 0
    00000204 00000001 00000000                C.Fn 0x0204 = 0
  00000003 0000002c 00000003               group 3: id=3, size=0x2c, 3 entries
    0000050e 00000001 00000000                C.Fn 0x050e = 0
    00000511 00000001 00000002                C.Fn 0x0511 = 2
    0000060f 00000001 00000000                C.Fn 0x060f = 0   (menu C.Fn 9: mirror lockup)
  00000004 00000038 00000004               group 4: id=4, size=0x38, 4 entries
    00000701 00000001 00000000                C.Fn 0x0701 = 0
    00000704 00000001 00000000                C.Fn 0x0704 = 0
    00000811 00000001 00000000                C.Fn 0x0811 = 0
    0000080f 00000001 00000000                C.Fn 0x080f = 0
```

`group_size` only balances under the 3-word reading: group 1 is 9 words = 36 bytes, minus its own
size word = 32 = `0x20`. Group 2 is 15 words = 60 bytes, minus 4 = 56 = `0x38`. Group 3 is 12 words
= 48 bytes, minus 4 = 44 = `0x2c`. All four groups check out exactly. A 2-word header would leave
`group_size` unexplained and unused — so `CanonCustomFunction.cs` is correct for the wire, and stays
as-is.

## The 450D dump matches its manual exactly

Cross-checking the wire dump above against `CUG_EOS450D_EN_Flat.pdf`'s Custom Function chapter
(p.153) confirms the group/entry layout is not just internally consistent, it's the camera's own
menu, in order:

| Group | Manual heading | Manual items (menu C.Fn #) | Wire ids, in entry order |
|---|---|---|---|
| 1 | C.Fn I: Exposure | 1 Exposure level increments · 2 Flash sync. speed in Av mode | `0x0101`, `0x010F` |
| 2 | C.Fn II: Image | 3 Long exposure NR · 4 High ISO NR · 5 Highlight tone priority · 6 Auto Lighting Optimizer | `0x0201`, `0x0202`, `0x0203`, `0x0204` |
| 3 | C.Fn III: Autofocus/Drive | 7 AF-assist beam firing · 8 AF during Live View · 9 Mirror lockup | `0x050E`, `0x0511`, `0x060F` |
| 4 | C.Fn IV: Operation/Others | 10 Shutter/AE lock button · 11 SET button when shooting · 12 LCD display when power ON · 13 Add original decision data | `0x0701`, `0x0704`, `0x0811`, `0x080F` |

Four groups, group sizes 2/4/3/4 entries — exactly the manual's four C.Fn groups with exactly the
manual's item counts, in exactly the manual's order. This is what already-verified `LongExposureNR_450D`
(`0x0201`), `HighIsoNR_450D` (`0x0202`) and `MirrorLockup_450D` (`0x060F`) in
`CanonCustomFunctionId` were checked against.

## Are the ids still model-dependent? Yes — and EDSDK does not solve this either

Same three settings, different bodies, from their own manuals:

| Setting | EOS 450D (`CUG_EOS450D`) | EOS 6D (`eos6d-im7`) | EOS Rebel SL3 / 200D II (`eosrebelsl3-eos200d2-ug2`) |
|---|---|---|---|
| Long exposure NR | **C.Fn 3**, group II "Image" (p.153) | **not a C.Fn** — `[z4]` shooting tab (p.302, 4312) | **not a C.Fn** — `[z]` shooting tab (p.137-138) |
| High-ISO NR | **C.Fn 4**, group II "Image" (p.153) | **not a C.Fn** — `[z4]` shooting tab (p.302, 4282) | **not a C.Fn** — `[z]` shooting tab (p.137) |
| Mirror lockup | **C.Fn 9**, group III "AF/Drive" (p.153) | **not a C.Fn** — `[z2]` shooting tab (p.164) | **C.Fn 5**, group II "Drive" (p.446, 449) |

So the same setting moves in and out of the Custom Function block across generations, and it doesn't
move in lockstep — the 200D II (DIGIC 8) still keeps mirror lockup as a Custom Function while both
noise-reduction settings graduated to plain properties, matching the 6D. `CanonCustomFunctionId`
previously carried guessed `LongExposureNR_6D`/`HighIsoNR_6D` constants (`0x0102`/`0x0103`) that this
manual evidence contradicts directly — the 6D manual's own Custom Function tables (p.302-303) don't
list either setting at all, because they aren't Custom Functions on that body. Those constants were
never wired into `HighIsoNrIdFor`/`MirrorLockupIdFor` (only the 450D family was), so nothing was
broken in practice, but they've been removed rather than left to mislead a future per-body addition.

This is also good news for issue #1 (Canon EOS 200D II): the manual placement predicts that body's
`0xD178` should just work as a direct property, the same way it does on the 6D — no C.Fn fallback
needed for noise reduction at all. Mirror lockup is the opposite story: the 200D II manual explicitly
puts it in C.Fn group II ("Drive") as item 5, so a block dump from that body should show it as the
second entry of whichever group is `group_id`-tagged 2 — matching against the reporter's own on-camera
menu the same way the 450D table above does, no raw id needed up front.

What reading the DLL settles is *how EDSDK deals with it*: it doesn't. Two independent pieces of
evidence:

1. **The writer takes the id from its caller.** `FUN_1800614e0` receives the target id as a plain
   parameter and linearly searches groups 1–16 (`uVar14 = 1; ... uVar14 < 0x10`) for an entry whose
   id equals it (`rev/EDSDK_decompiled.c:83574`–`83660`). There is no per-model branch, no model
   string compared, no lookup keyed on anything but the id the caller already supplied. Whoever
   calls into EDSDK for a C.Fn write has to already know which id means what on that body.

2. **No such table exists in the binary at all.** `rev/extract_propid_map.py` reads EDSDK's real
   property table straight out of `.rdata` (see that script and `rev/edsdk-propid-ptp-map.md` for
   the full 234-entry result) — and the entire `0xD180`–`0xD1A0` range, which is where
   `CustomFunc1`..`CustomFunc19` and `CustomFuncEx` sit in libgphoto2's numbering, is completely
   absent from it. EDSDK's property table only covers properties it can name and describe; the C.Fn
   block isn't one of them, consistent with `FUN_1800614e0` treating it as an opaque blob addressed
   by a caller-supplied id rather than a property EDSDK understands the contents of. The `.rsrc`
   section is checked too, in case Canon shipped a side table there instead — it's a version resource
   and an app manifest, nothing else. Nor is there a companion data file next to `EDSDK.dll` in the
   N.I.N.A. install; the DLL is the only artifact shipped for this platform/architecture.

So the per-model knowledge a caller needs isn't hiding somewhere in `EDSDK.dll` waiting to be
extracted — Canon's own EOS Utility either ships its own separate table or was tested per body by
hand, the same position this SDK is already in with
`CanonCustomFunctionId.MirrorLockupIdFor`/`HighIsoNrIdFor`. Confirms the CLAUDE.md rule to never
guess an id for an unverified body; there is no shortcut through the SDK to find one.

## The practical upshot: ask for a menu number, not a raw id

Because the group/entry layout is now confirmed against real EDSDK internals rather than a single
dump, `FunctionGroups` and entry order can be trusted for any body, and the on-camera menu number is
recoverable purely from a dumped block plus group order — no protocol analyser needed:

```
menu C.Fn 3  = group 2, entry 1  →  wire id from the dump
menu C.Fn 4  = group 2, entry 2  →  wire id from the dump
menu C.Fn 9  = group 3, entry 3  →  wire id from the dump
```

That means extending `CanonCustomFunctionId` to a new body no longer needs someone with a protocol
analyser to hand over a raw hex id — a reporter can dump the block (already logged by the viewer's
C.Fn action) and say which menu number does what, read straight off the camera's own menu.

## EDSDK's property table, separately

While reading `.rdata` for the above, the same pass recovered EDSDK's PTP-code ↔ `EdsPropertyId`
table wholesale: 234 pairs across 36 contiguous runs, keyed by
`{u32 ptpCode, u32 0, u64 handler, u32 edsPropertyId, u32 0}` records. It corroborates
`CanonPropertyMap` everywhere the two overlap — including the two properties the decompile-only
method couldn't reach because they have no setter, `TempStatus` (`0xD1AB`) and `MirrorLockUpState`
(`0xD1BF`) — and surfaces one naming gap worth fixing: `0xD178` (libgphoto2's
`HighISONoiseReduction`) resolves in EDSDK's table to id `0x0100043D`, not to
`EdsPropertyId.NoiseReduction` (`0x00000411`), which `CanonPropertyMap` currently keys it under.
`0x00000411` does not appear anywhere in EDSDK's table. Not a wire bug — `CanonPropertyMap` only
uses the enum member as a lookup key, not the numeric value — but the name is arguably already
spoken for by `EdsLongExposureNR`, so a future `HighISONoiseReduction` member matching EDSDK's own
naming would remove the ambiguity.

See `rev/extract_propid_map.py` for the extractor and its docstring for the full method; run it
against a local `EDSDK.dll` copy to regenerate `rev/edsdk-propid-ptp-map.md` (both are outside git —
`rev/` is `.gitignore`d — so they exist only on machines that have generated them).
