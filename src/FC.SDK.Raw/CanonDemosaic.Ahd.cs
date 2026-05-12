using System;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;

namespace FC.SDK.Raw;

public static partial class CanonDemosaic
{
    /// <summary>Adaptive Homogeneity-Directed demosaic (Hirakawa &amp; Parks
    /// 2005, dcraw's <c>ahd_interpolate</c>). Four phases:
    ///
    /// <list type="number">
    /// <item><b>Directional green</b>: at every non-G pixel, interpolate G
    /// horizontally and vertically with Laplacian correction. At G pixels
    /// both directions take the raw value. R / B at G pixels and the
    /// opposite colour at R / B pixels come from same-direction colour
    /// differences against the interpolated G.</item>
    /// <item><b>Homogeneity selection</b>: compute the L,a,b-like (luma + two
    /// chroma differences) distance from each pixel to its 5x5 neighbours
    /// for the H and V candidates. Count which direction is more homogeneous
    /// and pick the winner; ties average. This is what gives AHD its
    /// edge-preserving character vs naive bilinear.</item>
    /// <item><b>Edge fill</b>: the interior loop skips a 4-pixel border (2
    /// for the green Laplacian + 2 for the homogeneity window). Fill those
    /// border pixels via simple bilinear so the output covers the full
    /// frame without zero-padding artefacts.</item>
    /// <item><b>Median artefact reduction</b>: 3x3 median on R - G and B - G
    /// colour differences. Smooths the per-pixel direction switching that
    /// produces occasional colour speckles.</item>
    /// </list>
    ///
    /// Float internal, length <c>3 * w * h</c> interleaved RGB output.
    /// </summary>
    internal static float[] RunAhd(float[] mosaic, int w, int h, CanonCfaPattern pattern)
    {
        const int InterpRadius = 2;        // Laplacian-corrected green needs +/-2
        const int HomogeneityRadius = 2;   // 5x5 neighbourhood for the homogeneity vote
        const int TotalRadius = InterpRadius + HomogeneityRadius;

        var (p00, p01, p10, p11) = PatternColors(pattern);

        // Two candidate RGB buffers: one for the horizontal interpolation,
        // one for the vertical. Phase 3 picks per-pixel between them.
        var rgbH = new float[w * h * 3];
        var rgbV = new float[w * h * 3];

        // Phase 1+2: build the two candidate full-colour images. Both phases
        // can run in parallel across rows since each row's output depends only
        // on +/-2 rows of input (which are read-only here).
        Parallel.For(InterpRadius, h - InterpRadius, y =>
        {
            byte patternEven = (y & 1) == 0 ? p00 : p10;
            byte patternOdd  = (y & 1) == 0 ? p01 : p11;

            for (var x = InterpRadius; x < w - InterpRadius; x++)
            {
                byte knownColor = (x & 1) == 0 ? patternEven : patternOdd;
                float center = mosaic[y * w + x];
                int oi = (y * w + x) * 3;

                if (knownColor == 1) // G site
                {
                    // G is known for both candidates.
                    rgbH[oi + 1] = center;
                    rgbV[oi + 1] = center;

                    // Determine which colour is on the H neighbours vs V.
                    // Same-row neighbour = neighbour at (y, x+/-1), opposite
                    // parity from this G, so it's the "other CFA colour" in
                    // this row pair.
                    byte neighborColor = (x & 1) == 0 ? patternOdd : patternEven;
                    bool rOnH = neighborColor == 0;

                    float hAvg = (mosaic[y * w + (x - 1)] + mosaic[y * w + (x + 1)]) * 0.5f;
                    float vAvg = (mosaic[(y - 1) * w + x] + mosaic[(y + 1) * w + x]) * 0.5f;

                    rgbH[oi]     = rOnH ? hAvg : vAvg;
                    rgbH[oi + 2] = rOnH ? vAvg : hAvg;
                    rgbV[oi]     = rOnH ? hAvg : vAvg;
                    rgbV[oi + 2] = rOnH ? vAvg : hAvg;
                }
                else // R site (0) or B site (2)
                {
                    // Laplacian-corrected green interpolation in each direction.
                    // The +1/4 * (2*center - twoStep) term sharpens the green
                    // estimate by subtracting the second derivative of the
                    // same-colour samples; this is the key dcraw trick that
                    // lets AHD beat plain bilinear at hard edges.
                    float gW = mosaic[y * w + (x - 1)];
                    float gE = mosaic[y * w + (x + 1)];
                    float greenH = (gW + gE) * 0.5f
                        + (2f * center - mosaic[y * w + (x - 2)] - mosaic[y * w + (x + 2)]) * 0.25f;

                    float gN = mosaic[(y - 1) * w + x];
                    float gS = mosaic[(y + 1) * w + x];
                    float greenV = (gN + gS) * 0.5f
                        + (2f * center - mosaic[(y - 2) * w + x] - mosaic[(y + 2) * w + x]) * 0.25f;

                    rgbH[oi + 1] = greenH;
                    rgbV[oi + 1] = greenV;
                    rgbH[oi + knownColor] = center;
                    rgbV[oi + knownColor] = center;

                    // Diagonal neighbours hold the opposite colour. Estimate
                    // their green via their own cardinal G neighbours, then
                    // colour-difference average back to this pixel.
                    float dNW = mosaic[(y - 1) * w + (x - 1)];
                    float dNE = mosaic[(y - 1) * w + (x + 1)];
                    float dSW = mosaic[(y + 1) * w + (x - 1)];
                    float dSE = mosaic[(y + 1) * w + (x + 1)];

                    float cdNW = dNW - (gN + gW) * 0.5f;
                    float cdNE = dNE - (gN + gE) * 0.5f;
                    float cdSW = dSW - (gS + gW) * 0.5f;
                    float cdSE = dSE - (gS + gE) * 0.5f;
                    float cdAvg = (cdNW + cdNE + cdSW + cdSE) * 0.25f;

                    int oppositeChannel = knownColor == 0 ? 2 : 0;
                    rgbH[oi + oppositeChannel] = greenH + cdAvg;
                    rgbV[oi + oppositeChannel] = greenV + cdAvg;
                }
            }
        });

        // Phase 3: per-pixel homogeneity vote between the H and V candidates.
        // The metric is the sum of |dL| + |da| + |db| against each 5x5
        // neighbour, where L = luma and a/b are the (R-G) / (B-G) chroma
        // differences. We don't need a calibrated CIE Lab here — what matters
        // is that the metric ranks "smooth in this direction" higher.
        var output = new float[w * h * 3];
        Parallel.For(TotalRadius, h - TotalRadius, y =>
        {
            for (var x = TotalRadius; x < w - TotalRadius; x++)
            {
                int oi = (y * w + x) * 3;
                float lH = Luma(rgbH[oi], rgbH[oi + 1], rgbH[oi + 2]);
                float aH = rgbH[oi]     - rgbH[oi + 1];
                float bH = rgbH[oi + 2] - rgbH[oi + 1];
                float lV = Luma(rgbV[oi], rgbV[oi + 1], rgbV[oi + 2]);
                float aV = rgbV[oi]     - rgbV[oi + 1];
                float bV = rgbV[oi + 2] - rgbV[oi + 1];

                int homH = 0, homV = 0;
                for (int dy = -HomogeneityRadius; dy <= HomogeneityRadius; dy++)
                {
                    for (int dx = -HomogeneityRadius; dx <= HomogeneityRadius; dx++)
                    {
                        if (dx == 0 && dy == 0) continue;
                        int ni = ((y + dy) * w + (x + dx)) * 3;

                        float nlH = Luma(rgbH[ni], rgbH[ni + 1], rgbH[ni + 2]);
                        float naH = rgbH[ni]     - rgbH[ni + 1];
                        float nbH = rgbH[ni + 2] - rgbH[ni + 1];
                        float diffH = MathF.Abs(lH - nlH) + MathF.Abs(aH - naH) + MathF.Abs(bH - nbH);

                        float nlV = Luma(rgbV[ni], rgbV[ni + 1], rgbV[ni + 2]);
                        float naV = rgbV[ni]     - rgbV[ni + 1];
                        float nbV = rgbV[ni + 2] - rgbV[ni + 1];
                        float diffV = MathF.Abs(lV - nlV) + MathF.Abs(aV - naV) + MathF.Abs(bV - nbV);

                        if (diffH < diffV) homH++;
                        else if (diffV < diffH) homV++;
                    }
                }

                if (homH > homV)
                {
                    output[oi]     = rgbH[oi];
                    output[oi + 1] = rgbH[oi + 1];
                    output[oi + 2] = rgbH[oi + 2];
                }
                else if (homV > homH)
                {
                    output[oi]     = rgbV[oi];
                    output[oi + 1] = rgbV[oi + 1];
                    output[oi + 2] = rgbV[oi + 2];
                }
                else
                {
                    output[oi]     = (rgbH[oi]     + rgbV[oi])     * 0.5f;
                    output[oi + 1] = (rgbH[oi + 1] + rgbV[oi + 1]) * 0.5f;
                    output[oi + 2] = (rgbH[oi + 2] + rgbV[oi + 2]) * 0.5f;
                }
            }
        });

        // Phase 3b: fill the TotalRadius-wide border via bilinear so the AHD
        // output covers the full frame. We just run the bilinear demosaic on
        // the border pixels — cheap, and the visual difference from AHD on
        // the outermost 4 pixels is invisible.
        FillBorderBilinear(mosaic, output, w, h, TotalRadius, pattern);

        // Phase 4: 3x3 median on (R - G) and (B - G) colour differences.
        // Smooths the per-pixel H/V switching that occasionally produces a
        // single-pixel colour speckle at high-contrast edges. We can reuse
        // rgbH as scratch — it's dead from here on.
        var filtered = rgbH;
        Parallel.For(0, h, y =>
        {
            Span<float> medianBuf = stackalloc float[9];
            for (var x = 0; x < w; x++)
            {
                int oi = (y * w + x) * 3;
                float gCenter = output[oi + 1];
                filtered[oi + 1] = gCenter;

                if (y >= 1 && y < h - 1 && x >= 1 && x < w - 1)
                {
                    for (int c = 0; c < 3; c += 2) // 0 (R) and 2 (B) only
                    {
                        int idx = 0;
                        for (int dy = -1; dy <= 1; dy++)
                        {
                            for (int dx = -1; dx <= 1; dx++)
                            {
                                int ni = ((y + dy) * w + (x + dx)) * 3;
                                medianBuf[idx++] = output[ni + c] - output[ni + 1];
                            }
                        }
                        filtered[oi + c] = gCenter + Median9(medianBuf);
                    }
                }
                else
                {
                    filtered[oi]     = output[oi];
                    filtered[oi + 2] = output[oi + 2];
                }
            }
        });

        return filtered;
    }

    /// <summary>Fill the outermost <paramref name="radius"/> pixels of the
    /// AHD output via bilinear interpolation. Internal kernels are the same
    /// as <see cref="RunBilinear"/> but write into the pre-allocated buffer
    /// instead of allocating a new one.</summary>
    private static void FillBorderBilinear(float[] mosaic, float[] output, int w, int h, int radius, CanonCfaPattern pattern)
    {
        var (p00, p01, p10, p11) = PatternColors(pattern);

        // Top / bottom bands (full width).
        for (var y = 0; y < h; y++)
        {
            if (y >= radius && y < h - radius) continue;
            for (var x = 0; x < w; x++) WriteBilinearPixel(mosaic, output, w, h, x, y, p00, p01, p10, p11);
        }
        // Left / right bands (excluding the corners already done above).
        for (var y = radius; y < h - radius; y++)
        {
            for (var x = 0; x < radius; x++) WriteBilinearPixel(mosaic, output, w, h, x, y, p00, p01, p10, p11);
            for (var x = w - radius; x < w; x++) WriteBilinearPixel(mosaic, output, w, h, x, y, p00, p01, p10, p11);
        }
    }

    private static void WriteBilinearPixel(
        float[] mosaic, float[] output, int w, int h, int x, int y,
        byte p00, byte p01, byte p10, byte p11)
    {
        var rowEven = (y & 1) == 0;
        var colEven = (x & 1) == 0;
        var cellColor = rowEven ? (colEven ? p00 : p01) : (colEven ? p10 : p11);

        int oi = (y * w + x) * 3;
        float center = mosaic[y * w + x];

        if (cellColor == 1)
        {
            var eastColor = rowEven ? (colEven ? p01 : p00) : (colEven ? p11 : p10);
            var hIsR = eastColor == 0;
            var hAvg = (Sample(mosaic, w, h, x - 1, y) + Sample(mosaic, w, h, x + 1, y)) * 0.5f;
            var vAvg = (Sample(mosaic, w, h, x, y - 1) + Sample(mosaic, w, h, x, y + 1)) * 0.5f;
            output[oi]     = hIsR ? hAvg : vAvg;
            output[oi + 1] = center;
            output[oi + 2] = hIsR ? vAvg : hAvg;
        }
        else
        {
            var cardinal = (Sample(mosaic, w, h, x - 1, y)
                          + Sample(mosaic, w, h, x + 1, y)
                          + Sample(mosaic, w, h, x, y - 1)
                          + Sample(mosaic, w, h, x, y + 1)) * 0.25f;
            var diagonal = (Sample(mosaic, w, h, x - 1, y - 1)
                          + Sample(mosaic, w, h, x + 1, y - 1)
                          + Sample(mosaic, w, h, x - 1, y + 1)
                          + Sample(mosaic, w, h, x + 1, y + 1)) * 0.25f;
            if (cellColor == 0)
            {
                output[oi]     = center;
                output[oi + 1] = cardinal;
                output[oi + 2] = diagonal;
            }
            else
            {
                output[oi]     = diagonal;
                output[oi + 1] = cardinal;
                output[oi + 2] = center;
            }
        }
    }

    /// <summary>Rec.709 luma — same weights TianWen's AHD uses, and what
    /// dcraw's homogeneity metric ranks against in practice. The actual
    /// constants don't matter much for the vote (any monotone luma proxy
    /// works); Rec.709 is the convention.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float Luma(float r, float g, float b)
        => MathF.FusedMultiplyAdd(0.2126f, r, MathF.FusedMultiplyAdd(0.7152f, g, 0.0722f * b));

    /// <summary>Median of 9 floats via an insertion-style partial sort. ~25
    /// compares; for a 5500x3700 image at one median call per pixel times two
    /// channels this is ~40 M operations — fast enough that pulling in a
    /// faster sorting-network is not worth the complexity.</summary>
    private static float Median9(Span<float> values)
    {
        // Insertion sort up to position 4 (the median). Stops half-way through
        // because we only need the middle element, not a fully sorted span.
        for (var i = 1; i < 9; i++)
        {
            var v = values[i];
            var j = i - 1;
            while (j >= 0 && values[j] > v)
            {
                values[j + 1] = values[j];
                j--;
            }
            values[j + 1] = v;
        }
        return values[4];
    }
}
