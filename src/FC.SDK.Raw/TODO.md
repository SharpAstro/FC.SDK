# FC.SDK.Raw — TODO

Follow-ups for the Canon raw decoder. The current driver is TianWen's
astro-imaging pipeline (linear sensor values, custom stretch), but this
library is general-purpose — most items below land somewhere on the
spectrum between "consumer raw viewer" and "astro decode."

## Image rendering / output

- [ ] **Tone mapping stage in `CanonDemosaic`.** Today's render is
      linear sensor → sRGB gamma via Magick.NET, no auto-brightness,
      no S-curve. Daylight scenes come out ~2 stops darker than the
      embedded JPEG; astro scenes need exactly this linear behaviour.
      Add a `ToneCurve` enum so callers pick:
  - `Linear` — current behaviour. What TianWen wants (preserve photon
    counts for downstream stretch).
  - `SRgbGamma` — pure transfer, no scale. Useful for colour-managed
    pipelines that do their own brightness pass.
  - `AutoBrightSRgb` — dcraw / LibRaw default: histogram-driven scale
    to ~99th percentile + sRGB gamma. Consumer "looks right" output.
  - `CameraPictureStyle` — honour the Canon MakerNote Picture Style
    (Standard / Portrait / Landscape / Neutral / Faithful / Mono).
    Closest match to the in-camera JPEG.
- [ ] **Highlight recovery** — clipped highlights currently posterize
      on the saturated channel. dcraw's `-H 2` reconstructs from the
      unclipped channels; cheap to port.
- [ ] **Camera colour matrix** — we apply Canon's stock matrix today.
      Accept an Adobe DCP sidecar so output matches Lightroom for
      consumer workflows.

## CRX decoder coverage

- [ ] **FF13 subband markers + per-band qStep** (B.6). Empirically the
      "lossy cRAW" output from R5/R6 is `encType=0 levels=3` with every
      band using FF13 instead of FF03. The wavelet pyramid + Rice path
      is reused as-is; we add (a) FF13 parsing in `CrxMdatHeader` and
      (b) inverse-quantization (multiply each decoded coefficient by its
      band's qStep before the lift) in `CrxWaveletPlaneDecoder.Subband`.
      Fixture `Canon_EOS_R5_CRAW.CR3` committed and pinned by
      `EosR5_CrawFixture_HasExpectedShapeAndThrowsOnFf13` — flip that
      test's `Throw<NotImplementedException>` to a successful-decode
      assertion once the path lands. Bonus prerequisite: the R5 file
      uses 64-bit `largesize` mdat-box encoding (size field == 1) which
      `IsoBmffReader` currently parses as size=0; ~10 LOC fix needed
      before the decoder reaches the bands.
- [ ] **encType=3.** May genuinely not exist in current consumer CR3
      files — every R-series sample we've inspected is `encType=0`
      with FF13 quantization. Keep the throw with a clearer message
      (pointing at the FF13 path being the actual lossy variant) and
      leave encType=3 as a placeholder until a real fixture appears.
- [ ] **encType=1 (monochrome).** Throws today. Vanishingly rare;
      ship when a fixture turns up.
- [ ] **Multi-tile-column fixture.** `CrxWaveletPlaneDecoder.FilterTransform`
      HasBottom case falls through to `RegularInteriorTransform`; LibRaw
      does nothing in that branch. Needs a fixture with horizontal tile
      splits to validate (M50 + R5 fixtures both have a single tile column).
- [ ] **More camera fixtures.** EOS M50 (RAW.CR3 levels=0, CRAW.CR3
      levels=3) and EOS R5 (CRAW.CR3 levels=3 + FF13) committed today.
      Add R6 / R3 / 1DX III / 90D / etc. as we encounter them — each new
      body tends to surface one corner-case off-by-one in the
      boundary-extension counters.

## Container / metadata

- [ ] **Auxiliary tracks.** Phase A reads CTBO entries for the full-res
      RAW (track 2) but ignores track 3 (1024 px embedded preview) and
      the HDR PQ preview on newer bodies. Surface them as byte spans so
      callers can pull a fast preview without decoding the full frame.
- [ ] **Picture Style metadata.** Tags live in the existing MakerNote
      IFD already; parse into a structured record so the tone-mapping
      stage above can honour them.
- [ ] **Lens / shooting metadata round-trip.** Lens model, focal length,
      exposure, ISO are parsed but not all surfaced on `CanonRawFile`.
      Audit what the consumer use case needs vs. what astro discards.

## Performance / hygiene

- [ ] **Per-frame allocations.** `CrxWaveletPlaneDecoder` allocates a
      handful of per-row int[] buffers per (tile, plane). Hoist to
      pooled fields so future CR3M (Canon's raw video container) doesn't
      churn GC during real-time playback.
- [ ] **Optional SIMD.** The horizontal 5/3 lift and Rice quotient
      scanning are tight inner loops. Profile-driven; only worth doing
      after a real workload (batch decode, video) demands it.

## Test infrastructure

- [ ] **LibRaw oracle gate in CI.** Today the byte-exact comparison
      against `unprocessed_raw` is a manual local check (VS18 cmake +
      LibRaw build). Wire it into CI as an opt-in job so regressions
      against the reference decoder surface automatically.
- [ ] **Synthetic CRX round-trip.** Encoder + decoder round-trip on
      generated patterns (gradients, edges, noise) would catch decoder
      drift without depending on hardware fixtures. Encoder is more
      work than the decoder; defer unless we hit a hard-to-reproduce
      decode bug.
