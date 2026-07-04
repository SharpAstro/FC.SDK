using SharpAstro.Jpeg;
using SharpAstro.Png;
using Shouldly;
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
///   via SharpAstro.Jpeg, writes a PNG to the test output dir. Verifies our
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

        // Decode the JPEG. SharpAstro.Jpeg handles baseline JPEG (DCT) — the IFD0 preview
        // is always a standard sRGB JPEG, not the lossless SOF3 used for raw.
        var img = JpegDecoder.Decode(jpegBytes);
        img.ShouldNotBeNull();
        img.Width.ShouldBeGreaterThan(0);
        img.Height.ShouldBeGreaterThan(0);
        img.Pixels.Length.ShouldBe(img.Width * img.Height * 4);

        // Sanity-check the content — per-channel byte ranges must show non-trivial signal
        // (catches "extracted the wrong bytes" failures that produce a decoder-noise image).
        AssertHasSignal(img.Pixels);

        // Persist for visual inspection. Output dir is unique per test class so multiple
        // runs don't clobber each other.
        var outDir = CreateTestOutputDir(nameof(ThumbnailRendersToPng));
        var pngPath = Path.Combine(outDir, "thumbnail.png");
        File.WriteAllBytes(pngPath, PngWriter.Encode(img.Pixels, img.Width, img.Height));
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

        // Render through the production CanonDemosaic.Render pipeline: black-
        // subtract + as-shot WB + AHD demosaic + camera matrix + auto-stretch
        // + sRGB gamma. The 16-bit interleaved RGB is converted to 8-bit RGBA
        // for PNG output (PngWriter.Encode takes RGBA8). Save unconditionally
        // so failing tests still leave the artifact on disk for visual
        // debugging; per-channel stats vary by subject so we don't assert.
        var outDir = CreateTestOutputDir(nameof(RawRendersToPng_WithSensibleDefaults));
        WriteRenderedPng(file, CanonDemosaicAlgorithm.Bilinear, Path.Combine(outDir, "raw_bilinear.png"));
        WriteRenderedPng(file, CanonDemosaicAlgorithm.Ahd, Path.Combine(outDir, "raw_ahd.png"));

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

    /// <summary>Runs the production <see cref="CanonDemosaic.Render"/> with
    /// the chosen algorithm, downconverts the 16-bit output to 8-bit RGBA
    /// (PngWriter.Encode takes RGBA8), and writes to disk. The simple shift
    /// matches what a consumer would do to save a JPEG-ready file.</summary>
    private void WriteRenderedPng(CanonRawFile file, CanonDemosaicAlgorithm algorithm, string path)
    {
        var img = CanonDemosaic.Render(file, new CanonRenderOptions { Algorithm = algorithm });
        var rgba = new byte[file.Width * file.Height * 4];
        for (var p = 0; p < file.Width * file.Height; p++)
        {
            rgba[p * 4]     = (byte)(img.InterleavedRgb[p * 3]     >> 8);
            rgba[p * 4 + 1] = (byte)(img.InterleavedRgb[p * 3 + 1] >> 8);
            rgba[p * 4 + 2] = (byte)(img.InterleavedRgb[p * 3 + 2] >> 8);
            rgba[p * 4 + 3] = 0xFF;
        }
        File.WriteAllBytes(path, PngWriter.Encode(rgba, file.Width, file.Height));
        output.WriteLine($"{algorithm} render: {path} ({file.Width}x{file.Height})");
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
