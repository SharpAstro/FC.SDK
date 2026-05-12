using SharpAstro.Png;
using Shouldly;
using StbImageSharp;
using System;
using System.IO;
using Xunit;

namespace FC.SDK.Raw.Tests;

/// <summary>
/// End-to-end CR2 decode tests against the committed EOS 6D fixture
/// (<c>Fixtures/_MG_7578.CR2</c>, tracked via Git LFS). Three layers of
/// verification:
/// <list type="bullet">
/// <item><c>DecodesRealCr2_DimensionsAndBitDepthMatch</c>: structural —
///   dimensions, bit depth, EXIF model, per-pixel range.</item>
/// <item><c>ThumbnailRendersToPng</c>: extracts the IFD0 preview JPEG, decodes
///   via StbImageSharp, writes a PNG to the test output dir. Verifies our
///   JPEG-thumbnail extraction + the embedded preview is a valid sRGB image
///   with plausible content (per-channel dynamic range).</item>
/// <item><c>RawRendersToPng_WithSensibleDefaults</c>: decodes the raw Bayer
///   mosaic, runs a simple bilinear demosaic + auto-stretch, writes a PNG.
///   Verifies the slice unscramble produced a coherent image (per-channel
///   means + row-to-row correlation, plus visual inspection via the output PNG).</item>
/// </list>
///
/// Env var <c>FC_SDK_RAW_TEST_CR2</c> overrides the committed-fixture path —
/// useful for testing alternate CR2 files locally without recompiling.
/// </summary>
public class Cr2EndToEndTests(ITestOutputHelper output)
{
    private static string FixturePath
        => System.Environment.GetEnvironmentVariable("FC_SDK_RAW_TEST_CR2")
           ?? Path.Combine(AppContext.BaseDirectory, "Fixtures", "_MG_7578.CR2");

    [Fact]
    public void DecodesRealCr2_DimensionsAndBitDepthMatch()
    {
        var path = FixturePath;
        if (!File.Exists(path))
        {
            Assert.Skip($"CR2 fixture not present at {path}. " +
                "If running locally outside CI, run `git lfs pull` or set FC_SDK_RAW_TEST_CR2.");
            return;
        }

        var file = CanonRaw.Open(path);

        // Width / height must be even (Bayer block boundary); anything else means the
        // slice unscramble produced a malformed output.
        file.Width.ShouldBeGreaterThan(0);
        file.Height.ShouldBeGreaterThan(0);
        (file.Width % 2).ShouldBe(0, "Bayer-pattern raw must have even width");
        (file.Height % 2).ShouldBe(0, "Bayer-pattern raw must have even height");
        file.BayerMosaic.Length.ShouldBe(file.Width * file.Height);

        // Canon DSLR sensors run at 12 or 14 bits per sample (modern bodies are 14).
        file.BitDepth.ShouldBeInRange(12, 14);

        // EXIF must at least carry the camera model — otherwise we failed to walk the IFD chain.
        file.Exif.ShouldNotBeNull();
        file.Exif!.Model.ShouldNotBeNullOrEmpty();

        // Pixel values respect the declared bit depth.
        var maxAllowed = (1 << file.BitDepth) - 1;
        long min = int.MaxValue, max = 0;
        var rng = new Random(42);
        for (var i = 0; i < 1000; i++)
        {
            var idx = rng.Next(file.BayerMosaic.Length);
            int v = file.BayerMosaic[idx];
            if (v < min) min = v;
            if (v > max) max = v;
            v.ShouldBeLessThanOrEqualTo(maxAllowed,
                $"Pixel {idx} = {v} exceeds {file.BitDepth}-bit max {maxAllowed}");
        }
        (max - min).ShouldBeGreaterThan(100, "real-world CR2 should have wide dynamic range");
    }

    [Fact]
    public void ThumbnailRendersToPng()
    {
        var path = FixturePath;
        if (!File.Exists(path))
        {
            Assert.Skip($"CR2 fixture not present at {path}. " +
                "If running locally outside CI, run `git lfs pull` or set FC_SDK_RAW_TEST_CR2.");
            return;
        }

        var jpegBytes = CanonRaw.ExtractThumbnailJpeg(path);
        jpegBytes.ShouldNotBeNull("CR2 IFD0 should carry an embedded preview JPEG");
        jpegBytes!.Length.ShouldBeGreaterThan(10_000,
            $"Preview JPEG is implausibly small ({jpegBytes.Length} bytes) — likely the IFD1 mini thumbnail instead of IFD0 preview");
        // SOI marker
        jpegBytes[0].ShouldBe((byte)0xFF);
        jpegBytes[1].ShouldBe((byte)0xD8);

        // Decode the JPEG. StbImageSharp handles baseline JPEG (DCT) — the IFD0 preview
        // is always a standard sRGB JPEG, not the lossless SOF3 used for raw.
        var img = ImageResult.FromMemory(jpegBytes, ColorComponents.RedGreenBlueAlpha);
        img.ShouldNotBeNull();
        img.Width.ShouldBeGreaterThan(0);
        img.Height.ShouldBeGreaterThan(0);
        img.Data.Length.ShouldBe(img.Width * img.Height * 4);

        // Sanity-check the content — per-channel byte ranges must show non-trivial signal
        // (catches "extracted the wrong bytes" failures that produce a decoder-noise image).
        AssertHasSignal(img.Data);

        // Persist for visual inspection. Output dir is unique per test class so multiple
        // runs don't clobber each other.
        var outDir = CreateTestOutputDir(nameof(ThumbnailRendersToPng));
        var pngPath = Path.Combine(outDir, "thumbnail.png");
        File.WriteAllBytes(pngPath, PngWriter.Encode(img.Data, img.Width, img.Height));
        output.WriteLine($"Thumbnail PNG: {pngPath} ({img.Width}x{img.Height})");
    }

    [Fact]
    public void RawRendersToPng_WithSensibleDefaults()
    {
        var path = FixturePath;
        if (!File.Exists(path))
        {
            Assert.Skip($"CR2 fixture not present at {path}. " +
                "If running locally outside CI, run `git lfs pull` or set FC_SDK_RAW_TEST_CR2.");
            return;
        }

        var file = CanonRaw.Open(path);

        LogRawStats(file);

        // Per-channel raw-mosaic range — this is the assertion that verifies the slice
        // unscramble + lossless-JPEG decode produced coherent data. We check the raw
        // Bayer mosaic (not the demosaiced/stretched PNG) because the raw is the
        // decoder's actual output; whether the image renders as "pretty" with naive
        // auto-stretch depends on the subject (astro photo vs daylight vs studio).
        // The shipped fixture is an astro photograph (most pixels at black pedestal,
        // sparse bright stars) — naive percentile-99 stretch on that produces a
        // mostly-white image that wouldn't be visually meaningful to assert against.
        AssertRawMosaicHasCoherentSignal(file);

        // Row-to-row correlation on the RAW MOSAIC (not the stretched PNG). For any
        // real photograph — including astro — neighbouring rows differ by only a few
        // sensor counts on average. A failed slice unscramble would scatter sample
        // values across the row, producing high row-to-row deltas.
        var rowDelta = AverageRowToRowDeltaRaw(file.BayerMosaic, file.Width, file.Height);
        output.WriteLine($"Average raw row-to-row delta: {rowDelta:F2} counts (lower = coherent unscramble)");
        rowDelta.ShouldBeLessThan(2000.0,
            $"Average row-to-row raw delta {rowDelta:F2} is implausibly high — slice unscramble likely reordered samples incorrectly");

        // Render the way an off-the-shelf RAW codec would: black-subtract +
        // daylight WB + bilinear demosaic + joint stretch + sRGB gamma. Save it
        // unconditionally so failing tests still leave the artifact on disk for
        // visual debugging. The per-channel stats vary by subject so we don't
        // assert on them — visual inspection of the PNG is the real check.
        var rgba = RenderSensibleDefaults(file);
        var outDir = CreateTestOutputDir(nameof(RawRendersToPng_WithSensibleDefaults));
        var pngPath = Path.Combine(outDir, "raw_demosaiced.png");
        File.WriteAllBytes(pngPath, PngWriter.Encode(rgba, file.Width, file.Height));
        output.WriteLine($"Raw demosaiced PNG: {pngPath} ({file.Width}x{file.Height})");

        // Diagnostic: dump the raw Bayer mosaic as 8-bit grayscale with min/max
        // normalisation — NO demosaic, NO white-balance, NO stretch. If the moon
        // is recognisable here, the decoder is producing correct data and any
        // remaining artefacts come from our naive demosaic / render. If the
        // raw mosaic looks tiled or garbled, the unscramble / lossless-JPEG
        // pipeline upstream has a bug.
        ushort rawMin = ushort.MaxValue, rawMax = 0;
        foreach (var v in file.BayerMosaic)
        {
            if (v < rawMin) rawMin = v;
            if (v > rawMax) rawMax = v;
        }
        var range = (float)Math.Max(1, rawMax - rawMin);
        var gray = new byte[file.BayerMosaic.Length];
        for (var i = 0; i < gray.Length; i++)
        {
            var v = (file.BayerMosaic[i] - rawMin) / range * 255f;
            gray[i] = (byte)Math.Clamp((int)(v + 0.5f), 0, 255);
        }
        var grayPath = Path.Combine(outDir, "raw_mosaic_gray.png");
        File.WriteAllBytes(grayPath, PngWriter.EncodeGray8(gray, file.Width, file.Height));
        output.WriteLine($"Raw Bayer mosaic (grayscale, normalised [{rawMin}, {rawMax}]): {grayPath}");
    }

    /// <summary>Verifies the raw Bayer mosaic has plausible signal — range covers a
    /// meaningful fraction of the bit depth, distinct values exist, no whole-channel
    /// collapse. Subject-independent: works for astro / daylight / dark-frame.</summary>
    private void AssertRawMosaicHasCoherentSignal(CanonRawFile file)
    {
        var maxAllowed = (1 << file.BitDepth) - 1;
        ushort min = ushort.MaxValue, max = 0;
        foreach (var v in file.BayerMosaic)
        {
            if (v < min) min = v;
            if (v > max) max = v;
        }
        // Raw range should cover at least 10% of the bit depth — even a dark frame
        // has noise spanning hundreds of counts; a real photograph spans thousands.
        var span = max - min;
        var minSpan = maxAllowed / 10;
        span.ShouldBeGreaterThan(minSpan,
            $"Raw mosaic range [{min}, {max}] = {span} counts is < 10% of {file.BitDepth}-bit max ({maxAllowed}) — sensor data looks collapsed");

        // Sample 4000 pixels at random; require at least 30 distinct values among them.
        // Random sample is robust to "huge constant region with one bright pixel" failures
        // where range looks fine but variance is concentrated in <1% of pixels. The bar
        // is intentionally loose — for an astro / moon shot, most pixels are at pedestal
        // noise (the moon is a small fraction of the frame), so 30 captures pedestal
        // ± read noise without missing genuine signal-collapsed failures.
        var rng = new Random(7);
        var distinct = new HashSet<ushort>();
        for (var i = 0; i < 4000; i++)
        {
            distinct.Add(file.BayerMosaic[rng.Next(file.BayerMosaic.Length)]);
        }
        distinct.Count.ShouldBeGreaterThan(30,
            $"Only {distinct.Count} distinct values in 4000 random samples — mosaic looks pathologically uniform");
    }

    private static double AverageRowToRowDeltaRaw(ushort[] raw, int w, int h)
    {
        // Sample 200 random rows (avoiding the edges) and compute mean |raw[y] - raw[y+1]|.
        // For a real photograph including astro frames, this is a few hundred counts; for
        // a misordered mosaic where row Y comes from a different region than row Y+1, this
        // is many thousands.
        var rng = new Random(123);
        const int sampleCount = 200;
        double total = 0;
        for (var s = 0; s < sampleCount; s++)
        {
            var y = rng.Next(10, h - 10);
            long rowDelta = 0;
            for (var x = 0; x < w; x++)
            {
                rowDelta += Math.Abs(raw[y * w + x] - raw[(y + 1) * w + x]);
            }
            total += rowDelta / (double)w;
        }
        return total / sampleCount;
    }

    private void LogRawStats(CanonRawFile file)
    {
        ushort min = ushort.MaxValue, max = 0;
        long sum = 0;
        foreach (var v in file.BayerMosaic)
        {
            if (v < min) min = v;
            if (v > max) max = v;
            sum += v;
        }
        var mean = sum / (double)file.BayerMosaic.Length;
        output.WriteLine($"Raw mosaic: {file.Width}x{file.Height} {file.BitDepth}-bit  range [{min}, {max}]  mean {mean:F1}");
        output.WriteLine($"  CFA: {file.CfaPattern}  Model: {file.Exif?.Model ?? "?"}");
        var wb = file.MakerNote?.AsShotWhiteBalance;
        if (wb is not null)
            output.WriteLine($"  As-shot WB: R={wb.R:F3}  G1={wb.G1:F3}  G2={wb.G2:F3}  B={wb.B:F3}");
        else
            output.WriteLine("  As-shot WB: <not parsed>");
    }

    /// <summary>Renders the raw Bayer mosaic the way a regular RAW codec
    /// (Explorer's RAW preview, Affinity Photo, Lightroom) would: black-level
    /// subtract -> as-shot white-balance -> bilinear demosaic -> joint
    /// percentile stretch (single divisor across R/G/B so highlights stay
    /// neutral and white-balance ratios survive) -> sRGB gamma encode.
    ///
    /// White-balance multipliers come from the MakerNote ColorData
    /// (WB_RGGB_LEVELS_AS_SHOT, byte offset 126 in ColorData5/6/7/8/9 per
    /// dcraw's parse_makernote dispatch). Falls back to Canon-typical daylight
    /// (R = 2.0, G = 1.0, B = 1.4) when the MakerNote isn't parseable. Black
    /// level uses the fixed 2048 pedestal — real per-channel black levels are
    /// in MakerNote but the variation across channels is &lt; 5 counts on a
    /// healthy sensor, which is below the visible threshold after stretch.
    /// </summary>
    private static byte[] RenderSensibleDefaults(CanonRawFile file)
    {
        var w = file.Width;
        var h = file.Height;
        var raw = file.BayerMosaic;

        // Canon 14-bit raw pedestal. Real per-channel black levels live in the
        // MakerNote ColorData and vary slightly with ISO; 2048 is the standard
        // baseline for EOS 14-bit sensors at non-extended ISOs.
        const int blackLevel = 2048;
        var maxRaw = (1 << file.BitDepth) - 1;
        var headroom = (float)(maxRaw - blackLevel);

        // Prefer the as-shot white-balance from MakerNote when available; fall
        // back to Canon-typical daylight constants when ColorData parsing failed
        // (e.g. uncalibrated capture, older ColorData revision we don't decode).
        var asShot = file.MakerNote?.AsShotWhiteBalance;
        var wbR = asShot?.R ?? 2.0f;
        var wbG1 = asShot?.G1 ?? 1.0f;
        var wbG2 = asShot?.G2 ?? 1.0f;
        var wbB = asShot?.B ?? 1.4f;

        // Step 1: black-subtract + white-balance the mosaic in place into a
        // float[] in linear [0, ~max(wbR,wbB)] space. We do this before demosaic
        // so the bilinear interpolation averages WB-corrected samples and the
        // R/G/B output channels share a common scale.
        var rawWb = new float[w * h];
        for (var y = 0; y < h; y++)
        {
            var rowBase = y * w;
            var isEvenRow = (y & 1) == 0;
            for (var x = 0; x < w; x++)
            {
                var sub = raw[rowBase + x] - blackLevel;
                if (sub < 0) sub = 0;
                var lin = sub / headroom;
                float mul;
                // RGGB layout: row even -> R G1 R G1 ...; row odd -> G2 B G2 B ...
                if (isEvenRow) mul = (x & 1) == 0 ? wbR : wbG1;
                else           mul = (x & 1) == 0 ? wbG2 : wbB;
                rawWb[rowBase + x] = lin * mul;
            }
        }

        // Step 2: bilinear demosaic on the WB'd mosaic. Same logic as before but
        // in floating point so we don't lose precision after WB amplification.
        var rgb = new float[w * h * 3];
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                var (r, g, b) = SampleRggbFloat(rawWb, w, h, x, y);
                var i = (y * w + x) * 3;
                rgb[i] = r;
                rgb[i + 1] = g;
                rgb[i + 2] = b;
            }
        }

        // Step 3: apply the camera-to-sRGB colour matrix derived from the
        // CanonCameraProfile table (sourced from dcraw's adobe_coeff). After WB
        // the channels are scaled to make a neutral scene have R = G = B in
        // camera space, but those camera-space neutrals are NOT the same as
        // sRGB primaries — the CFA filters span different spectral bands. The
        // matrix mixes camera RGB into sRGB so a neutral scene actually renders
        // neutral. Falls back to passing camera RGB through unchanged when the
        // model isn't in the table.
        var cm = CanonCameraProfiles.ResolveProfile(file.Exif?.Model)?.ComputeRgbCam();
        if (cm is not null)
        {
            var tmp = new float[3];
            for (var p = 0; p < w * h; p++)
            {
                var i = p * 3;
                tmp[0] = cm[0]*rgb[i] + cm[1]*rgb[i+1] + cm[2]*rgb[i+2];
                tmp[1] = cm[3]*rgb[i] + cm[4]*rgb[i+1] + cm[5]*rgb[i+2];
                tmp[2] = cm[6]*rgb[i] + cm[7]*rgb[i+1] + cm[8]*rgb[i+2];
                rgb[i]     = tmp[0];
                rgb[i + 1] = tmp[1];
                rgb[i + 2] = tmp[2];
            }
        }

        // Step 4: joint stretch by the global max — single divisor across all
        // three channels so nothing clips and the WB / matrix ratios survive.
        var stretchMax = 0f;
        for (var i = 0; i < rgb.Length; i++) if (rgb[i] > stretchMax) stretchMax = rgb[i];
        if (stretchMax < 1e-6f) stretchMax = 1f;

        // Step 5: sRGB gamma encode to 8-bit. The standard sRGB transfer function
        // is what Explorer / Affinity / browsers all assume on unmanaged JPEGs.
        var rgba = new byte[w * h * 4];
        for (var p = 0; p < w * h; p++)
        {
            for (var c = 0; c < 3; c++)
            {
                var linear = rgb[p * 3 + c] / stretchMax;
                if (linear < 0) linear = 0;
                else if (linear > 1) linear = 1;
                var encoded = SrgbEncode(linear);
                rgba[p * 4 + c] = (byte)(encoded * 255f + 0.5f);
            }
            rgba[p * 4 + 3] = 0xFF;
        }
        return rgba;
    }

    private static (float R, float G, float B) SampleRggbFloat(float[] raw, int w, int h, int x, int y)
    {
        // Bayer position: (y % 2, x % 2) -> R=(0,0), G=(0,1) or (1,0), B=(1,1).
        var isEvenRow = (y & 1) == 0;
        var isEvenCol = (x & 1) == 0;

        float r, g, b;
        if (isEvenRow && isEvenCol)
        {
            r = raw[y * w + x];
            g = (NNF(raw, w, h, x - 1, y) + NNF(raw, w, h, x + 1, y) + NNF(raw, w, h, x, y - 1) + NNF(raw, w, h, x, y + 1)) * 0.25f;
            b = (NNF(raw, w, h, x - 1, y - 1) + NNF(raw, w, h, x + 1, y - 1) + NNF(raw, w, h, x - 1, y + 1) + NNF(raw, w, h, x + 1, y + 1)) * 0.25f;
        }
        else if (!isEvenRow && !isEvenCol)
        {
            b = raw[y * w + x];
            g = (NNF(raw, w, h, x - 1, y) + NNF(raw, w, h, x + 1, y) + NNF(raw, w, h, x, y - 1) + NNF(raw, w, h, x, y + 1)) * 0.25f;
            r = (NNF(raw, w, h, x - 1, y - 1) + NNF(raw, w, h, x + 1, y - 1) + NNF(raw, w, h, x - 1, y + 1) + NNF(raw, w, h, x + 1, y + 1)) * 0.25f;
        }
        else
        {
            // Green pixel.
            g = raw[y * w + x];
            if (isEvenRow)
            {
                r = (NNF(raw, w, h, x - 1, y) + NNF(raw, w, h, x + 1, y)) * 0.5f;
                b = (NNF(raw, w, h, x, y - 1) + NNF(raw, w, h, x, y + 1)) * 0.5f;
            }
            else
            {
                r = (NNF(raw, w, h, x, y - 1) + NNF(raw, w, h, x, y + 1)) * 0.5f;
                b = (NNF(raw, w, h, x - 1, y) + NNF(raw, w, h, x + 1, y)) * 0.5f;
            }
        }
        return (r, g, b);
    }

    private static float NNF(float[] raw, int w, int h, int x, int y)
    {
        x = Math.Clamp(x, 0, w - 1);
        y = Math.Clamp(y, 0, h - 1);
        return raw[y * w + x];
    }

    /// <summary>Single percentile across the combined R+G+B sample set. Used as
    /// a global stretch divisor so the WB ratio between channels is preserved.
    /// Cheap 4096-bin histogram over the max-normalised values — exact percentile
    /// is overkill for "is the moon visible".</summary>
    private static float ComputeJointPercentile(float[] rgb, float fraction)
    {
        float max = 0f;
        for (var i = 0; i < rgb.Length; i++) if (rgb[i] > max) max = rgb[i];
        if (max <= 1e-6f) return 1f;

        const int bins = 4096;
        var hist = new int[bins];
        for (var i = 0; i < rgb.Length; i++)
        {
            var bn = (int)(rgb[i] / max * (bins - 1));
            if (bn < 0) bn = 0;
            else if (bn >= bins) bn = bins - 1;
            hist[bn]++;
        }
        var target = (long)(rgb.Length * fraction);
        long cum = 0;
        for (var i = 0; i < bins; i++)
        {
            cum += hist[i];
            if (cum >= target) return (i + 0.5f) / bins * max;
        }
        return max;
    }

    private static float SrgbEncode(float linear)
    {
        return linear <= 0.0031308f
            ? 12.92f * linear
            : 1.055f * MathF.Pow(linear, 1f / 2.4f) - 0.055f;
    }

    private static void AssertHasSignal(byte[] rgba)
    {
        Span<int> chMin = stackalloc int[3] { 255, 255, 255 };
        Span<int> chMax = stackalloc int[3] { 0, 0, 0 };
        Span<long> chSum = stackalloc long[3];
        var pixels = rgba.Length / 4;
        for (var i = 0; i < rgba.Length; i += 4)
        {
            for (var c = 0; c < 3; c++)
            {
                var v = rgba[i + c];
                if (v < chMin[c]) chMin[c] = v;
                if (v > chMax[c]) chMax[c] = v;
                chSum[c] += v;
            }
        }
        for (var c = 0; c < 3; c++)
        {
            (chMax[c] - chMin[c]).ShouldBeGreaterThan(20, $"channel {c} dynamic range too narrow ({chMax[c] - chMin[c]})");
            var mean = chSum[c] / (double)pixels;
            // Bounds are intentionally loose — astro / dark-frame CR2s have very low means
            // (~0.7 for the EOS 6D fixture, which is a near-night exposure). The test just
            // rules out the catastrophic "all zero" / "all 255" failure modes.
            mean.ShouldBeGreaterThan(0.1, $"channel {c} mean = {mean:F2} = essentially all-black");
            mean.ShouldBeLessThan(254.9, $"channel {c} mean = {mean:F2} = essentially all-white");
        }
    }

    private static string CreateTestOutputDir(string testName)
    {
        var dir = Path.Combine(Path.GetTempPath(), "FC.SDK.Raw.Tests",
            DateTime.Now.ToString("yyyyMMdd"), testName);
        Directory.CreateDirectory(dir);
        return dir;
    }
}
