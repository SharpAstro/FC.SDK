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

## The practical upshot (450D): ask for a menu number, not a raw id

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

**Correction (2026-11-17): this holds on a 450D only.** A 6D's groups and entries follow its menu
too, but it sends one group at a time and numbers its menu I, II, III as wire groups 1, 3, 4 (see
below). An EOS R6 breaks the rule: its groups mix items of several menu groups and list their
entries in ascending id order, so neither the group nor the position names the menu item. On an R6
the mapping has to be measured by changing each item on the body and watching which
id moves (`FC.SDK.Diagnostics watch`, or `cfnwalk`, which steps through a whole menu).

## A 6D sends one group at a time: the one last shown in its menu

Measured 2026-11-16 on an EOS 6D over WPD raw ioctl with `FC.SDK.Diagnostics watch`, which holds a
session open (so the body's own menus work, where a cabled 6D with no session answers every button
"busy") and prints each `0xD1A0` the body announces, diffed entry by entry. The operator stepped
through every item of the camera's C.Fn menu and said what was changed.

**`0xD1A0` on a 6D carries a single group, and which one is whatever the camera's C.Fn menu last
displayed.** Opening C.Fn I replaced the five-entry group 4 block (156 bytes) with a six-entry group 1
block (96 bytes); opening C.Fn II replaced it with group 3 (188 bytes). Each change inside the shown
group is announced at once on the event stream. Consequences:

- A dump shows one group, so a device report from a 6D lists whatever group the reporter last had
  open, and a write can only reach an entry of that group: `SetValue` is false for an id not in the
  block. Nothing writes a 6D C.Fn today (its NR and mirror lockup are properties), so nothing broke.
- **The group id is not the menu's roman numeral.** The 6D's menu has three groups, I, II and III, sent
  as wire groups 1, 3 and 4. Wire group 2 (Image on a 450D) has no menu group on this body. Entry order
  inside a group does match the menu's item order.
- The 450D sent all four groups in one block, so this is a property of the body, not of the protocol.
- The earlier note that the still/movie lever "changes the block from 156 to 188 bytes" is most likely
  this: 188 bytes is group 3, so that run caught the body showing a different C.Fn group.

| Menu | Wire id | Item (6D menu) | Values seen and their meaning |
|---|---|---|---|
| I-1 | `0x0101` | Exposure level increments | 1 → 0 (same id as the 450D's C.Fn 1, same setting) |
| I-2 | `0x0102` | ISO speed setting increments | 0 ↔ 1 |
| I-3 | `0x0104` | Bracketing auto cancel | 0 ↔ 1 |
| I-4 | `0x0105` | Bracketing sequence | 0 `0,-,+` · 1 `-,0,+` · 2 `+,0,-` (from the menu order; not changed on the wire) |
| I-5 | `0x0106` | Number of bracketed shots | two words, **the count then its menu index**: `3,0` → `2,1` → `5,2` → `7,3` (set on the body with `watch`, 2026-11-17). *Correction*: this row said the first word was the index, from the menu order, without having seen it change |
| I-6 | `0x0108` | Safety shift | 0 → 1 → 2 → 0 (off, Tv/Av, ISO) |
| II-1 | `0x0502` | Tracking sensitivity | set to +1 reads 1; probably signed, a negative step not yet seen |
| II-2 | `0x0518` | Accel./decel. tracking | 0, not changed |
| II-3 | `0x0519` | AI Servo 1st image priority | 0, not changed |
| II-4 | `0x051A` | AI Servo 2nd image priority | 0 → 2 |
| II-5 | `0x050E` | AF-assist beam firing | 0 → 1 (same id as the 450D's C.Fn 7, same setting) |
| II-6 | `0x0505` | Lens drive when AF impossible | 0 → 1 |
| II-7 | `0x0516` | Orientation linked AF point | 0 → 1 |
| II-8 | `0x0510` | Superimposed display | 0 → 1 |
| II-9 | `0x0507` | AF Microadjustment | 16 words; the first went 0 → 1 → 0 → 1 (the mode), the sixth reads 2 throughout |
| III-1 | `0x0706` | Dial direction during Tv/Av | 0 normal → 1 reverse |
| III-2 | `0x080B` | Focusing screen | 0 Eg-A II · 1 Eg-D · 2 Eg-S (0 → 1 seen) |
| III-3 | `0x070F` | Multi function lock | two words; the second is a mask, bit 0 main dial · bit 1 quick control dial · bit 2 multi-controller (2 → 7 with all three ticked). The first word, 0, is unexplained |
| III-4 | `0x040A` | Warnings in viewfinder | a mask of the four checkboxes (monochrome, WB correction, ISO expansion, spot meter); 3 → 11 seen, which box is which bit not yet settled |
| III-5 | `0x070C` | Custom Controls | 19 words, not changed; not decoded |

**Two settings now carry the same wire id on two bodies** (`0x0101` and `0x050E`, a 450D and a 6D),
which does not fit the "per-model ids" reading this document started from. Two matches are not a
rule, and `MirrorLockupIdFor` stays verified-bodies-only. But if the ids are Canon-wide setting codes,
a 200D II's mirror lockup is `0x060F` like the 450D's, and a device report from that body (issue #1)
would show whether it is.

## An EOS R6: every value set on the body, and an id order that is not the menu's

Measured 2026-11-17 on an EOS R6 over WPD raw ioctl (`ConnectWpdAutoAsync`), FC.SDK alone, with
`FC.SDK.Diagnostics cfnwalk`: for each value of each item, in the order of the camera's menu, the
operator set it on the body and the tool filed whatever arrived (property and state events, the
`0xD1A0` block diffed every second, the Av/Tv/ISO/exposure-compensation allowed lists). One pass per
value; the walk's transcripts and JSON lines are the record.

| Menu | Wire group | Wire id | Item (R6 menu) | Values set and read |
|---|---|---|---|---|
| 1-1 | 1 | `0x0101` | Exposure level increments | 0 = 1/3 · 1 = 1/2, **shared with 1-2**, see below |
| 1-2 | 1 | `0x0101` | ISO speed setting increments | 0 = 1/3 · 2 = 1-stop, **shared with 1-1** |
| 1-3 | — | — | Speed from metering/ISO Auto | nothing in the block, no property |
| 1-4 | 2 | `0x0104` | Bracketing auto cancel | 0 = ON · 1 = OFF |
| 1-5 | 2 | `0x0105` | Bracketing sequence | 0 `0,-,+` · 1 `-,0,+` · 2 `+,0,-` |
| 1-6 | 2 | `0x0106` | Number of bracketed shots | two words; the first is **the count itself**: 3, 2, 5, 7. The second read 0 throughout |
| 1-7 | 1 | `0x0108` | Safety shift | 0 OFF · 1 Tv/Av · 2 ISO |
| 2-1 | 1 | `0x0112` | Same expo. for new aperture | 0 OFF · 1 ISO · 2 Tv · 3 ISO/Tv (bit 0 ISO, bit 1 Tv; the menu lists ISO/Tv before Tv) |
| 2-2 | 3 | `0x0114` | AE lock meter. mode after focus | a mask, one bit per checkbox in menu order: evaluative 1 · partial 2 · spot 4 · centre-weighted 8 |
| 2-3 | 3 | `0x010C` | Set shutter speed range | four words, `[fastest, slowest, fastest, slowest]`, on the APEX scale below |
| 2-4 | 3 | `0x010D` | Set aperture range | four words, `[smallest aperture, largest aperture, clamped smallest, clamped largest]`: the last two are the set range clamped to the lens (below) |
| 3-1 | 5 | `0x0706` | Dial direction during Tv/Av | 0 normal · 1 reverse (the 6D's id for the same item) |
| 3-2 | 5 | `0x0712` | Control ring rotation | 0 normal · 1 reverse |
| 3-3, 3-4 | — | — | Customize buttons, Customize dials | opening either sends nothing |
| 4-1 | 5 | `0x080E` | Add cropping information | 0 OFF · 1..6 = 6:6, 3:4, 4:5, 6:7, 5:6, 5:7 |
| 4-2 | — | — | Audio compression | nothing in the block, both values |
| 4-3 | 5 | `0x0813` | Default Erase option | 0 Cancel · 1 Erase · 2 Erase RAW · 3 Erase non-RAW |
| 4-4 | 5 | `0x0711` | Release shutter w/o lens | 0 OFF · 1 ON (the body measured was on ON) |
| 4-5 | 5 | `0x0814` | Retract lens on power off | 0 ON · 1 OFF |
| 4-6 | 5 | `0x0815` | Add IPTC information | 0 OFF · 1 ON |

What it changes:

- **Neither the group nor the position names the item.** Wire group 1 holds 1-1/1-2, 1-7 and 2-1;
  group 2 holds 1-4..1-6; group 3 holds 2-2..2-4; group 5 holds all of C.Fn3 and C.Fn4. Within every
  group the entries come in ascending id order. Like a 6D, the body sends one group at a time,
  apparently the one holding the item its menu last showed; group 4 never appeared.
- **`0x0101` is shared by two items and holds whichever was set last**, on one scale (0 = 1/3, 1 = 1/2,
  2 = 1-stop). From (1/2, 1-stop) it read 2 after the ISO item was set and 1 after the exposure item
  was set again to the 1/2 it already had. So the block cannot report both settings. The allowed lists
  can: a 1/2-stop exposure gives a Tv list stepping by 4 (`0x10, 0x14, 0x18…`), a 1-stop ISO an ISO
  list stepping by 8 (`0x48, 0x50…`). No `0x0102` exists on this body.
- **The range words are APEX × 768, signed.** Eight values were
  set and all eight fit exactly: 1/8000 = 9984 and 1/4000 = 9216 (Tv 13 and 12), 30 s = -3840 and
  15 s = -3072 (Tv -5 and -4), f/91 = 9984, f/64 = 9216, f/1.0 = 0, f/1.4 = 768. The Tv allowed list
  agrees: it lost `0x10`/`0x14` (30", 20") when the slowest limit went to 15 s, and `0x9C`/`0xA0`
  (1/6000, 1/8000) when the fastest went to 1/4000. Two more set on 2026-11-18 fit too: 1/2000 = 8448
  and f/22 = 6912 (Tv 11, Av 9: the nominal stop, where f/22 is really Av 8.92). **Both range menus
  offer whole stops only**, at 1/3 and at 1/2 exposure increments alike (checked on the body the same
  day: 1/8000, 1/4000, 1/2000…, f/1.0, f/1.4, f/2…), so every word the body itself produces is a
  multiple of 768, and how it would read a word between stops is unknown. In `0x010D` the last two
  words are the set range clamped to the lens (below).
- **Six ids name the same setting as on a 6D** (`0x0101`, `0x0104`, `0x0105`, `0x0106`, `0x0108`,
  `0x0706`), which strengthens the "Canon-wide setting codes" reading above. The layout does not always
  carry over: on both bodies `0x0106`'s first word is the count, but a 6D's second word is the menu
  index (`2,1`, `5,2`, `7,3`) where an R6's stayed 0.

`CanonCustomFunctionCatalog` carries this table (`For("Canon EOS R6")`), with
`CanonCustomFunctionInfo.OptionValues` for `0x0106`, `Range` and `FormatRange` for the two ranges, and a
`Note` on the shared id.

### Writes on an R6

Measured 2026-11-17 on the same body and link with `FC.SDK.Diagnostics cfnmenu`: for each item it
writes the next option (each checkbox in turn for a mask), reads the block back past the cache 2.5 s
later, draws the menu screen the value should give, and asks the operator, who reopens the item on
the body; then it writes the original back. **One try per item**, the operator showing the item on
the body (menu open) before each write. The two ranges were written later (below).

| Item | Id | Written | Read back | The body's menu |
|---|---|---|---|---|
| 1-4 Bracketing auto cancel | `0x0104` | 0 | 0 | matches |
| 1-5 Bracketing sequence | `0x0105` | 1 | 1 | matches |
| 1-7 Safety shift | `0x0108` | 1 | 1 | matches |
| 2-1 Same expo. for new aperture | `0x0112` | 1 | 1 | matches |
| 2-2 AE lock meter. mode | `0x0114` | 1, 2, 4, 8 | each | boxes 1, 2, 3, 4 ticked in turn |
| 3-2 Control ring rotation | `0x0712` | 1 | 1 | matches |
| 4-1 Add cropping information | `0x080E` | 1 | 1 | matches |
| 4-3 Default Erase option | `0x0813` | 1 | 1 | matches |
| 4-4 Release shutter w/o lens | `0x0711` | 1 | 1 | matches |
| 4-5 Retract lens on power off | `0x0814` | 1 | 1 | matches |
| 4-6 Add IPTC information | `0x0815` | 1 | 1 | matches |
| 3-1 Dial direction during Tv/Av | `0x0706` | 1 | **0, kept its value** | — |
| 1-6 Number of bracketed shots | `0x0106` | 2 (the count) | **3,0, kept its value** | — |
| 1-1 Exposure level increments | `0x0101` | 1 | 1 | **still 1/3** (see the retries below) |
| 1-2 ISO speed setting increments | `0x0101` | 2 | **1** | — (see below) |

Every restore that was read back came back as written; the one for `0x0815` found nothing to read,
the body no longer showing its group, which is why `cfnmenu` now asks the operator to show the item
again before a restore. So eleven items take a write that the body's own menu then shows, `0x0711`
among them. Whether a written `0x0711` lets the body release with no lens was not tried: only the
menu was looked at.

**Retried the same evening, the operator closing the menu after showing the item** (`cfnmenu
--first <row> --rows 1`, which now also logs the Av/Tv/ISO/exposure-compensation allowed lists
around every write):

- **`0x0706` and `0x0106` refuse writes.** `0x0706` kept 0 when given 1, 2 tries of 2; `0x0106` kept
  `3,0` when given the count 2 (2 of 2) and when given the menu index 1 (`--value 1`, 1 of 1). Open or
  closed menu made no difference. Whether another value or a different word would take is untried.
- **Do not write `0x0101`: a write moves the allowed lists, not necessarily the setting.**
  *Correction*: this bullet first said a write "sets the exposure increment", judged on the Av/Tv
  allowed lists. The operator then checked the body itself, setting each starting value on the camera
  and counting main-dial clicks from 1/125 to 1/250 (3 clicks = 1/3-stop, 2 = 1/2-stop):

  | Body before (menu) | `0x0101` written | Read back | Av and Tv lists | Body after |
  |---|---|---|---|---|
  | exposure 1/3 | 1 | 1 | 1/3-stop (22 Av values) → 1/2-stop (15) | menu 1/3, **3 clicks: still 1/3**, 2 tries |
  | exposure 1/2 | 0 | 0 | 1/2-stop → 1/3-stop | menu 1/3 (clicks not counted), 2 tries |
  | ISO 1-stop (C.Fn1-2 showing) | 0 | 0 | 1/2-stop → 1/3-stop | ISO menu still 1-stop, 2 tries |
  | ISO 1-stop | 2 | **1** | 1/3-stop → 1/2-stop | not looked at |

  So the lists follow the written value both ways, but the body's own setting moved only from 1/2 to
  1/3, as far as its menu shows, and a write of 1 left a body at 1/3 dialling in thirds while
  announcing half-stop lists — **after a write the lists can lie**, so a host reading them would offer
  values the dial does not step through. The ISO list (`0xD103`) and the ISO item never moved on any
  write. Writing 2 reads back 1. n is two or fewer per row and the 1/2 → 1/3 row has no click count, so
  the asymmetry is an observation, not a rule.

  The catalog marks both `0x0101` rows `Writable = false` (#51): the viewer's Custom Functions panel
  shows the value and this note with no step buttons, and `cfnmenu` skips them unless given
  `--include-unwritable`, which is how to repeat the table above (it was measured on 2026-11-17, before the flag
  existed, when `cfnmenu` wrote every item).

**The two ranges take writes, but only whole stops display** (same body, 2026-11-17, `cfnmenu --first 9`
and `--first 10`, one limit moved at a time, the body at 1/2-stop exposure increments throughout).
A first pass moved each limit by a third of a stop; a second by a whole stop:

| Limit | Third-stop write | Its menu | Whole-stop write | Its menu |
|---|---|---|---|---|
| fastest speed | 1/4000 → 1/3200 | "highest 15\"" ✗ | 1/8000 → 1/4000 | matches |
| slowest speed | 15" → 13" | "lowest 30\"" ✗ | 30" → 15" | matches |
| smallest aperture | f/64 → f/57 | "min 1.4" ✗ | f/64 → f/45 | matches |
| largest aperture | f/1.4 → f/1.6 | "max 1.0" ✗ | f/1.0 → f/1.4 | matches |

Every write and every restore read back as written, and the Tv allowed list followed each one (the
speeds beyond a new limit left it: `0x9C`, `0xA0` for a fastest limit of 1/4000, `0x98` for 1/3200).
The Av list moved only where the new limit fell inside the lens's own range (f/1.4–f/16): `0x10`,
f/1.4, left it when the largest aperture closed to f/1.6, and it did not move for f/64 → f/57,
f/64 → f/45 or f/1.0 → f/1.4. So the body stores whatever is written. **But the menu offers whole
stops only, and a third-stop value written to it is drawn as values nobody set**; every whole-stop
write showed correctly. A third run, started from a fastest limit left at 1/3200 by an earlier write,
wrote 1/1600 (drawn as "15″, 15″") and then a slowest limit of 8" (drawn as "low 8″", correct,
beside the still wrong fastest one). *Correction*: this paragraph first put the nonsense down to the
body being at 1/2-stop increments. The menus were then checked at both increments (2026-11-18) and
offer whole stops either way, so a third-stop write is off the menu's scale on any setting. The
third-stop words were written as APEX × 768 at 256 a third, which the body never produces itself.

**The shutter limits are real, checked on the dial** (the `0x0101` lesson: lists alone are not
enough). In Tv mode, the operator turned the main dial to both ends before the run (1/8000 to 30"),
after each write, and after the restore (1/8000 to 30" again):

| `0x010C` written | Dial in Tv mode |
|---|---|
| fastest limit 1/4000 | stops at 1/4000 |
| slowest limit 15" | stops at 15" |

One run, 2026-11-17.

**The aperture limits are real too.** Same body, Sigma 35mm F1.4 on it (f/1.4–f/16), limits written
inside the lens's range with `cfnmenu --first 10 --rows 1 --value <v> --word <n>`, one limit per run;
in Av mode the dial ran f/1.4 to f/16 before the first run:

| `0x010D` written | Read back | Dial in Av mode | Av allowed list |
|---|---|---|---|
| word 0 = 4608, smallest aperture f/8 | `4608,0,4608,768` | stops at f/8 | ends at `0x38` (f/8) |
| word 1 = 2304, largest aperture f/2.8 | `9216,2304,6144,2304` | stops at f/2.8 | starts at `0x20` (f/2.8) |

Words 2 and 3 followed as the clamp predicts. After each restore the block read `9216,0,6144,768`
again and the Av list went back to f/1.4–f/16; the dial was not re-checked after the second run.
One run each.

The words the body wrote itself:

- **`0x010C`**: every write to word 0 or 1 came back copied into word 2 or 3.
- **`0x010D`**: during the `cfnmenu` runs word 2 stayed 6144 (f/16); word 3 stayed 768 (f/1.4) with
  word 1 at f/1.0 or f/1.4, and followed word 1 to 1024 when it was set to f/1.6. f/16 and f/1.4 are the limits of
  the lens on the body (Sigma 35mm F1.4 DG HSM Art, EF, on the EF-EOS R adapter). **Words 2 and 3
  are the set range clamped to the lens on the body**, checked by changing the lens with `watch`
  running and the set range at f/64–f/1.0 (`9216,0`), nothing written:

  | On the body (`LensStatus` 0xD1A8) | `0x010D` | Words 2, 3 |
  |---|---|---|
  | Sigma 35mm F1.4 (1) | `9216,0,6144,768` | f/16, f/1.4 |
  | no lens (0) | `9216,0,9216,0` | f/64, f/1.0: the set range itself |
  | Sigma 24-70mm F2.8 DG OS HSM Art (1) | `9216,0,6912,2304` | f/22, f/2.8 |
  | no lens (0) | `9216,0,9216,0` | f/64, f/1.0 |
  | Sigma 35mm F1.4 again (1) | `9216,0,6144,768` | f/16, f/1.4 |

  Each change showed in the block within 1.5 s of the lens announcement. So word 2 is the narrower of
  the set limit and the lens's smallest aperture, word 3 the wider-numbered of the set limit and the
  lens's largest one (the f/1.6 write above came back as f/1.6 for the same reason). Read only: what a
  write to words 2 or 3 does is untried.

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
