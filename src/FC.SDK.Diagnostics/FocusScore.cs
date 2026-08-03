using FC.SDK.Raw;

namespace FC.SDK.Diagnostics;

/// <summary>
/// How sharp is this frame? Scores a Canon raw file so an experiment can be judged on its pixels
/// instead of on a response code.
/// </summary>
/// <remarks>
/// <para>
/// Written for the 0x9128 autofocus question, where nothing cheaper worked: all four rows of that
/// matrix answer <c>OK</c> and deliver a ~21 MB CR2 in 130-310 ms, so neither the response code nor
/// the timing distinguishes a release that focused from one that did not. It lives in the repo rather
/// than a scratch directory because it has now decided three separate runs, and a verdict whose
/// instrument is missing cannot be re-checked.
/// </para>
/// <para>
/// <b>A score is only meaningful against another score from the same scene.</b> The number depends on
/// subject detail, so it compares frames within one run and says nothing in isolation. That is why
/// the caller must shoot known-sharp and known-soft controls: without them, frames that all score
/// alike are indistinguishable from a measure that cannot see focus at all, which is exactly the
/// false negative this class was built to stop being fooled by.
/// </para>
/// </remarks>
internal static class FocusScore
{
    /// <summary>
    /// Normalised high-frequency contrast in the centre of the frame. Higher is sharper. On an EOS 6D
    /// with an EF50mm at f/2.8 a focused frame scored 0.114 against 0.060 defocused, so a real focus
    /// difference is a factor, not a few percent.
    /// </summary>
    internal static double Measure(string rawPath)
    {
        var raw = CanonRaw.Open(rawPath);
        var (w, h, mosaic) = (raw.Width, raw.Height, raw.BayerMosaic);

        // One green plane only. Stepping 2 in both axes stays on a single CFA colour, so a colour
        // edge is never mistaken for detail; green is the densest and what the AF system works from.
        var (gx0, gy0) = raw.CfaPattern switch
        {
            CanonCfaPattern.Rggb or CanonCfaPattern.Bggr => (1, 0),
            _ => (0, 0),
        };

        // Centre third: the AF point is central, and frame edges are where a defocus shows least.
        var (x0, x1) = (w / 3, 2 * w / 3);
        var (y0, y1) = (h / 3, 2 * h / 3);
        var gw = (x1 - x0) / 2;
        var gh = (y1 - y0) / 2;
        if (gw < 8 || gh < 8) return 0;

        var plane = new float[gw * gh];
        for (var j = 0; j < gh; j++)
        for (var i = 0; i < gw; i++)
            plane[j * gw + i] = mosaic[(y0 + gy0 + j * 2) * w + (x0 + gx0 + i * 2)];

        // Canon 14-bit black. Subtracting it matters because the normalisation below divides by the
        // mean, and 2048 counts of pedestal would flatten the difference being measured.
        var mean = 0.0;
        foreach (var v in plane) mean += v;
        mean = mean / plane.Length - 2048.0;
        if (mean < 1.0) mean = 1.0;

        // Box-downsample 4x before measuring. These frames can be noisy, and noise is pure high
        // frequency: measured at pixel scale it swamps the signal and the score ends up ranking
        // exposure instead of focus. A gross defocus survives a 4x reduction; read noise does not.
        var dw = gw / 4;
        var dh = gh / 4;
        var small = new float[dw * dh];
        for (var j = 0; j < dh; j++)
        for (var i = 0; i < dw; i++)
        {
            float sum = 0;
            for (var b = 0; b < 4; b++)
            for (var a = 0; a < 4; a++)
                sum += plane[(j * 4 + b) * gw + i * 4 + a];
            small[j * dw + i] = sum / 16f;
        }

        // Gradient across 2 reduced pixels (16 sensor pixels), normalised by signal level so a darker
        // frame is not automatically reported as softer.
        var gradient = 0.0;
        for (var j = 0; j < dh; j++)
        for (var i = 2; i < dw; i++)
            gradient += Math.Abs(small[j * dw + i] - small[j * dw + i - 2]);
        gradient /= dh * (dw - 2);

        return gradient / mean;
    }
}
