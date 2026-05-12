using System;
using System.Collections.Generic;

namespace FC.SDK.Raw;

/// <summary>
/// Per-camera-model raw colour-pipeline profile (black level, saturation, and
/// camera-to-XYZ-D65 colour matrix). Mirrors entries from dcraw's
/// <c>adobe_coeff</c> table — the canonical public-domain source for these
/// values. Use <see cref="CanonCameraProfiles.ResolveProfile"/> to look one up
/// by EXIF Model string.
///
/// <para>The colour matrix lets a consumer convert white-balanced camera RGB
/// into sRGB so a neutral scene renders neutral (without it, the WB-amplified
/// R and B channels overrun sRGB's primaries and produce a magenta cast).</para>
/// </summary>
/// <param name="Model">Camera model string, matches EXIF tag 0x0110.</param>
/// <param name="BlackLevel">Per-channel raw pedestal in ADU. <c>0</c> when dcraw
/// has no override; callers should read per-channel black levels from MakerNote
/// or default to a model-typical value (2048 for Canon 14-bit, 1024 for 12-bit).</param>
/// <param name="MaxRaw">Sensor saturation level in ADU. <c>0</c> when dcraw
/// has no override; callers should default to <c>(1 &lt;&lt; bitDepth) - 1</c>.</param>
/// <param name="CamToXyz">9 ints, row-major, representing the linear
/// camera-RGB → XYZ-D65 matrix scaled by 10000 (dcraw's storage convention).
/// Negative entries are normal.</param>
public sealed record CanonCameraProfile(
    string Model,
    int BlackLevel,
    int MaxRaw,
    int[] CamToXyz)
{
    /// <summary>Compute the 3×3 camera-RGB → sRGB matrix (9 floats, row-major)
    /// per dcraw's <c>cam_xyz_coeff</c>: multiply <see cref="CamToXyz"/>
    /// (scaled back to 1/1) by the sRGB-to-XYZ-D65 primaries matrix, normalise
    /// each row to sum to 1 (so a neutral camera input maps to neutral sRGB
    /// output), then take the 3×3 inverse. The returned matrix is applied to a
    /// WB-corrected camera-RGB pixel:
    /// <code>
    /// sRGB[c] = rgb_cam[c, 0] * camR + rgb_cam[c, 1] * camG + rgb_cam[c, 2] * camB
    /// </code>
    /// The result is in linear sRGB space — apply the standard sRGB transfer
    /// function before writing to an 8-bit/PNG output.</summary>
    public float[] ComputeRgbCam()
    {
        // sRGB primaries in XYZ-D65 (canonical Rec. 709). Matches dcraw's
        // const xyz_rgb[3][3] in /dcraw.c.
        ReadOnlySpan<double> xyzRgb = stackalloc double[]
        {
            0.412453, 0.357580, 0.180423,
            0.212671, 0.715160, 0.072169,
            0.019334, 0.119193, 0.950227,
        };

        // Step 1: cam_rgb = cam_xyz * xyz_rgb (matrix product). cam_xyz comes
        // from dcraw's table scaled by 10000, so divide as we go.
        Span<double> camRgb = stackalloc double[9];
        for (var i = 0; i < 3; i++)
        for (var j = 0; j < 3; j++)
        {
            double s = 0;
            for (var k = 0; k < 3; k++)
                s += (CamToXyz[i * 3 + k] / 10000.0) * xyzRgb[k * 3 + j];
            camRgb[i * 3 + j] = s;
        }

        // Step 2: normalise each row so it sums to 1 — encodes "neutral camera
        // RGB (1,1,1) maps to neutral output (1,1,1)" into the matrix itself.
        for (var i = 0; i < 3; i++)
        {
            var rowSum = camRgb[i * 3] + camRgb[i * 3 + 1] + camRgb[i * 3 + 2];
            if (rowSum == 0) continue;
            for (var j = 0; j < 3; j++) camRgb[i * 3 + j] /= rowSum;
        }

        // Step 3: 3x3 inverse via cofactor expansion. cam_rgb takes neutral
        // camera -> neutral output; we want the reverse direction (apply to a
        // demosaiced camera-RGB pixel to get sRGB), which is the inverse.
        var m00 = camRgb[0]; var m01 = camRgb[1]; var m02 = camRgb[2];
        var m10 = camRgb[3]; var m11 = camRgb[4]; var m12 = camRgb[5];
        var m20 = camRgb[6]; var m21 = camRgb[7]; var m22 = camRgb[8];
        var det = m00 * (m11 * m22 - m12 * m21)
                - m01 * (m10 * m22 - m12 * m20)
                + m02 * (m10 * m21 - m11 * m20);
        if (Math.Abs(det) < 1e-12)
            throw new InvalidOperationException(
                $"cam_rgb for {Model} is singular (det={det:E3}); profile entry is malformed.");

        var invDet = 1.0 / det;
        return new float[]
        {
            (float)((m11 * m22 - m12 * m21) * invDet),
            (float)((m02 * m21 - m01 * m22) * invDet),
            (float)((m01 * m12 - m02 * m11) * invDet),
            (float)((m12 * m20 - m10 * m22) * invDet),
            (float)((m00 * m22 - m02 * m20) * invDet),
            (float)((m02 * m10 - m00 * m12) * invDet),
            (float)((m10 * m21 - m11 * m20) * invDet),
            (float)((m01 * m20 - m00 * m21) * invDet),
            (float)((m00 * m11 - m01 * m10) * invDet),
        };
    }
}
