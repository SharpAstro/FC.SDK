using Shouldly;
using System;
using Xunit;

namespace FC.SDK.Raw.Tests;

/// <summary>
/// Unit tests for the Bilinear and AHD demosaic implementations and the full
/// <see cref="CanonDemosaic.Render"/> pipeline. Tests use synthetic mosaics
/// with known per-CFA-position values so we can assert reconstruction
/// correctness without depending on the LFS-tracked CR2 fixture; an
/// end-to-end smoke test against the real CR2 lives in
/// <c>Cr2EndToEndTests.RawRendersToPng_WithSensibleDefaults</c>.
/// </summary>
public class CanonDemosaicTests
{
    [Theory]
    [InlineData(CanonDemosaicAlgorithm.Bilinear)]
    [InlineData(CanonDemosaicAlgorithm.Ahd)]
    public void FlatField_AllChannelsMatchInput(CanonDemosaicAlgorithm algorithm)
    {
        // A uniform mosaic must demosaic to a uniform image — every algorithm
        // averages same-channel neighbours, so equal inputs give equal outputs.
        // Edge pixels can differ slightly under clamp-addressing; we sample
        // the interior where every neighbour exists.
        const int w = 32, h = 32;
        var mosaic = new float[w * h];
        Array.Fill(mosaic, 0.42f);

        var rgb = algorithm switch
        {
            CanonDemosaicAlgorithm.Bilinear => InvokeRunBilinear(mosaic, w, h, CanonCfaPattern.Rggb),
            CanonDemosaicAlgorithm.Ahd => InvokeRunAhd(mosaic, w, h, CanonCfaPattern.Rggb),
            _ => throw new ArgumentOutOfRangeException(nameof(algorithm)),
        };

        // AHD's homogeneity window pushes the bilinear-filled border 4 pixels
        // in; bilinear-only is uniform from x = 1 onward.
        var margin = algorithm == CanonDemosaicAlgorithm.Ahd ? 4 : 1;
        AssertInteriorUniform(rgb, w, h, expected: 0.42f, tolerance: 1e-4f, margin);
    }

    [Fact]
    public void Bilinear_AtRPosition_OutputRMatchesInput_Rggb()
    {
        // Build an RGGB mosaic: R = 1.0, G = 0.5, B = 0.25 at each respective
        // CFA site. After bilinear, the output at an R site must have
        // output[R] == input (the pixel itself), output[G] == 0.5 (avg of 4
        // G neighbours, all equal), output[B] == 0.25 (avg of 4 B diagonals).
        const int w = 16, h = 16;
        var mosaic = BuildRggbMosaic(w, h, r: 1.0f, g: 0.5f, b: 0.25f);

        var rgb = InvokeRunBilinear(mosaic, w, h, CanonCfaPattern.Rggb);

        // Pick an interior R site at (4, 4) — RGGB has R at (even, even).
        var oi = (4 * w + 4) * 3;
        rgb[oi].ShouldBe(1.0f, tolerance: 1e-4f);     // R from input
        rgb[oi + 1].ShouldBe(0.5f, tolerance: 1e-4f); // G from cardinal average
        rgb[oi + 2].ShouldBe(0.25f, tolerance: 1e-4f);// B from diagonal average

        // Pick an interior B site at (5, 5) — RGGB has B at (odd, odd).
        oi = (5 * w + 5) * 3;
        rgb[oi].ShouldBe(1.0f, tolerance: 1e-4f);     // R from diagonal average
        rgb[oi + 1].ShouldBe(0.5f, tolerance: 1e-4f); // G from cardinal average
        rgb[oi + 2].ShouldBe(0.25f, tolerance: 1e-4f);// B from input

        // Pick an interior G1 site at (4, 5) — RGGB has G1 at (even, odd).
        oi = (4 * w + 5) * 3;
        rgb[oi].ShouldBe(1.0f, tolerance: 1e-4f);     // R from horizontal pair
        rgb[oi + 1].ShouldBe(0.5f, tolerance: 1e-4f); // G from input
        rgb[oi + 2].ShouldBe(0.25f, tolerance: 1e-4f);// B from vertical pair
    }

    [Theory]
    [InlineData(CanonCfaPattern.Rggb, 1.0f, 0.25f)]
    [InlineData(CanonCfaPattern.Bggr, 0.25f, 1.0f)]
    public void Bilinear_RespectsCfaPattern(CanonCfaPattern pattern, float expectedRAtZero, float expectedBAtZero)
    {
        // Same mosaic, different CFA pattern declaration: the R / B channels
        // must swap meaning. (0, 0) is R in RGGB and B in BGGR — so a value
        // of 1.0 there reads as R=1 / B=0.25 under RGGB and R=0.25 / B=1
        // under BGGR.
        const int w = 16, h = 16;
        var mosaic = BuildRggbMosaic(w, h, r: 1.0f, g: 0.5f, b: 0.25f);

        var rgb = InvokeRunBilinear(mosaic, w, h, pattern);

        var oi = (4 * w + 4) * 3;
        rgb[oi].ShouldBe(expectedRAtZero, tolerance: 1e-4f);
        rgb[oi + 1].ShouldBe(0.5f, tolerance: 1e-4f);
        rgb[oi + 2].ShouldBe(expectedBAtZero, tolerance: 1e-4f);
    }

    [Theory]
    [InlineData(CanonDemosaicAlgorithm.Bilinear)]
    [InlineData(CanonDemosaicAlgorithm.Ahd)]
    public void Render_FlatFieldRaw_ProducesUniformOutput(CanonDemosaicAlgorithm algorithm)
    {
        // End-to-end pipeline against a flat-field raw: black-subtract +
        // WB + demosaic + matrix + auto-stretch + gamma. Output must be
        // uniform in the interior (every pixel sees the same neighbours).
        // Edge pixels can diverge slightly under bilinear clamp + AHD bilinear
        // edge fill; we sample inside a comfortable margin.
        const int w = 64, h = 64;
        const int bitDepth = 14;
        const ushort raw = 8192; // mid-range
        var mosaic = new ushort[w * h];
        Array.Fill(mosaic, raw);

        var file = new CanonRawFile(
            Width: w, Height: h, BayerMosaic: mosaic, BitDepth: bitDepth,
            CfaPattern: CanonCfaPattern.Rggb, Exif: null, MakerNote: null);

        var img = CanonDemosaic.Render(file, new CanonRenderOptions
        {
            Algorithm = algorithm,
            // Disable colour matrix — without an EXIF model the profile lookup
            // returns null anyway, so the matrix step would no-op. Being
            // explicit makes the test intent clear.
            ApplyColorMatrix = false,
        });

        img.Width.ShouldBe(w);
        img.Height.ShouldBe(h);
        img.InterleavedRgb.Length.ShouldBe(w * h * 3);

        // Sample the interior (8-pixel margin) and require every pixel's
        // channel to match the centre's same-channel sample within +/- 1 LSB.
        // We compare per-channel because daylight WB (R = 2.0, G = 1.0,
        // B = 1.4) leaves R / G / B at different post-stretch ushort values
        // even on a uniform raw — the test asserts spatial uniformity, not
        // colour-channel equality.
        var sx = w / 2;
        var sy = h / 2;
        var sampleI = (sy * w + sx) * 3;
        var sampleR = img.InterleavedRgb[sampleI];
        var sampleG = img.InterleavedRgb[sampleI + 1];
        var sampleB = img.InterleavedRgb[sampleI + 2];
        for (var y = 8; y < h - 8; y++)
        {
            for (var x = 8; x < w - 8; x++)
            {
                var i = (y * w + x) * 3;
                Math.Abs(img.InterleavedRgb[i]     - sampleR).ShouldBeLessThanOrEqualTo(1, $"R at ({x}, {y})");
                Math.Abs(img.InterleavedRgb[i + 1] - sampleG).ShouldBeLessThanOrEqualTo(1, $"G at ({x}, {y})");
                Math.Abs(img.InterleavedRgb[i + 2] - sampleB).ShouldBeLessThanOrEqualTo(1, $"B at ({x}, {y})");
            }
        }
    }

    [Fact]
    public void Render_WithoutAutoStretch_ProducesLowerOutput()
    {
        // With auto-stretch disabled the WB-amplified output isn't normalised
        // up to full scale, so a mid-range raw should produce a smaller
        // ushort value than the auto-stretched version. Sanity check that
        // the flag actually does something.
        const int w = 16, h = 16, bitDepth = 14;
        var mosaic = new ushort[w * h];
        Array.Fill(mosaic, (ushort)4096);

        var file = new CanonRawFile(
            Width: w, Height: h, BayerMosaic: mosaic, BitDepth: bitDepth,
            CfaPattern: CanonCfaPattern.Rggb, Exif: null, MakerNote: null);

        var withStretch = CanonDemosaic.Render(file, new CanonRenderOptions
        {
            Algorithm = CanonDemosaicAlgorithm.Bilinear,
            ApplyColorMatrix = false,
            AutoStretch = true,
        });
        var withoutStretch = CanonDemosaic.Render(file, new CanonRenderOptions
        {
            Algorithm = CanonDemosaicAlgorithm.Bilinear,
            ApplyColorMatrix = false,
            AutoStretch = false,
        });

        // Auto-stretch divides by the brightest channel, so its output uses
        // the full ushort range; without auto-stretch the same scene caps
        // out at WB-amplified linear values which are well below ushort.Max
        // for a non-saturated raw.
        var greenStretched = withStretch.InterleavedRgb[(h / 2 * w + w / 2) * 3 + 1];
        var greenLinear = withoutStretch.InterleavedRgb[(h / 2 * w + w / 2) * 3 + 1];
        greenStretched.ShouldBeGreaterThan(greenLinear);
    }

    [Fact]
    public void PreprocessMosaic_FusesBlackSubAndWhiteBalance()
    {
        // PreprocessMosaic should produce the same float[] that CanonDemosaic.Render
        // uses internally. Build a mosaic where every R-site = 4096 (= 2048 black
        // + 2048 signal), every G-site = 4096, every B-site = 4096. After
        // black-subtract (sub = 2048) and headroom-normalisation (max = 14-bit -
        // 2048 = 14335), the linear value is 2048 / 14335 = 0.1429. Then WB
        // multiplies per channel.
        const int w = 8, h = 8, bitDepth = 14;
        var mosaic = new ushort[w * h];
        Array.Fill(mosaic, (ushort)4096);
        var file = new CanonRawFile(w, h, mosaic, bitDepth, CanonCfaPattern.Rggb, Exif: null, MakerNote: null);

        // Use explicit WB so the daylight fallback doesn't interfere with the
        // hand-computed expectation.
        var wb = new CanonWhiteBalance(R: 2.0f, G1: 1.0f, G2: 1.0f, B: 1.4f);
        var pre = CanonRaw.PreprocessMosaic(file, blackLevel: 2048, whiteBalance: wb);

        pre.Length.ShouldBe(w * h);

        // RGGB: (0,0) R -> wb.R = 2.0; (0,1) G1 -> 1.0; (1,0) G2 -> 1.0; (1,1) B -> 1.4.
        var expectedLinear = 2048f / 14335f;
        pre[0 * w + 0].ShouldBe(expectedLinear * 2.0f, tolerance: 1e-5f); // R
        pre[0 * w + 1].ShouldBe(expectedLinear * 1.0f, tolerance: 1e-5f); // G1
        pre[1 * w + 0].ShouldBe(expectedLinear * 1.0f, tolerance: 1e-5f); // G2
        pre[1 * w + 1].ShouldBe(expectedLinear * 1.4f, tolerance: 1e-5f); // B
    }

    [Fact]
    public void PreprocessMosaic_ClampsNegativeSignalToZero()
    {
        // A raw value below the black level should clamp to 0, not produce
        // a negative result that would then get amplified by WB.
        const int w = 4, h = 4, bitDepth = 14;
        var mosaic = new ushort[w * h];
        Array.Fill(mosaic, (ushort)1000); // below the 2048 default black level
        var file = new CanonRawFile(w, h, mosaic, bitDepth, CanonCfaPattern.Rggb, Exif: null, MakerNote: null);

        var pre = CanonRaw.PreprocessMosaic(file, blackLevel: 2048);
        foreach (var v in pre) v.ShouldBe(0f);
    }

    /// <summary>Build a flat-field RGGB mosaic with distinct values for each
    /// CFA position. RGGB layout: row even -> R G1, row odd -> G2 B.</summary>
    private static float[] BuildRggbMosaic(int w, int h, float r, float g, float b)
    {
        var mosaic = new float[w * h];
        for (var y = 0; y < h; y++)
        {
            var rowEven = (y & 1) == 0;
            for (var x = 0; x < w; x++)
            {
                var colEven = (x & 1) == 0;
                mosaic[y * w + x] = rowEven
                    ? (colEven ? r : g)
                    : (colEven ? g : b);
            }
        }
        return mosaic;
    }

    /// <summary>Assert every pixel within <paramref name="margin"/> of the
    /// frame edge has all three channels equal to <paramref name="expected"/>.</summary>
    private static void AssertInteriorUniform(float[] rgb, int w, int h, float expected, float tolerance, int margin)
    {
        for (var y = margin; y < h - margin; y++)
        {
            for (var x = margin; x < w - margin; x++)
            {
                var oi = (y * w + x) * 3;
                rgb[oi].ShouldBe(expected, tolerance, $"R at ({x}, {y})");
                rgb[oi + 1].ShouldBe(expected, tolerance, $"G at ({x}, {y})");
                rgb[oi + 2].ShouldBe(expected, tolerance, $"B at ({x}, {y})");
            }
        }
    }

    // InternalsVisibleTo gives us direct access to the algorithm internals so
    // tests can validate per-channel reconstruction without going through the
    // black-subtract / WB / matrix / gamma pipeline. Both production callers
    // (Render) and tests share the same code path.
    private static float[] InvokeRunBilinear(float[] m, int w, int h, CanonCfaPattern p) => CanonDemosaic.RunBilinear(m, w, h, p);
    private static float[] InvokeRunAhd(float[] m, int w, int h, CanonCfaPattern p) => CanonDemosaic.RunAhd(m, w, h, p);
}
