using SharpAstro.Exif;
using SharpAstro.Tiff;
using SharpAstro.Jpeg;
using System;
using System.Collections.Generic;
using System.IO;

namespace FC.SDK.Raw;

/// <summary>
/// Canon CR2 raw-file decoder. CR2 is a TIFF container with multiple IFDs:
/// <list type="bullet">
/// <item>IFD0: thumbnail JPEG + EXIF / MakerNote pointers</item>
/// <item>IFD1: small embedded JPEG (low-res, ~quarter)</item>
/// <item>IFD2: uncompressed RGB preview</item>
/// <item>IFD3: the raw lossless-JPEG payload (compression tag 6) plus the
///   Canon-specific <c>0xC640</c> CR2Slice tag describing the slice layout</item>
/// </list>
/// The IFD3 offset is also stored verbatim in the TIFF header at bytes 12-15,
/// so we don't need to walk the IFD chain just to find the raw — but we walk it
/// anyway to extract Compression, StripOffsets, StripByteCounts, CR2Slice, and
/// ImageWidth / ImageLength values needed for the slice unscramble.
/// </summary>
internal static class Cr2Decoder
{
    private const ushort TagImageWidth = 0x0100;
    private const ushort TagImageLength = 0x0101;
    private const ushort TagCompression = 0x0103;
    private const ushort TagStripOffsets = 0x0111;
    private const ushort TagStripByteCounts = 0x0117;
    private const ushort TagExifIfd = 0x8769;
    private const ushort TagMakerNote = 0x927C;
    private const ushort TagCfaPattern = 0x828D;
    private const ushort TagCr2Slice = 0xC640;

    /// <summary>Decode a CR2 byte stream. Caller has already verified the file
    /// starts with a TIFF header — this method walks IFDs to find the raw payload,
    /// decodes the lossless JPEG, runs the slice unscramble, and assembles the
    /// <see cref="CanonRawFile"/>.</summary>
    internal static CanonRawFile Decode(ReadOnlySpan<byte> bytes)
    {
        var (fileIsLE, ifd0Offset) = Cr2IfdReader.ReadHeader(bytes);
        if (!fileIsLE)
        {
            // No known CR2 emits MM byte order — Canon's encoder is LE-only. Caller can
            // re-fork this decoder if that ever changes; the IFD walker handles MM fine,
            // but lossless-JPEG samples come back in host order regardless.
            throw new InvalidDataException("CR2 with MM (big-endian) byte order is not supported by Canon and not exercised by tests");
        }

        // Walk the full IFD chain. We need IFD0 for EXIF/MakerNote pointers, and
        // we need to find the IFD whose Compression == 6 *and* whose StripByteCounts
        // is largest — that's the raw payload (the smaller compressed-JPEG IFDs use
        // tags 0x0201/0x0202 instead, not StripOffsets/StripByteCounts).
        var ifds = new List<Dictionary<ushort, Cr2IfdReader.Entry>>();
        var nextOffset = ifd0Offset;
        while (nextOffset != 0)
        {
            var ifd = Cr2IfdReader.ParseIfd(bytes, nextOffset, fileIsLE, out nextOffset);
            ifds.Add(ifd);
            if (ifds.Count >= 8) break; // safety stop — CR2 has 4, never more
        }
        if (ifds.Count == 0)
            throw new InvalidDataException("CR2 has no IFDs");

        var ifd0 = ifds[0];
        var rawIfd = FindRawIfd(ifds)
            ?? throw new InvalidDataException("CR2 has no IFD with Compression=6 + StripOffsets — raw payload missing");

        var width = (int)Cr2IfdReader.ScalarValue(rawIfd[TagImageWidth], fileIsLE);
        var height = (int)Cr2IfdReader.ScalarValue(rawIfd[TagImageLength], fileIsLE);
        var stripOffset = (int)Cr2IfdReader.ScalarValue(rawIfd[TagStripOffsets], fileIsLE);
        var stripByteCount = (int)Cr2IfdReader.ScalarValue(rawIfd[TagStripByteCounts], fileIsLE);

        var (sliceCount, sliceWidth, lastSliceWidth) = ReadCr2Slice(rawIfd, fileIsLE)
            ?? (1, width, width);

        // ---- Decode the lossless JPEG payload via SharpAstro.Jpeg (LosslessJpeg) ----
        if (stripOffset < 0 || stripOffset + stripByteCount > bytes.Length)
            throw new InvalidDataException($"CR2 strip offset {stripOffset} + length {stripByteCount} out of bounds");
        var jpegBytes = bytes.Slice(stripOffset, stripByteCount);
        var jpeg = LosslessJpeg.FromMemory(jpegBytes);

        // Total JPEG samples must match output pixel count — the slice unscrambler
        // verifies this too, but the message is clearer with both width × height
        // and (jpeg.Width × jpeg.Height × jpeg.Components) printed.
        var expectedSamples = (long)width * height;
        var jpegSamples = (long)jpeg.Width * jpeg.Height * jpeg.Components;
        if (jpegSamples != expectedSamples)
        {
            throw new InvalidDataException(
                $"CR2 lossless-JPEG sample count {jpegSamples} ({jpeg.Width}×{jpeg.Height}×{jpeg.Components}) " +
                $"≠ expected {expectedSamples} ({width}×{height}). " +
                "CR2Slice tag may be inconsistent with the IFD ImageWidth/ImageLength.");
        }

        var bayer = CanonSliceUnscrambler.Unscramble(
            jpeg.Samples, width, height, sliceCount, sliceWidth, lastSliceWidth);

        // ---- EXIF + MakerNote + CFA pattern ----
        var exif = ExifReader.FromTiff(bytes);
        var makerNote = TryParseMakerNote(exif, bytes, fileIsLE);
        var cfa = ResolveCfaPattern(ifd0, ifds, bytes, fileIsLE);

        return new CanonRawFile(
            Width: width,
            Height: height,
            BayerMosaic: bayer,
            BitDepth: jpeg.Precision,
            CfaPattern: cfa,
            Exif: exif,
            MakerNote: makerNote);
    }

    /// <summary>Extract the embedded preview JPEG from a CR2's IFD0. Returns null
    /// if IFD0 doesn't carry Compression=6 + StripOffsets (no embedded JPEG) or if
    /// the strip pointer is out of range. The returned byte[] is a complete JPEG
    /// file (starts with SOI <c>FFD8</c>) — caller pipes it to any standard JPEG
    /// decoder.</summary>
    internal static byte[]? ExtractThumbnail(ReadOnlySpan<byte> bytes)
    {
        try
        {
            var (fileIsLE, ifd0Offset) = Cr2IfdReader.ReadHeader(bytes);
            var ifd0 = Cr2IfdReader.ParseIfd(bytes, ifd0Offset, fileIsLE, out _);
            if (!ifd0.TryGetValue(TagCompression, out var comp)) return null;
            if (Cr2IfdReader.ScalarValue(comp, fileIsLE) != 6) return null;
            if (!ifd0.TryGetValue(TagStripOffsets, out var soff)) return null;
            if (!ifd0.TryGetValue(TagStripByteCounts, out var slen)) return null;
            var off = (int)Cr2IfdReader.ScalarValue(soff, fileIsLE);
            var len = (int)Cr2IfdReader.ScalarValue(slen, fileIsLE);
            if (off < 0 || off + len > bytes.Length) return null;
            return bytes.Slice(off, len).ToArray();
        }
        catch (InvalidDataException)
        {
            return null;
        }
    }

    /// <summary>The raw payload IFD is the one carrying both Compression=6 and
    /// StripOffsets+StripByteCounts. Walks every IFD because the position varies
    /// across Canon models (usually IFD3 but not load-bearing).</summary>
    private static Dictionary<ushort, Cr2IfdReader.Entry>? FindRawIfd(List<Dictionary<ushort, Cr2IfdReader.Entry>> ifds)
    {
        foreach (var ifd in ifds)
        {
            if (!ifd.TryGetValue(TagCompression, out var comp)) continue;
            if (Cr2IfdReader.ScalarValue(comp, fileIsLE: true) != 6) continue;
            if (!ifd.ContainsKey(TagStripOffsets) || !ifd.ContainsKey(TagStripByteCounts)) continue;
            // Distinguish from the IFD0 thumbnail (also Compression=6 + StripOffsets on some
            // cameras): require the CR2Slice tag, which only the raw IFD carries.
            if (!ifd.ContainsKey(TagCr2Slice)) continue;
            return ifd;
        }
        return null;
    }

    private static (int Count, int SliceWidth, int LastSliceWidth)? ReadCr2Slice(
        Dictionary<ushort, Cr2IfdReader.Entry> ifd, bool fileIsLE)
    {
        if (!ifd.TryGetValue(TagCr2Slice, out var entry)) return null;
        if (entry.Type != TiffFieldType.Short || entry.Count != 3 || entry.Bytes.Length < 6) return null;
        // Tag layout: [slice_count_minus_1, sliceWidth, lastSliceWidth]. Count==1 means
        // a single slice that spans the full width (lastSliceWidth applies).
        var countMinus1 = Cr2IfdReader.ReadShort(entry.Bytes.AsSpan(0, 2), fileIsLE);
        var sliceWidth = Cr2IfdReader.ReadShort(entry.Bytes.AsSpan(2, 2), fileIsLE);
        var lastSliceWidth = Cr2IfdReader.ReadShort(entry.Bytes.AsSpan(4, 2), fileIsLE);
        return (countMinus1 + 1, sliceWidth, lastSliceWidth);
    }

    /// <summary>Best-effort MakerNote parse: locate the Canon MakerNote sub-IFD via
    /// IFD0 tag 0x927C, walk its sub-entries, and preserve them in
    /// <see cref="CanonMakerNote.RawSubtags"/>. Strongly-typed fields
    /// (<c>ModelId</c>, <c>SensorWidth</c>, <c>ColorMatrix</c>) are decoded from
    /// well-known sub-tags when present; null otherwise.</summary>
    /// <summary>Best-effort MakerNote parse. The MakerNote (tag 0x927C) lives in
    /// the Exif sub-IFD, not IFD0 — <see cref="ExifReader"/> follows the 0x8769
    /// pointer for us and exposes it via <c>ExifMetadata.RawTags</c>. The bytes
    /// are the Canon MakerNote IFD payload starting with a u16 entry count
    /// (no header / magic prefix — Canon's MakerNote on CR2 is a plain IFD,
    /// unlike Nikon's which has a nested mini-TIFF).</summary>
    private static CanonMakerNote? TryParseMakerNote(
        ExifMetadata? exif, ReadOnlySpan<byte> tiff, bool fileIsLE)
    {
        if (exif?.RawTags is null) return null;
        if (!exif.RawTags.TryGetValue(0x927C, out var mn)) return null;
        // Canon MakerNote on a CR2 is a plain TIFF IFD starting at the entry's data
        // offset (no header / signature in front of it — unlike Nikon, where the
        // MakerNote has its own embedded mini-TIFF).
        if (mn.Bytes.Length < 2) return null;
        // The entry data is the IFD itself; but for CR2 the MakerNote IFD is at the
        // *offset* given by the entry (because the IFD is larger than 4 bytes —
        // entry.Bytes is already the dereferenced data, so parse it inline).
        // Approach: the MakerNote IFD lives somewhere in the file; we need to find its
        // offset. Cr2IfdReader.ParseIfd already follows offsets through the file, but
        // the entry only carries the dereferenced *bytes* of the IFD payload (which is
        // what we want — a Short-typed sub-tag value lives in those bytes directly).
        //
        // Rather than re-parse the same IFD with the higher-level walker, we just
        // interpret mn.Bytes as an inline IFD: entry count (2) + entries (12 each)
        // + next-IFD pointer (4). Sub-tag value-or-offset references are interpreted
        // relative to the *file*, not the MakerNote IFD, per the CR2 / EXIF spec.
        var subEntryCount = Cr2IfdReader.ReadShort(mn.Bytes.AsSpan(0, 2), fileIsLE);
        var subtags = new Dictionary<ushort, byte[]>();
        int? modelId = null;
        int? sensorWidth = null;
        int? sensorHeight = null;
        float[]? colorMatrix = null;
        CanonWhiteBalance? asShotWb = null;

        for (var i = 0; i < subEntryCount; i++)
        {
            var entryStart = 2 + i * 12;
            if (entryStart + 12 > mn.Bytes.Length) break;
            var tag = Cr2IfdReader.ReadShort(mn.Bytes.AsSpan(entryStart, 2), fileIsLE);
            var type = (TiffFieldType)Cr2IfdReader.ReadShort(mn.Bytes.AsSpan(entryStart + 2, 2), fileIsLE);
            var count = (int)Cr2IfdReader.ReadLong(mn.Bytes.AsSpan(entryStart + 4, 4), fileIsLE);
            var typeSize = FieldTypeSize(type);
            if (typeSize == 0) continue;
            var totalBytes = typeSize * count;
            ReadOnlySpan<byte> data;
            if (totalBytes <= 4)
            {
                data = mn.Bytes.AsSpan(entryStart + 8, totalBytes);
            }
            else
            {
                var off = (int)Cr2IfdReader.ReadLong(mn.Bytes.AsSpan(entryStart + 8, 4), fileIsLE);
                if (off < 0 || off + totalBytes > tiff.Length) continue;
                data = tiff.Slice(off, totalBytes);
            }
            subtags[tag] = data.ToArray();

            // Well-known Canon MakerNote sub-tags. Numbers from ExifTool's Canon.pm.
            if (tag == 0x0010 && type == TiffFieldType.Long && count >= 1)
            {
                // CanonModelID (e.g. 0x80000302 = EOS 6D, 0x80000218 = EOS 5D Mk II)
                modelId = (int)Cr2IfdReader.ReadLong(data, fileIsLE);
            }
            else if (tag == 0x00E0 && type == TiffFieldType.Short && count >= 8)
            {
                // SensorInfo — array of SHORT. Indices 1/2 = sensor width/height
                // (active area, includes margin pixels), 5..8 = active-area crop rect.
                sensorWidth = Cr2IfdReader.ReadShort(data.Slice(2, 2), fileIsLE);
                sensorHeight = Cr2IfdReader.ReadShort(data.Slice(4, 2), fileIsLE);
            }
            else if (tag == 0x4001 && type == TiffFieldType.Short && count >= 9)
            {
                // ColorData — vendor-specific layout that varies by model. Layout
                // versions 5..9 (mid-2010s 14-bit DSLRs: 5D Mk III, 6D, 7D, 70D, …)
                // share a common section: WB_RGGB_LEVELS_AS_SHOT at int16 offset 63
                // (4 shorts: R, G1, G2, B). Earlier ColorData revisions (1..4) and
                // newer ones (10+) put these at different offsets and aren't decoded
                // here — the raw bytes are still available via RawSubtags[0x4001]
                // for callers who want to parse them.
                colorMatrix = new float[9];
                for (var k = 0; k < 9; k++)
                {
                    var raw = (short)Cr2IfdReader.ReadShort(data.Slice(k * 2, 2), fileIsLE);
                    colorMatrix[k] = raw / 1024f;
                }
                asShotWb = TryParseColorDataWhiteBalance(data, fileIsLE);
            }
        }

        return new CanonMakerNote(modelId, sensorWidth, sensorHeight, colorMatrix, asShotWb, subtags);
    }

    /// <summary>Extract WB_RGGB_LEVELS_AS_SHOT from a Canon ColorData payload,
    /// covering every Canon DSLR shipped to date. The dispatch on ColorData
    /// byte-count mirrors dcraw's <c>parse_makernote</c>:
    /// <list type="bullet">
    /// <item>582 bytes → WB at byte 50 (ColorData1: 1D Mark II)</item>
    /// <item>653 bytes → WB at byte 68 (ColorData2: 1Ds Mark II)</item>
    /// <item>5120 bytes → WB at byte 142 (ColorData4-era 1DX / 5DMkIII video)</item>
    /// <item>anything else &gt; 500 bytes → WB at byte 126 (ColorData5..12: the
    ///   2010s+ 14-bit DSLR line — 5D Mk III/IV, 6D / Mk II, 7D / Mk II, 70D,
    ///   77D, 80D, 90D, R-series, M-series, ...)</item>
    /// </list>
    /// Each WB block is 4 successive int16s: R, G1, G2, B. Normalised so
    /// G1 = 1.0 — callers multiply demosaiced channels by these multipliers
    /// to neutralise white.</summary>
    private static CanonWhiteBalance? TryParseColorDataWhiteBalance(
        ReadOnlySpan<byte> colorData, bool fileIsLE)
    {
        if (colorData.Length <= 500) return null;
        var byteOffset = colorData.Length switch
        {
            582 => 50,
            653 => 68,
            5120 => 142,
            _ => 126,
        };
        if (colorData.Length < byteOffset + 8) return null;
        var rRaw = Cr2IfdReader.ReadShort(colorData.Slice(byteOffset + 0, 2), fileIsLE);
        var g1Raw = Cr2IfdReader.ReadShort(colorData.Slice(byteOffset + 2, 2), fileIsLE);
        var g2Raw = Cr2IfdReader.ReadShort(colorData.Slice(byteOffset + 4, 2), fileIsLE);
        var bRaw = Cr2IfdReader.ReadShort(colorData.Slice(byteOffset + 6, 2), fileIsLE);
        // G1 == 0 is Canon's sentinel for "this slot isn't a populated as-shot WB"
        // (some uninitialised template slots in the ColorData table).
        if (g1Raw == 0) return null;
        // Sanity bounds — real-world WB raw values land in the 200..8000 range
        // across tungsten/daylight/shade. Anything wildly outside means we're
        // reading at the wrong offset for this ColorData revision.
        if (rRaw < 100 || rRaw > 20000 || bRaw < 100 || bRaw > 20000) return null;
        var g = (float)g1Raw;
        return new CanonWhiteBalance(rRaw / g, 1.0f, g2Raw / g, bRaw / g);
    }

    /// <summary>Resolve the CFA pattern. CR2 carries it explicitly via EXIF tag
    /// <c>0xA302 CFAPattern</c> (in the EXIF IFD) — a 4-byte array of channel codes
    /// (0=R, 1=G, 2=B) for the 2x2 Bayer block at (0,0). Defaults to RGGB when
    /// absent, which matches every Canon DSLR shipped so far.</summary>
    private static CanonCfaPattern ResolveCfaPattern(
        Dictionary<ushort, Cr2IfdReader.Entry> ifd0,
        List<Dictionary<ushort, Cr2IfdReader.Entry>> ifds,
        ReadOnlySpan<byte> tiff,
        bool fileIsLE)
    {
        if (!ifd0.TryGetValue(TagExifIfd, out var exifPtr)) return CanonCfaPattern.Rggb;
        var exifIfd = Cr2IfdReader.ParseIfd(tiff, (int)Cr2IfdReader.ScalarValue(exifPtr, fileIsLE), fileIsLE, out _);
        if (!exifIfd.TryGetValue(0xA302, out var cfa)) return CanonCfaPattern.Rggb;
        // CFAPattern: 2 SHORTs (cols, rows) + N BYTEs (the 2x2 block) — or, in many
        // Canon files, just 4 BYTEs starting at offset 0. Probe both shapes.
        var bytes = cfa.Bytes;
        var pattern = bytes.Length >= 4
            ? bytes[^4..]
            : (ReadOnlySpan<byte>)stackalloc byte[] { 0, 1, 1, 2 };

        return (pattern[0], pattern[1], pattern[2], pattern[3]) switch
        {
            (0, 1, 1, 2) => CanonCfaPattern.Rggb,
            (2, 1, 1, 0) => CanonCfaPattern.Bggr,
            (1, 0, 2, 1) => CanonCfaPattern.Grbg,
            (1, 2, 0, 1) => CanonCfaPattern.Gbrg,
            _ => CanonCfaPattern.Rggb,
        };
    }

    private static int FieldTypeSize(TiffFieldType type) => type switch
    {
        TiffFieldType.Byte or TiffFieldType.Ascii or TiffFieldType.SByte or TiffFieldType.Undefined => 1,
        TiffFieldType.Short or TiffFieldType.SShort => 2,
        TiffFieldType.Long or TiffFieldType.SLong or TiffFieldType.Float => 4,
        TiffFieldType.Rational or TiffFieldType.SRational or TiffFieldType.Double => 8,
        _ => 0,
    };
}
