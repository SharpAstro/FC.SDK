# Live-view magnification: an operation, a hidden gate, and the only honest read-out

Measured on an EOS 6D (firmware as shipped, `EF50mm f/1.8 STM`) on 2026-08-03 with
`FC.SDK.Diagnostics zoom` and `evf` (subcommands now, not `--zoom`/`--evf`; the flag form was
replaced by System.CommandLine and errors today). Every claim below is judged on the zoom rect the
body itself reports, never on a response code, because **0x9158 answers `OK` whether or not it acts**.

## Zoom is an operation, not a property

EDSDK models magnification as the properties `kEdsPropID_Evf_Zoom` (0x507) and
`Evf_ZoomPosition` (0x508). There is no such PTP property. libgphoto2's `ptp.h`:

```c
#define PTP_OC_CANON_EOS_Zoom          0x9158 /* 1 arg: zoom */
#define PTP_OC_CANON_EOS_ZoomPosition  0x9159 /* 2 args: x,y */
```

Both are plain no-data operations with uint32 parameters. This matters because a consumer had
already deferred work on the opposite assumption — `../tianwen` recorded that EVF zoom-pan needed a
point/rect *property* accessor and shelved its planetary mode waiting for one. It never did; the pan
actuator is two integers on an operation.

## The gate: `LvAfSystem` (0xD1BA) can silently block magnification

The finding that cost the most to reach, because everything answered `OK` throughout. With an
`EF50mm f/1.8 STM` mounted:

| `0xD1BA` | libgphoto2 name | Zoom rect after `Zoom(5)` |
|---|---|---|
| 2 — **the factory default** | `LiveFace` | `(0,0) 5472x3648` — **ignored** |
| 1 | `Live` (FlexiZone-Single) | `(2184,1456) 1104x736` |
| 0 | `Quick` (phase-detect) | `(2184,1456) 1104x736` |

This mirrors the camera's own UI, where the magnify button is disabled in face-tracking AF. Over PTP
there is no such feedback: the command is accepted and discarded, the feed stays at full frame, and
the response code is `OK` in every cell of that table.

### It is a condition, not a rule — and that is why the SDK verifies

**With no lens mounted the same body magnifies happily in `LiveFace`.** Re-measured with a bare
mount, which is the honest proxy for a telescope: `Evf_AFMode` still reads, still announces
`Quick, Live, LiveFace`, is still writable and reads back — and `Zoom(5)` crops in every one of
them. The pan inset disappears too (see below).

So the blocking condition depends on what is on the mount, and encoding "LiveFace blocks zoom" as a
rule would have been wrong for exactly the configuration this feature exists for.
`CanonCamera.SetEvfZoomAsync` instead **verifies by default**: it reads the zoom rect back and
answers `OperationRefused` only when the frame really did not crop — the same pattern as the
mirror-lockup refusal. That gets both cases right without knowing which one it is in.

`Live` worked in every configuration measured, so it stays the recommended setting before zooming.
`evf` exercises both directions; a guard that always refused would pass a one-sided test.

### Verifying a step between two magnified levels needs the crop width

`IsMagnified` cannot mark a 5x → 10x change: it is already true before the body acts, so a stale
pre-zoom frame satisfies it instantly and a body that ignored the step would still be reported as
having honoured it. `SetEvfZoomAsync` therefore reads the rect first and, when both sides are
magnified, waits for `Width` to move instead. One case stays genuinely undecidable: because the
factor is a threshold, asking for 6 while already at 5x legitimately changes nothing, and that is
indistinguishable from being ignored without a per-body factor table. It logs and returns `OK` rather
than inventing a refusal.

## The factor is a threshold, not a value

Sweeping the parameter with the gate open:

| Asked | Rect | Real factor |
|---|---|---|
| 1, 2, 3, 4 | `(0,0) 5472x3648` | 1.00× |
| 5, 6, 8 | `(2184,1456) 1104x736` | **4.96×** |
| 10, 15 | `(2184,1456) 552x368` | **9.91×** |

So only 1 / 5 / 10 exist; anything else rounds down to the next real step, and nothing errors. Note
5× is really 4.96× — the crop is a whole number of pixels, not an exact fifth. Ask for what you want
and read `GetEvfZoomRectAsync` for what you got.

**This corrects how the 10× row was read.** `(2184,1456) 552x368` is not the area shown at 10×: the
rect (record 18) is the origin of the image sent, then the size of the area shown, and at 10× the
image sent is still the 5× one. The 10× area itself is the magnifier rect, record 13:
`(2460,1640) 552x368` when centred. Measured on a 6D and an R6 over WPD raw ioctl in issue #21
(2026-11-13), identical on both; see "The envelope records" below.

## Panning: two different out-of-range behaviours, and only one of them is a clamp

`ZoomPosition(x, y)` sets the **rect origin in sensor pixels**. Each axis is handled independently,
and there are three regimes — the third one is the trap:

| Asked | Landed | |
|---|---|---|
| (1000,500) | (1000,500) | exact |
| (2184,1456) | (2184,1456) | exact — centred |
| (0,0) | (271,364) | clamped up to the inset |
| (4368,2912) | (4097,2548) | clamped down to the inset |
| (5471,0) | *unchanged* | **discarded** |
| (9999,9999) | *unchanged* | **discarded** |

- **Accepted** while `x ≤ sensorWidth − cropWidth` (4368 at 5× on this body) and likewise for y.
- Within that, the body **clamps** to a box inset by one crop edge: `5472 − 271 − 1104 = 4097`,
  `3648 − 364 − 736 = 2548`.
- **Beyond it the axis is silently discarded** and keeps its previous value.

**The inset only exists with a lens attached.** On a bare mount `(0,0)` lands on `(0,0)` and
`(4368,2912)` on `(4368,2912)` — both exact, the full `[0, sensor − crop]` range usable. Consistent
with the frame being the AF frame in `Live` mode: no lens, no AF-frame constraint, no inset. The
acceptance limit is unchanged either way, so clamping to `sensor − crop` is correct in both.

**A pan at 1× is accepted too.** This was long assumed to be discarded, by arithmetic on the rule
above rather than by sending one. Sent on an R6 and a 6D (issue #21): at 1×, `ZoomPosition`
moves the magnifier rect (record 13), record 5, and in `Live` the AF frame (record 8), clamped by the
body to its AF area — (300,200) lands on `(300,212)` on an R6 — while the zoom rect (record 18) stays
`(0,0) 5472x3648`. `SetEvfZoomPositionAsync` therefore settles on record 13: on record 18 a 1× pan
waited out the whole 3 s timeout.

| `SetEvfZoomPositionAsync(1000,1000)`, default `verify` | Before (settles on 18) | After (settles on 13) |
|---|---|---|
| 6D at 1× | 3034–3144 ms, 6 tries | 151–289 ms, 6 tries |
| R6 at 1× | 3021–3134 ms, 6 tries | 371–408 ms, 6 tries |
| 6D at 5×, there and back | 375–509 ms | 366–501 ms |
| R6 at 5×, there and back | 142–395 ms | 257–392 ms |

Same sitting, builds alternated, WPD raw ioctl, 2026-11-13. Record 13 read the position asked for
after every call. Re-run over WPD COM and PTP/IP on 2026-11-14, the 1× pan went from 3039–3554 ms
to 174–657 ms on a 6D, and from 3099–3146 ms to 391–425 ms on an R6 over COM. An R6 over PTP/IP
sometimes stalls a fetch for 1.6–1.8 s, and there a 1× pan took 609–4595 ms and a 5× one up to
5640 ms. On that transport a pan can also return a position from before the move, as the settle on
record 18 could too; the rule that would rule it out is still open.

That last row of the pan table above cost a whole measurement pass. Asking for "the far corner" with a big number such as
9999 moves nothing at all, and since the previous position is still reported it looks exactly like a
pan that does not work — the first reading of this table recorded 9999 as "clamped to (4097,2548)"
purely because the preceding request had legitimately clamped there.

`SetEvfZoomPositionAsync` therefore clamps into the accepted range itself before sending, so asking
for a corner gets the corner. Units are per-body — libgphoto2 measured "approx 64 pixel steps on the
EOS 1000D" — so calibrate against the rect rather than carrying a constant between models.

### The pixels, not just the rect

The rect is the body's own claim, so it was checked against image content. At 5× with the crop at
`(271,364)` the frame is the scene's dark top-left corner; at `(4097,2548)` it is the tablecloth,
with **individual threads of the weave resolved**. Two entirely different regions, each 1104×736.

Two structural corroborations came out of that comparison:

- **The JPEG's own dimensions change with the zoom.** 1× streams 960×640 — the whole frame,
  downscaled. 5× streams **1104×736, exactly the rect size**. The feed is a true 1:1 sensor crop,
  not a magnified preview: an upscale of the 960×640 image could not resolve the weave.
- **10× streams the 5× image.** Still 1104×736, and on a 6D the 5× and 10× frames show the same
  field, the same edges at the same places, not a 2× enlargement (issue #21). The 10× area is the
  magnifier rect inside it, at `(13.x − 18.x, 13.y − 18.y)` = (276,184) when centred.
- Frames must be exposed properly before any of this is visible. An early pass ran wide open and
  every frame — cropped or not — was a white rectangle, a visual test that could not have failed.

In `Live` (FlexiZone-Single) AF the magnification frame **is** the AF frame, so panning the zoom
position also moves the AF point. Confirmed visually: after panning to (4097,2548) the frame sits in
the bottom-right corner of the camera's own screen.

## The envelope records, decoded

`GetViewFinderData` (0x9153) returns `[length:u32][type:u32][payload…]` records. Only the image
record was decoded before; a 6D sends twelve. Identified by driving the body through known states
and watching which tracked:

| Type | Payload | Meaning |
|---|---|---|
| 1 / 9 / 11 | JPEG | the preview image (9 is movie mode) |
| **14** | `5472, 3648` | **full sensor size** |
| **18** | `x, y, w, h` | **the zoom rect**: origin of the image sent, then size of the area shown |
| **13** | `x, y, w, h` | **the magnifier rect**, moved by a pan at every magnification |
| **4** | `1` / `5` / `10` | **magnification** |
| 5 | `x, y` | magnifier origin, the first half of 13 |
| **8** | `count, state, 1, x, y, w, h` | **the AF frame**; state 16 at rest, 17 while focus is achieved |
| **7** | `1` / `2` | **AF busy**: 2 while the body drives AF |
| **17** | 4096 bytes | **exposure histogram** — 4 channels × 256 bins × uint32, decoded and confirmed |
| 10 | `0x6A70974B, …` | a Unix timestamp and a counter |
| 0xFFFFFFFF | 32–56 bytes | frame header |
| 12 | `0` | not identified (a 6D sends it, an R6 does not) |

**This corrects the row that read 13 as "LV readout size, then display size".** That reading came
from one transcript taken centred, where 13 and the 5× rect coincide. Records 4, 5, 7, 8 and 13 were
identified on an R6 and a 6D in issue #21 by zooming, panning and closing the focus line while the
bytes were watched. Centred, sensor 5472×3648, identical on both bodies except record 8's size:

| Type | 1× | 5× | 10× | 10×, panned to (1000,700) |
|---|---|---|---|---|
| 4 | `1` | `5` | `10` | `10` |
| 13 | `2184,1456,1104,736` | `2184,1456,1104,736` | `2460,1640,552,368` | `1000,700,552,368` |
| 18 | `0,0,5472,3648` | `2184,1456,1104,736` | `2184,1456,552,368` | `724,516,552,368` |
| 8, 6D | `1,16,1,2460,1456,552,736` | `1,16,1,2554,1640,364,368` | `1,16,1,2645,1732,182,184` | `1,16,1,1185,792,182,184` |
| 8, R6 | `1,16,1,2536,1608,400,432` | `1,16,1,2552,1640,368,368` | `1,16,1,2644,1732,184,184` | `1,16,1,1184,792,184,184` |
| JPEG | 960×640 | 1104×736 | 1104×736 | 1104×736 |

`CanonCamera.GetLiveViewFrameAndInfoAsync` returns the JPEG (a slice of the envelope, no copy) with
these decoded as `CanonEvfFrameInfo`, from one fetch; record 8 is decoded only for one frame, the
layout measured, and returned raw otherwise. Types 14 and 18 are also what
`CanonViewfinderFrame.TryGetZoomRect` reads. `ViewfinderEnvelopeTests` and `EvfFrameInfoTests` pin the
layout against 6D and R6 transcripts. **libgphoto2 decodes none of them** — its
`camera_capture_preview` logs every non-JPEG record without interpreting it — so there is no second
implementation to check against. A body that numbers its records differently gets `null` rather than
a wrong answer, and `IsMagnified` stays false when the sensor record is missing rather than being
inferred from the crop alone.

### Record 17 is a real light meter, and it is linear

Decoded as `CanonEvfHistogram` and confirmed on the 6D by `FC.SDK.Diagnostics meter`, which tests it
three ways so a plausible-looking blob cannot pass:

- **Structure.** All four groups counted **345,600** pixels, so they are four histograms of one image
  rather than four unrelated arrays. Note that is 720×480, a reduction of its own: not the streamed
  JPEG's 960×640, and not the sensor. The decode returns `null` when the groups disagree.
- **Channel order.** Luma is a fixed blend, so of the 24 assignments only one satisfies
  `Y = 0.299R + 0.587G + 0.114B`. Y,R,G,B won with a residual of **0.03%** against 1.49% for the
  runner-up. Corroborated physically: the means read R 71.8% / G 47.6% / B 18.0% under a warm LED,
  which is the ordering that light demands.
- **Liveness.** An ISO sweep moved the mean **2.49% → 9.87% → 33.22% → 70.82%**, monotonically. The
  first step is **3.96× for a 4× light increase**, so the histogram is **linear in light, not gamma
  encoded**; later steps compress only because p99 approaches clipping. That is what makes two
  `MeanLevel` readings comparable as an exposure ratio, with stops being `log2` of it.

This matters beyond diagnostics: it is the only live metering an EOS offers over PTP. `MeteringMode`
(0xD107) and `ExposureCompensation` (0xD104) say how the body is *configured* to meter; nothing
reports a metered value. On a dial at Manual, this is the only way to know a frame is exposed without
spending a shutter actuation to find out.

**Still unknown: whether the histogram covers the magnified crop or the full field when zoomed.** That
is exactly the question for metering a star at 10×, and one `meter` run while magnified answers it.

## Detecting whether autofocus is possible at all

Nothing in the protocol reports "I cannot focus" — a bare mount and a lens switched to MF both
answer an AF command with `OK` and move nothing. Both are distinguishable, though, and
`GetFocusStateAsync` combines them:

| Configuration | `LensName` (0xD1D8) | `AFMode` (0xD108) | `AutoFocusAvailable` |
|---|---|---|---|
| No lens | `""` | `OneShot` | false |
| Lens, switch at AF | `EF50mm f/1.8 STM` | `OneShot` | true |
| Lens, switch at MF | `EF50mm f/1.8 STM` | **`ManualFocus`** | false |

**`0xD108` follows the lens's own AF/MF switch**, established by flipping it with the lens mounted
and diffing. `FocusInfoEx` (0xD1D3) tracks it too — the uint16 at offset 6 goes `2 → 0` — but the
focus mode is the readable one.

The trap on the way there is worth keeping. `0xD108`'s allowed-value list is
`{OneShot, AIServo, AIFocus}` — `ManualFocus` is **not in it** — which made the switch look
undetectable through this property. That inference was wrong: **allowed values are what a client may
write, not the values a property can report.** A property the body sets for itself can report
outside its own writable set.

Note the focus mode alone cannot tell a bare mount from a lens at AF; both read `OneShot`. Lens
presence has to come from the name.

## Live-view autofocus works

`DoAf` (0x9154) returns in ~8 ms — on acceptance, not on focus. Confirmed mechanically: the STM
motor was audible, and the frame immediately after went from ~51 KB to ~120 KB, which is what a
JPEG does when the scene comes into focus. This is the live-view counterpart to the mirror-path
half-press (0x9128 param 1), which drives the phase-detect sensor and is blind while the mirror is
up.

## Recipe

```csharp
await camera.StartLiveViewAsync(ct);
await camera.SetEvfAfSystemAsync(CanonEvfAfSystem.Live, ct);   // or the zoom is silently ignored
await camera.SetEvfZoomAsync(CanonEvfZoom.X5, ct: ct);         // verifies; OperationRefused if blocked
var (_, landed) = await camera.SetEvfZoomPositionAsync(x, y, ct: ct);  // waits for the move to show
var (err, jpeg, info) = await camera.GetLiveViewFrameAndInfoAsync(ct);
// About half the polls answer ObjectNotReady with no image: read again until err is OK and jpeg is
// not empty. info.Magnifier: where the magnifier is; info.ImageOrigin + ShownSize: the zoom rect.
```

## The telescope case works

Re-measured with the lens removed, which is what a body on a Dobsonian looks like electrically — no
contacts either way. Everything the astro use needs is intact, and two constraints relax:

| | With EF lens | Bare mount |
|---|---|---|
| `Zoom(5)` / `Zoom(10)` | works | **works** |
| `Evf_AFMode` readable, writable, allowed values | yes | **yes** |
| Zoom in `LiveFace` | ignored | **works** |
| Pan range | inset to `[271, 4097]` | **full `[0, 4368]`** |
| `LensName` | `EF50mm f/1.8 STM` | `""` |
| `DoAf` | focuses (STM audible) | `OK`, nothing to drive |

So 10× live view — the way focus is actually achieved on a telescope — is available, and pans across
the whole sensor.

## Open

- **The AF-method condition is n=1** (a 6D). The 1/5/10 steps, the record numbering and the pan
  inset are confirmed on an R6 too (issue #21): at 5× it moves `(0,0)` to `(196,212)`, where a 6D
  moves it to `(271,364)`. A 450D has no `0x9159` to test against.
- **Record 8 with more than one frame** (zone AF, faces) has never been captured: whether the state
  word is per frame or shared is unknown, so `CanonEvfFrameInfo` returns that record raw.
- **`DoAf` answers `OK` with no lens** and emits an `OLCInfoChanged` — it cannot be taken as
  evidence that focus happened. Not a problem for a telescope, where nothing would drive it anyway.
- Record type 12 is unidentified, as are the records only an R6 sends (16, 19, 20, 21, 22, 24, 28,
  29).
