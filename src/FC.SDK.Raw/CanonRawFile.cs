using SharpAstro.Exif;

namespace FC.SDK.Raw;

/// <summary>
/// Decoded Canon raw file: the raw Bayer mosaic at sensor native bit depth
/// (typically 14-bit packed into <see cref="ushort"/>), the colour-filter-array
/// pattern, plus parsed metadata. Demosaicing is a separate step — callers that
/// just need the mosaic (astronomical stacking, calibration master generation)
/// can skip it entirely.
/// </summary>
/// <param name="Width">Sensor active-area width in pixels.</param>
/// <param name="Height">Sensor active-area height in pixels.</param>
/// <param name="BayerMosaic">Length = <c>Width * Height</c>, row-major. Values
/// are the linear sensor counts in [0, 2^BitDepth - 1].</param>
/// <param name="BitDepth">Native sensor bit depth (14 for most modern Canon
/// DSLRs, 12 for some older bodies).</param>
/// <param name="CfaPattern">Bayer pattern starting at pixel (0, 0).</param>
/// <param name="Exif">Standard EXIF metadata (model, exposure, ISO, etc.) as
/// parsed by SharpAstro.Exif. Null only on malformed files.</param>
/// <param name="MakerNote">Canon-specific MakerNote subtags. Null when the
/// MakerNote IFD is missing or unparseable. Contains sensor model code,
/// colour matrix, white-balance presets, lens info, etc.</param>
public sealed record CanonRawFile(
    int Width,
    int Height,
    ushort[] BayerMosaic,
    int BitDepth,
    CanonCfaPattern CfaPattern,
    ExifMetadata? Exif,
    CanonMakerNote? MakerNote);

/// <summary>
/// Bayer colour-filter pattern at sensor pixel (0, 0). Matches the standard
/// 2x2 block notation used by libraw / dcraw and the EXIF CFAPattern tag.
/// </summary>
public enum CanonCfaPattern
{
    Rggb,
    Bggr,
    Gbrg,
    Grbg,
}

/// <summary>
/// Subset of Canon MakerNote subtags relevant for raw-file processing. Filled
/// best-effort — null fields mean the subtag wasn't present or wasn't decoded
/// for the body model. The raw subtag dictionary is preserved in
/// <see cref="RawSubtags"/> for callers that need more than the strongly-typed
/// projection.
/// </summary>
/// <param name="ModelId">Canon model ID (the integer code used in EDSDK
/// property <c>kEdsPropID_ProductName</c>'s neighbours — matches body model
/// regardless of localised name).</param>
/// <param name="SensorWidth">Sensor active-area width in pixels (may differ
/// from the TIFF strip width when the raw frame carries margin pixels).</param>
/// <param name="SensorHeight">Sensor active-area height.</param>
/// <param name="ColorMatrix">Per-model 3×3 colour-conversion matrix from
/// sensor RGB to sRGB (linear, no gamma). Row-major, 9 floats.</param>
/// <param name="AsShotWhiteBalance">As-shot white-balance multipliers parsed
/// from MakerNote ColorData's WB_RGGB_LEVELS_AS_SHOT block (4 values: R, G1,
/// G2, B at offset 63 in newer ColorData versions). Values are normalised so
/// G1 = 1.0; callers multiply the demosaiced R/G/B channels by these to
/// neutralise white. Null when ColorData layout doesn't match a known
/// version (callers fall back to daylight constants).</param>
/// <param name="RawSubtags">All MakerNote subtags as raw bytes for callers
/// that want the long tail (lens info, image stabiliser state, custom
/// functions, etc.).</param>
public sealed record CanonMakerNote(
    int? ModelId,
    int? SensorWidth,
    int? SensorHeight,
    float[]? ColorMatrix,
    CanonWhiteBalance? AsShotWhiteBalance,
    System.Collections.Generic.IReadOnlyDictionary<ushort, byte[]> RawSubtags);

/// <summary>
/// Per-channel white-balance multipliers as stored in Canon's MakerNote
/// ColorData. The raw values from the file are normalised so that
/// <see cref="G1"/> = 1.0; multiplying R / G1 / G2 / B sensor counts by the
/// corresponding multiplier produces a neutral-white render. Typical daylight
/// values are R ≈ 2.0, G ≈ 1.0, B ≈ 1.5.
/// </summary>
public sealed record CanonWhiteBalance(float R, float G1, float G2, float B);
