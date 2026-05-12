using System;

namespace FC.SDK.Raw;

public static partial class CanonDemosaic
{
    /// <summary>3x3 bilinear demosaic of a WB-corrected Bayer mosaic into a
    /// flat interleaved RGB float buffer (length <c>3 * w * h</c>).
    ///
    /// <para>Per-pixel kernels for an RGGB layout (other CFA patterns rotate
    /// these via <see cref="PatternColors"/>):</para>
    /// <list type="bullet">
    /// <item>At R-site: copy R; G = avg(N, S, E, W); B = avg(NW, NE, SW, SE).</item>
    /// <item>At G-site between R-row pair (G1): copy G; R = avg(W, E); B = avg(N, S).</item>
    /// <item>At G-site between B-row pair (G2): copy G; R = avg(N, S); B = avg(W, E).</item>
    /// <item>At B-site: copy B; G = avg(N, S, E, W); R = avg(NW, NE, SW, SE).</item>
    /// </list>
    /// Edges use clamp-addressing so the kernel degrades to whatever
    /// neighbours exist; no zero-padding artefacts.</summary>
    internal static float[] RunBilinear(float[] mosaic, int w, int h, CanonCfaPattern pattern)
    {
        var output = new float[w * h * 3];
        var (p00, p01, p10, p11) = PatternColors(pattern);

        for (var y = 0; y < h; y++)
        {
            var rowEven = (y & 1) == 0;
            for (var x = 0; x < w; x++)
            {
                var colEven = (x & 1) == 0;
                var cellColor = rowEven
                    ? (colEven ? p00 : p01)
                    : (colEven ? p10 : p11);

                var oi = (y * w + x) * 3;
                var center = mosaic[y * w + x];

                if (cellColor == 1) // Green site
                {
                    // Identify which colour the horizontal neighbours hold.
                    // In a 2x2 CFA cell the G's neighbour-to-the-east is the
                    // opposite-row colour; on RGGB G1 (even row, odd col) the
                    // east neighbour is on the same row at an R column, so
                    // hColor = R. Use the cell table directly.
                    var eastColor = rowEven
                        ? (colEven ? p01 : p00)
                        : (colEven ? p11 : p10);
                    var hIsR = eastColor == 0;

                    var hAvg = (Sample(mosaic, w, h, x - 1, y) + Sample(mosaic, w, h, x + 1, y)) * 0.5f;
                    var vAvg = (Sample(mosaic, w, h, x, y - 1) + Sample(mosaic, w, h, x, y + 1)) * 0.5f;

                    output[oi]     = hIsR ? hAvg : vAvg; // R
                    output[oi + 1] = center;            // G
                    output[oi + 2] = hIsR ? vAvg : hAvg; // B
                }
                else // R-site (cellColor == 0) or B-site (cellColor == 2)
                {
                    var cardinal = (Sample(mosaic, w, h, x - 1, y)
                                  + Sample(mosaic, w, h, x + 1, y)
                                  + Sample(mosaic, w, h, x, y - 1)
                                  + Sample(mosaic, w, h, x, y + 1)) * 0.25f;
                    var diagonal = (Sample(mosaic, w, h, x - 1, y - 1)
                                  + Sample(mosaic, w, h, x + 1, y - 1)
                                  + Sample(mosaic, w, h, x - 1, y + 1)
                                  + Sample(mosaic, w, h, x + 1, y + 1)) * 0.25f;

                    if (cellColor == 0) // R-site
                    {
                        output[oi]     = center;   // R
                        output[oi + 1] = cardinal; // G (4 cardinal neighbours are all G)
                        output[oi + 2] = diagonal; // B (4 diagonal neighbours are all B)
                    }
                    else // B-site
                    {
                        output[oi]     = diagonal; // R (4 diagonal neighbours are all R)
                        output[oi + 1] = cardinal; // G
                        output[oi + 2] = center;   // B
                    }
                }
            }
        }
        return output;
    }

    /// <summary>Clamp-addressed sample. At the edges this reads the nearest
    /// in-bounds pixel — the same convention dcraw uses and the standard
    /// approach for bilinear at the border. The kernel is small enough that
    /// the resulting edge bias is sub-pixel and visually invisible.</summary>
    private static float Sample(float[] mosaic, int w, int h, int x, int y)
    {
        if (x < 0) x = 0; else if (x >= w) x = w - 1;
        if (y < 0) y = 0; else if (y >= h) y = h - 1;
        return mosaic[y * w + x];
    }
}
