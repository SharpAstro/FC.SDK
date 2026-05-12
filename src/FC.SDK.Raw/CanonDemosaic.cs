using System;

namespace FC.SDK.Raw;

/// <summary>
/// Demosaics a <see cref="CanonRawFile"/>'s Bayer mosaic into a 16-bit sRGB
/// <see cref="CanonRgbImage"/>, applying the full sensible-defaults pipeline:
/// black-level subtract -&gt; white balance (as-shot or daylight fallback) -&gt;
/// Bilinear / AHD interpolation -&gt; camera-to-sRGB matrix from
/// <see cref="CanonCameraProfiles"/> -&gt; joint stretch -&gt; sRGB gamma.
/// Pipeline stages are individually toggleable via <see cref="CanonRenderOptions"/>.
///
/// <para>I/O is <see cref="ushort"/> on the surface (raw mosaic in, RGB pixels
/// out); internally the entire pipeline runs in <see cref="float"/> so the
/// WB amplification + matrix steps don't lose precision against the source
/// 14-bit range. The output is canonically 16-bit so downstream PNG / TIFF
/// writers get the full precision; downconvert to 8-bit JPEG with a single
/// <c>(byte)(v &gt;&gt; 8)</c> if needed.</para>
///
/// <para>The companion <see cref="CanonCameraProfiles"/> table only covers
/// Canon — the matrix step silently no-ops on unrecognised models and the
/// caller still gets a WB-corrected, demosaiced image. For "I just want the
/// Bayer mosaic" use cases (astronomical stacking, calibration masters)
/// stop at <see cref="CanonRawFile.BayerMosaic"/> and don't call this.</para>
/// </summary>
public static partial class CanonDemosaic
{
    /// <summary>Renders the raw mosaic into a 16-bit sRGB image.
    /// See <see cref="CanonRenderOptions"/> for stage toggles.</summary>
    public static CanonRgbImage Render(CanonRawFile raw, CanonRenderOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(raw);
        options ??= new CanonRenderOptions();

        var w = raw.Width;
        var h = raw.Height;

        // Stage 1: fused ushort -> float + black-subtract + per-CFA-cell WB.
        // Done before demosaic so the interpolation averages already-balanced
        // samples and the per-channel scales survive the bilinear / AHD
        // averaging cleanly. Single-pass; the result buffer is the only
        // float[w*h] allocation in the pipeline outside the RGB output.
        var wbMosaic = CanonRaw.PreprocessMosaic(raw, options.BlackLevel, options.WhiteBalance);

        // Stage 2: demosaic into a flat interleaved RGB float buffer.
        var rgb = options.Algorithm switch
        {
            CanonDemosaicAlgorithm.Bilinear => RunBilinear(wbMosaic, w, h, raw.CfaPattern),
            CanonDemosaicAlgorithm.Ahd => RunAhd(wbMosaic, w, h, raw.CfaPattern),
            _ => throw new ArgumentOutOfRangeException(nameof(options),
                $"Unsupported demosaic algorithm {options.Algorithm}"),
        };

        // Stage 3: camera-RGB -> sRGB matrix from the Canon profile table.
        // After WB the channels look neutral in camera-RGB space; the matrix
        // mixes them into sRGB primaries so a neutral scene actually renders
        // neutral. No-op when the model isn't in the table or the caller
        // disabled the stage.
        if (options.ApplyColorMatrix)
        {
            var cm = CanonCameraProfiles.ResolveProfile(raw.Exif?.Model)?.ComputeRgbCam();
            if (cm is not null) ApplyMatrixInPlace(rgb, cm);
        }

        // Stage 4: optional joint stretch (single divisor across R/G/B) so
        // WB-amplified highlights don't clip, then sRGB gamma encode, then
        // quantise to 16-bit ushort.
        return Finalize(rgb, w, h, options);
    }

    /// <summary>Apply the 3x3 camera-RGB to sRGB matrix in place over a flat
    /// interleaved RGB float buffer. The matrix is 9 floats row-major as
    /// returned by <see cref="CanonCameraProfile.ComputeRgbCam"/>.</summary>
    private static void ApplyMatrixInPlace(float[] rgb, float[] cm)
    {
        for (var i = 0; i < rgb.Length; i += 3)
        {
            var r = rgb[i];
            var g = rgb[i + 1];
            var b = rgb[i + 2];
            rgb[i]     = cm[0] * r + cm[1] * g + cm[2] * b;
            rgb[i + 1] = cm[3] * r + cm[4] * g + cm[5] * b;
            rgb[i + 2] = cm[6] * r + cm[7] * g + cm[8] * b;
        }
    }

    /// <summary>Joint stretch + optional sRGB gamma + ushort conversion.
    /// Joint stretch is a single global divisor across all three channels so
    /// WB / matrix ratios stay intact and highlights don't clip the brightest
    /// channel. Skipped when <see cref="CanonRenderOptions.AutoStretch"/> is
    /// false — in that case values are clipped at 1.0 before gamma.</summary>
    private static CanonRgbImage Finalize(float[] rgb, int w, int h, CanonRenderOptions options)
    {
        var divisor = 1.0f;
        if (options.AutoStretch)
        {
            var max = 0f;
            for (var i = 0; i < rgb.Length; i++) if (rgb[i] > max) max = rgb[i];
            if (max > 1e-6f) divisor = max;
        }

        var output = new ushort[rgb.Length];
        var gamma = options.ApplySrgbGamma;
        for (var i = 0; i < rgb.Length; i++)
        {
            var v = rgb[i] / divisor;
            if (v < 0) v = 0;
            else if (v > 1) v = 1;
            if (gamma) v = SrgbEncode(v);
            // Round-to-nearest 16-bit. Add 0.5 instead of MidpointRounding so we
            // stay branch-free; 65535 * 1.0 fits cleanly in float.
            output[i] = (ushort)(v * 65535f + 0.5f);
        }
        return new CanonRgbImage(w, h, output);
    }

    /// <summary>Standard sRGB transfer function (IEC 61966-2-1). The linear-to-
    /// sRGB encode applied before writing 8/16-bit display-referred pixels.</summary>
    internal static float SrgbEncode(float linear)
    {
        return linear <= 0.0031308f
            ? 12.92f * linear
            : 1.055f * MathF.Pow(linear, 1f / 2.4f) - 0.055f;
    }

    /// <summary>Resolve the CFA colour (0=R, 1=G, 2=B) at each of the 4 2x2
    /// cell positions for a given pattern. Used by the bilinear and AHD
    /// implementations to dispatch the per-pixel interpolation kernels.</summary>
    internal static (byte p00, byte p01, byte p10, byte p11) PatternColors(CanonCfaPattern p)
    {
        const byte R = 0, G = 1, B = 2;
        return p switch
        {
            CanonCfaPattern.Rggb => (R, G, G, B),
            CanonCfaPattern.Bggr => (B, G, G, R),
            CanonCfaPattern.Gbrg => (G, B, R, G),
            CanonCfaPattern.Grbg => (G, R, B, G),
            _ => throw new ArgumentOutOfRangeException(nameof(p)),
        };
    }
}
