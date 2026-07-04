using System;
using System.IO;

namespace FC.SDK.Raw;

/// <summary>
/// Top-level entry point for decoding Canon CR2 (TIFF container) and CR3 (ISO BMFF
/// container) raw files into a Bayer mosaic + metadata. Pure managed C# — no
/// EDSDK / libraw / dcraw binary required.
///
/// Pipeline:
/// <list type="number">
/// <item><b>CR2</b>: outer TIFF parse → locate the raw IFD (compression 6 +
///   CR2Slice tag 0xC640) → SOF3 lossless-JPEG decode via SharpAstro.Jpeg →
///   slice unscramble per the CR2Slice descriptor → parse Canon MakerNote
///   (TIFF tag 0x927C) for sensor / model / colour-matrix info → return
///   <see cref="CanonRawFile"/>.</item>
/// <item><b>CR3</b>: ISO BMFF container walk → CRX-compressed raw payload.
///   Not yet implemented — <see cref="FromBytes"/> throws on CR3 input until
///   the ISO BMFF parser + CRX decoder land.</item>
/// </list>
///
/// This package intentionally stops at the raw Bayer mosaic — the
/// astronomical-stacking and calibration-master use cases (the original
/// motivation for FC.SDK.Raw) need the mosaic, not a rendered RGB image.
/// Callers wanting a sensible-default JPEG render must supply their own
/// demosaic + colour pipeline; the per-model <see cref="CanonCameraProfiles"/>
/// table + <see cref="CanonRawFile.MakerNote"/>'s as-shot WB give them
/// everything except the demosaic itself.
/// </summary>
public static class CanonRaw
{
    /// <summary>Decode a Canon raw file from disk. Equivalent to
    /// <c>FromBytes(File.ReadAllBytes(path))</c>; the in-memory variant is
    /// preferred when the caller already has the file bytes (e.g. fetched over
    /// PTP via FC.SDK's <c>CanonCamera.TransferCompleteAsync</c>).</summary>
    public static CanonRawFile Open(string path) => FromBytes(File.ReadAllBytes(path));

    /// <summary>Extract the embedded preview JPEG (the largest one, suitable for
    /// in-app thumbnails / "before you wait for the full raw" UIs). For CR2 this
    /// is the IFD0 strip — Canon stores a full-resolution sRGB JPEG there.
    /// Returns null if no embedded JPEG is present or the file is malformed.</summary>
    public static byte[]? ExtractThumbnailJpeg(string path) => ExtractThumbnailJpeg(File.ReadAllBytes(path));

    /// <inheritdoc cref="ExtractThumbnailJpeg(string)"/>
    public static byte[]? ExtractThumbnailJpeg(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 16) return null;
        if ((bytes[0] == 'I' && bytes[1] == 'I') || (bytes[0] == 'M' && bytes[1] == 'M'))
        {
            return Cr2Decoder.ExtractThumbnail(bytes);
        }
        // CR3: walk the BMFF box tree to PRVW (preferred, larger preview at
        // ~1620x1080) or THMB (fallback, 160x120). Cr3Decoder handles both
        // and returns null on any structural failure.
        if (bytes.Length >= 12 && bytes[4] == 'f' && bytes[5] == 't' && bytes[6] == 'y' && bytes[7] == 'p')
        {
            return Cr3Decoder.ExtractThumbnail(bytes);
        }
        return null;
    }

    /// <summary>One-pass conversion of the raw Bayer mosaic to a normalised,
    /// black-subtracted, white-balanced <see cref="float"/> array sized
    /// <c>Width * Height</c>. This is the natural input for any debayer
    /// algorithm — TianWen's <c>DebayerAsync</c>, a custom drizzle stacker,
    /// or <see cref="CanonDemosaic"/>'s own Bilinear / AHD kernels.
    ///
    /// <para>The same loop reads the source <see cref="ushort"/>, subtracts
    /// the pedestal, divides by <c>(maxRaw - blackLevel)</c> to land in
    /// linear <c>[0, 1+headroom]</c>, and multiplies by the per-CFA-position
    /// white-balance multiplier — fused into one pass so the working buffer
    /// is float-format and ready for further linear processing in a single
    /// memory traversal.</para>
    ///
    /// <para>WB selection: explicit <paramref name="whiteBalance"/> override
    /// wins, then <c>CanonRawFile.MakerNote.AsShotWhiteBalance</c>, then a
    /// Canon-typical daylight fallback (R = 2.0, G = 1.0, B = 1.4). Pass an
    /// all-1.0 <see cref="CanonWhiteBalance"/> to skip WB entirely (useful
    /// for calibration arithmetic where the multipliers would skew dark /
    /// flat math).</para>
    /// </summary>
    /// <param name="raw">Decoded raw file (typically from <see cref="Open"/>).</param>
    /// <param name="blackLevel">Per-channel pedestal subtracted before the
    /// linear conversion. 2048 is the Canon 14-bit default; the real
    /// per-channel black is in MakerNote ColorData but rarely deviates by
    /// more than a few counts on a healthy sensor.</param>
    /// <param name="whiteBalance">Optional WB override (see selection
    /// precedence above). Null = use the file's as-shot or daylight fallback.</param>
    public static float[] PreprocessMosaic(
        CanonRawFile raw, int blackLevel = 2048, CanonWhiteBalance? whiteBalance = null)
    {
        ArgumentNullException.ThrowIfNull(raw);

        var w = raw.Width;
        var h = raw.Height;
        var mosaic = raw.BayerMosaic;
        var maxRaw = (1 << raw.BitDepth) - 1;
        var headroom = (float)Math.Max(1, maxRaw - blackLevel);

        var wb = whiteBalance
            ?? raw.MakerNote?.AsShotWhiteBalance
            ?? new CanonWhiteBalance(2.0f, 1.0f, 1.0f, 1.4f);

        // Pre-resolve the per-cell WB multipliers so the inner loop is a
        // parity-indexed lookup and a single multiply.
        var (m00, m01, m10, m11) = WbCellMultipliers(raw.CfaPattern, wb);

        var result = new float[w * h];
        for (var y = 0; y < h; y++)
        {
            var rowBase = y * w;
            var rowEven = (y & 1) == 0;
            for (var x = 0; x < w; x++)
            {
                var raw0 = mosaic[rowBase + x] - blackLevel;
                if (raw0 < 0) raw0 = 0;
                var linear = raw0 / headroom;
                var colEven = (x & 1) == 0;
                var mul = rowEven
                    ? (colEven ? m00 : m01)
                    : (colEven ? m10 : m11);
                result[rowBase + x] = linear * mul;
            }
        }
        return result;
    }

    /// <summary>Per-cell WB lookup for a given CFA pattern. The 2x2 cell
    /// positions are <c>(row even, col even)</c>, <c>(row even, col odd)</c>,
    /// <c>(row odd, col even)</c>, <c>(row odd, col odd)</c> — the inner
    /// loop uses row/col parity to index into this 4-tuple branch-free.</summary>
    internal static (float m00, float m01, float m10, float m11) WbCellMultipliers(
        CanonCfaPattern p, CanonWhiteBalance wb) => p switch
    {
        // RGGB: row even -> R G1, row odd -> G2 B
        CanonCfaPattern.Rggb => (wb.R, wb.G1, wb.G2, wb.B),
        // BGGR: row even -> B G1, row odd -> G2 R
        CanonCfaPattern.Bggr => (wb.B, wb.G1, wb.G2, wb.R),
        // GBRG: row even -> G1 B, row odd -> R G2
        CanonCfaPattern.Gbrg => (wb.G1, wb.B, wb.R, wb.G2),
        // GRBG: row even -> G1 R, row odd -> B G2
        CanonCfaPattern.Grbg => (wb.G1, wb.R, wb.B, wb.G2),
        _ => throw new ArgumentOutOfRangeException(nameof(p)),
    };

    /// <summary>Decode a Canon raw file from an in-memory byte span. Container
    /// type is detected by signature: <c>"II"</c> / <c>"MM"</c> + TIFF magic
    /// → CR2; ISO BMFF <c>ftyp</c> box → CR3.</summary>
    public static CanonRawFile FromBytes(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 16)
            throw new InvalidDataException("Input too short to be a Canon raw file");

        // CR2: TIFF byte-order mark + magic 42 + Canon-specific "CR" / version /
        // raw-IFD offset at bytes 8..15. The CR signature isn't strictly required
        // (some plain TIFF-with-lossless-JPEG would also decode), but using it as
        // the dispatch gate avoids mis-decoding ordinary TIFFs that happen to
        // hit this method.
        if ((bytes[0] == 'I' && bytes[1] == 'I') || (bytes[0] == 'M' && bytes[1] == 'M'))
        {
            return Cr2Decoder.Decode(bytes);
        }

        // CR3: ISO BMFF "ftyp" box at offset 4 (after the 32-bit box size).
        // Phase A: Cr3Decoder walks the container and extracts EXIF + MakerNote +
        // dimensions + thumbnail, then throws NotImplementedException for the
        // CRX-compressed sensor frame (Phase B work). The throw moves into
        // Cr3Decoder so container-level errors (truncated file, missing CMP1)
        // surface with a clearer message before the CRX one.
        if (bytes.Length >= 12 && bytes[4] == 'f' && bytes[5] == 't' && bytes[6] == 'y' && bytes[7] == 'p')
        {
            return Cr3Decoder.Decode(bytes, decodeMosaic: true);
        }

        throw new InvalidDataException(
            $"Unrecognised container — first 8 bytes: {Convert.ToHexString(bytes[..8])}. " +
            "Expected TIFF (II/MM + 42) for CR2 or ISO BMFF (ftyp box) for CR3.");
    }
}
