# Live-view magnification: an operation, a hidden gate, and the only honest read-out

Measured on an EOS 6D (firmware as shipped, `EF50mm f/1.8 STM`) on 2026-08-03 with
`FC.SDK.Diagnostics --zoom` and `--evf`. Every claim below is judged on the zoom rect the body
itself reports, never on a response code — because **0x9158 answers `OK` whether or not it acts**.

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
`--evf` exercises both directions; a guard that always refused would pass a one-sided test.

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

That last row cost a whole measurement pass. Asking for "the far corner" with a big number such as
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
| **18** | `x, y, w, h` | **the zoom rect** |
| 13 | `2184, 1456, 1104, 736` | LV readout size, then display size |
| 17 | 4096 bytes | histogram — 4 channels × 256 bins × uint32 |
| 10 | `0x6A70974B, …` | a Unix timestamp and a counter |
| 0xFFFFFFFF | 32 bytes | frame header |
| 4, 5, 7, 12 | small | not identified |

Types 14 and 18 are what `CanonViewfinderFrame.TryGetZoomRect` reads, and
`ViewfinderEnvelopeTests` pins the layout against this transcript. **libgphoto2 decodes none of
them** — its `camera_capture_preview` logs every non-JPEG record without interpreting it — so there
is no second implementation to check against. A body that numbers its records differently gets
`null` rather than a wrong answer, and `IsMagnified` stays false when the sensor record is missing
rather than being inferred from the crop alone.

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
await camera.SetEvfZoomPositionAsync(x, y, ct);
var rect = await camera.GetEvfZoomRectAsync(ct);               // where it actually landed
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

- **One body.** Record numbering, the AF-method condition, the pan insets and the 1/5/10 steps are
  all n=1. A 450D has no `0x9159` to test against.
- **`DoAf` answers `OK` with no lens** and emits an `OLCInfoChanged` — it cannot be taken as
  evidence that focus happened. Not a problem for a telescope, where nothing would drive it anyway.
- Record types 4, 5, 7 and 12 are unidentified.
