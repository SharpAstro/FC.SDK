using System;

namespace FC.SDK.Raw;

/// <summary>
/// Debayer algorithm choice for <see cref="CanonDemosaic.Render"/>.
/// </summary>
public enum CanonDemosaicAlgorithm
{
    /// <summary>Bilinear interpolation: at each pixel, average the nearest
    /// same-channel neighbours. Fast (~one pass over the mosaic), simple,
    /// prone to zipper artefacts on hard edges — but fine for soft
    /// astronomical / natural-light scenes and the de-facto default in most
    /// raw codecs' "fast preview" modes.</summary>
    Bilinear,

    /// <summary>Adaptive Homogeneity-Directed demosaic (Hirakawa &amp; Parks,
    /// 2005, as implemented by dcraw). Interpolates green in horizontal and
    /// vertical directions separately, picks the more homogeneous direction
    /// per pixel via a 5x5 luma + chroma neighborhood metric, reconstructs
    /// R / B from the chosen green via colour-difference interpolation, then
    /// runs a 3x3 median on (R - G) / (B - G) for artefact reduction. ~5x
    /// slower than bilinear; virtually eliminates the colour-fringe artefacts
    /// on hard edges. Default for general-use rendering.</summary>
    Ahd,
}

/// <summary>
/// Render options for <see cref="CanonDemosaic.Render"/>. Defaults mirror the
/// "open the file in Affinity Photo / Lightroom" behaviour: black-subtract
/// using the Canon 14-bit pedestal, apply the as-shot white-balance from the
/// MakerNote (with daylight fallback), demosaic via AHD, apply the per-model
/// camera-to-sRGB colour matrix, encode sRGB gamma. Toggle individual stages
/// off for pipelines that want raw linear data (HDR fusion, calibration
/// arithmetic, etc.).
/// </summary>
public sealed record CanonRenderOptions
{
    /// <summary>Demosaic algorithm. <see cref="CanonDemosaicAlgorithm.Ahd"/>
    /// by default — quality wins over speed for the typical sensible-default
    /// render use case.</summary>
    public CanonDemosaicAlgorithm Algorithm { get; init; } = CanonDemosaicAlgorithm.Ahd;

    /// <summary>Black-level pedestal subtracted from every raw sample before
    /// white-balance and demosaic. The Canon 14-bit baseline is 2048 at
    /// non-extended ISOs; the real per-channel black is in MakerNote ColorData
    /// but variation between channels is &lt; 5 counts on a healthy sensor —
    /// invisible after stretch. Set to 0 to skip subtraction (dark-frame
    /// arithmetic, calibration masters).</summary>
    public int BlackLevel { get; init; } = 2048;

    /// <summary>White-balance multipliers applied to each CFA channel before
    /// demosaic. When null, uses <c>CanonRawFile.MakerNote.AsShotWhiteBalance</c>
    /// from the file; if that's also null, falls back to Canon-typical
    /// daylight constants (R = 2.0, G = 1.0, B = 1.4). Set explicitly to
    /// override the in-file value (e.g. to render under a different scene
    /// illuminant).</summary>
    public CanonWhiteBalance? WhiteBalance { get; init; }

    /// <summary>Apply the per-model camera-RGB to sRGB colour matrix from
    /// <see cref="CanonCameraProfiles"/>. When the EXIF model isn't in the
    /// table, camera RGB is passed through unchanged regardless of this flag.
    /// Disable for pipelines that want camera-native primaries (e.g. SPCC /
    /// photometric calibration that re-derives the matrix from scene stars).</summary>
    public bool ApplyColorMatrix { get; init; } = true;

    /// <summary>Apply the standard sRGB transfer function (gamma encode)
    /// before writing the 16-bit output buffer. Default true: matches what
    /// PNG / JPEG / browsers / Explorer all assume on unmanaged images.
    /// Disable for HDR / linear pipelines that gamma-encode downstream.</summary>
    public bool ApplySrgbGamma { get; init; } = true;

    /// <summary>Joint-stretch the demosaiced + matrixed RGB so the brightest
    /// pixel lands at full scale before gamma. Single divisor across R/G/B so
    /// the WB / matrix ratios survive — without this, WB amplification pushes
    /// the highlight channels past the ushort range and they clamp. Default
    /// true: produces a "ready to view" image. Disable if you need values
    /// proportional to scene radiance (the trade-off is that ushort output
    /// will clip at the brightest channel).</summary>
    public bool AutoStretch { get; init; } = true;
}

/// <summary>
/// Demosaiced + colour-corrected RGB image. 16-bit per channel, interleaved
/// row-major (<c>R0 G0 B0 R1 G1 B1 ...</c>). Length of <see cref="InterleavedRgb"/>
/// is <c>3 * Width * Height</c>. Encoding is sRGB gamma when
/// <see cref="CanonRenderOptions.ApplySrgbGamma"/> was true (the default),
/// linear sRGB primaries otherwise.
/// </summary>
public sealed record CanonRgbImage(
    int Width,
    int Height,
    ushort[] InterleavedRgb)
{
    /// <summary>Index of the red sample for pixel (x, y) — convenience for
    /// callers that prefer a flat-array projection over the row stride math.</summary>
    public int IndexOf(int x, int y) => (y * Width + x) * 3;
}
