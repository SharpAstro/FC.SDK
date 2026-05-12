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
///   CR2Slice tag 0xC640) → SOF3 lossless-JPEG decode via SharpAstro.StbImage →
///   slice unscramble per the CR2Slice descriptor → parse Canon MakerNote
///   (TIFF tag 0x927C) for sensor / model / colour-matrix info → return
///   <see cref="CanonRawFile"/>.</item>
/// <item><b>CR3</b>: ISO BMFF container walk → CRX-compressed raw payload.
///   Not yet implemented — <see cref="FromBytes"/> throws on CR3 input until
///   the ISO BMFF parser + CRX decoder land.</item>
/// </list>
///
/// Demosaicing lives in <c>CanonDemosaic</c> (separate class) so callers that
/// just want the Bayer mosaic (astronomical stacking, calibration-master
/// generation) can skip it entirely.
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
        // CR3 thumbnail extraction = walk to PRVW / THMB BMFF boxes — wire up when CR3 lands.
        return null;
    }

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
        if (bytes.Length >= 12 && bytes[4] == 'f' && bytes[5] == 't' && bytes[6] == 'y' && bytes[7] == 'p')
        {
            throw new NotImplementedException(
                "CR3 (ISO BMFF + CRX) decoder not yet implemented. CR2 (TIFF + lossless JPEG) works; CR3 is a separate follow-up.");
        }

        throw new InvalidDataException(
            $"Unrecognised container — first 8 bytes: {Convert.ToHexString(bytes[..8])}. " +
            "Expected TIFF (II/MM + 42) for CR2 or ISO BMFF (ftyp box) for CR3.");
    }
}
