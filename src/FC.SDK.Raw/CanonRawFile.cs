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
    CanonMakerNote? MakerNote)
{
    /// <summary>
    /// The part of <see cref="BayerMosaic"/> that is a photograph. <see cref="Width"/> and
    /// <see cref="Height"/> are the full sensor raster, which on every Canon body is larger: the
    /// left and top edges carry shielded and partly shielded photosites, and the right and bottom a
    /// few spare columns and rows.
    /// </summary>
    /// <remarks>
    /// <para>Read from Canon's <c>SensorInfo</c> MakerNote tag, falling back to the whole frame when
    /// the file does not describe a rectangle that fits these pixels. <b>Nothing here crops the
    /// mosaic</b> -- see <see cref="CanonSensorInfo"/> for why the decoder must keep handing back the
    /// full raster, and for what the discarded margin is and is not good for.</para>
    /// <para>Applying it means reading <c>BayerMosaic[(Top + y) * Width + (Left + x)]</c> and taking
    /// the pattern from <see cref="CanonActiveArea.CfaPattern"/> rather than from
    /// <see cref="CfaPattern"/>: an odd offset re-phases the colour filter.</para>
    /// </remarks>
    public CanonActiveArea ActiveArea => CanonSensorInfo.Resolve(Width, Height, CfaPattern, MakerNote);
}

/// <summary>
/// The rectangle of a Canon sensor raster that carries a picture, and the Bayer pattern seen from
/// its origin.
/// </summary>
/// <param name="Left">Column of the first picture pixel in the full raster.</param>
/// <param name="Top">Row of the first picture pixel in the full raster.</param>
/// <param name="Width">Picture width in pixels.</param>
/// <param name="Height">Picture height in pixels.</param>
/// <param name="CfaPattern">The pattern at (<paramref name="Left"/>, <paramref name="Top"/>), which
/// differs from the full raster's whenever either offset is odd. Use this one after cropping.</param>
public readonly record struct CanonActiveArea(
    int Left,
    int Top,
    int Width,
    int Height,
    CanonCfaPattern CfaPattern)
{
    /// <summary>True when the active area is the whole raster, i.e. there is nothing to crop --
    /// either the body records no margin or the file did not describe a usable rectangle.</summary>
    public bool IsWholeFrame => Left == 0 && Top == 0;
}

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
    System.Collections.Generic.IReadOnlyDictionary<ushort, byte[]> RawSubtags)
{
    /// <summary>
    /// Byte order of <see cref="RawSubtags"/>, which is the file's, not the machine's.
    /// </summary>
    /// <remarks>
    /// The strongly-typed fields above are already decoded, so this exists for anything reading the
    /// raw bytes back -- <see cref="CanonSensorInfo"/> is the one such reader today. Defaulted to
    /// little-endian, which every Canon file we have ever seen is, and set explicitly by both
    /// decoders; a decoder that forgot would at worst read a nonsense rectangle, which
    /// <see cref="CanonSensorInfo.Resolve"/> rejects against the decoded frame size rather than
    /// acting on.
    /// </remarks>
    public bool IsLittleEndian { get; init; } = true;
}

/// <summary>
/// Per-channel white-balance multipliers as stored in Canon's MakerNote
/// ColorData. The raw values from the file are normalised so that
/// <see cref="G1"/> = 1.0; multiplying R / G1 / G2 / B sensor counts by the
/// corresponding multiplier produces a neutral-white render. Typical daylight
/// values are R ≈ 2.0, G ≈ 1.0, B ≈ 1.5.
/// </summary>
public sealed record CanonWhiteBalance(float R, float G1, float G2, float B);
