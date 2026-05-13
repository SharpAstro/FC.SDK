using FC.SDK.Raw.Crx;
using SharpAstro.Exif;
using SharpAstro.Tiff;
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;

namespace FC.SDK.Raw;

/// <summary>
/// Canon CR3 raw-file decoder (Phase A: container walker + metadata + thumbnail).
/// CR3 is an ISO BMFF container with a Canon-specific box vocabulary; this
/// decoder walks the box tree via <see cref="IsoBmffReader"/> and extracts:
/// EXIF (CMT1 + CMT2), MakerNote (CMT3), image dimensions + bit depth + CFA
/// pattern (CMP1), and thumbnail JPEG (PRVW preferred, fallback to THMB).
///
/// <para>The CRX-compressed sensor frame inside the <c>mdat</c> box is NOT
/// decoded in Phase A — <see cref="Decode"/> throws
/// <see cref="NotImplementedException"/> when <c>decodeMosaic=true</c>.
/// Phase B will implement the CRX wavelet + Golomb-Rice codec.</para>
///
/// <para>Format reference: Laurent Clévy's "canon_cr3" reverse-engineering
/// project (<c>github.com/lclevy/canon_cr3</c>) — used as algorithmic
/// documentation only; this implementation is clean-room C# on top of the
/// publicly-documented byte layouts. LibRaw's <c>src/decoders/crx.cpp</c>
/// (LGPL 2.1) is the canonical CRX codec reference but is consulted as a
/// documentation source, not ported.</para>
/// </summary>
internal static class Cr3Decoder
{
    // Canon UUIDs that appear in the CR3 box tree. Values per Laurent's docs
    // and the lclevy/canon_cr3 reference implementation; verified against the
    // EOS M50 sample fixture in FC.SDK.Raw.Tests.
    private static readonly Guid CanonMetadataUuid = new("85c0b687-820f-11e0-8111-f4ce462b6a48");
    private static readonly Guid PreviewContainerUuid = new("eaf42b5e-1c98-4b88-b9fb-b7dc406e4d16");
    // private static readonly Guid XPacketUuid = new("be7acfcb-97a9-42e8-9c71-999491e3afac"); // XMP, not consumed today

    /// <summary>Resolve the CR3 sensor-track header without decoding any
    /// payload. Returns null if the file isn't a CR3 or has no raw track.
    /// Internal — Phase B tests use this to verify the BMFF descent extracts
    /// the expected geometry + codec params before the full mosaic decode
    /// lands. Production callers use <see cref="Decode"/>.</summary>
    internal static CrxImageHeader? TryResolveHeader(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 16) return null;
        if (!(bytes[4] == 'f' && bytes[5] == 't' && bytes[6] == 'y' && bytes[7] == 'p')) return null;
        var ftyp = IsoBmffReader.FindTopLevel(bytes, "ftyp");
        if (ftyp is null) return null;
        var moov = IsoBmffReader.FindTopLevel(bytes, "moov");
        if (moov is not { } m) return null;
        return ResolveRawTrack(bytes, m);
    }

    /// <summary>Decode a CR3 byte stream. <paramref name="decodeMosaic"/> is
    /// the Phase A vs Phase B switch: with <c>false</c>, the BMFF tree is
    /// parsed, EXIF / MakerNote / dimensions / thumbnail are all extracted,
    /// and the returned <see cref="CanonRawFile"/> has an empty
    /// <see cref="CanonRawFile.BayerMosaic"/>; with <c>true</c> (the
    /// production default), throws <see cref="NotImplementedException"/>
    /// after the BMFF + metadata parse succeeds — so callers see container
    /// errors (truncated file, missing CMP1, etc.) before the CRX message.</summary>
    internal static CanonRawFile Decode(ReadOnlySpan<byte> bytes, bool decodeMosaic)
    {
        // ftyp must be the first top-level box and must declare brand "crx ".
        // CanonRaw.FromBytes already gates on the ftyp signature, but we
        // re-check the brand here for the "user opened a non-CR3 BMFF file"
        // case (e.g. MP4 with the same magic).
        var ftyp = IsoBmffReader.FindTopLevel(bytes, "ftyp")
            ?? throw new InvalidDataException("CR3 missing required `ftyp` top-level box");
        var brand = ReadFourCcFromPayload(bytes, ftyp, 0);
        if (brand != "crx ")
            throw new InvalidDataException($"CR3 ftyp brand '{brand}' is not 'crx ' — file is not a Canon CR3");

        var moov = IsoBmffReader.FindTopLevel(bytes, "moov")
            ?? throw new InvalidDataException("CR3 missing required `moov` box");

        // EXIF (CMT1) + ExifIFD (CMT2) + MakerNote (CMT3) all live inside the
        // Canon-metadata UUID box at moov/uuid[85c0b687-...]. Each is a
        // complete TIFF (starts with `II*\0` or `MM\0*`) and can be handed
        // to SharpAstro.Exif.ExifReader directly.
        var canonMeta = FindUuidChild(bytes, moov, CanonMetadataUuid);
        var exif = canonMeta is { } cm ? ParseExifFromCanonMetadata(bytes, cm) : null;
        var makerNote = canonMeta is { } cm2 ? ParseMakerNoteFromCanonMetadata(bytes, cm2) : null;

        // CMP1 + co64 + stsz from the largest trak give us everything the CRX
        // decoder needs: image + tile geometry, codec params, per-track mdat
        // byte range. The proper descent (moov/trak[i]/mdia/minf/stbl/stsd/CRAW/CMP1)
        // is in ResolveRawTrack; it correctly handles the FullBox preamble on
        // stsd and the VisualSampleEntry preamble on CRAW.
        var crxHeader = ResolveRawTrack(bytes, moov)
            ?? throw new InvalidDataException("CR3 has no raw track with CMP1 — sensor dimensions unavailable");

        if (decodeMosaic)
            throw new NotImplementedException(
                $"CR3 CRX wavelet+Golomb-Rice decoder pending (Phase B). " +
                $"Track header parsed: {crxHeader.Width}x{crxHeader.Height} {crxHeader.BitDepth}-bit, " +
                $"encType={crxHeader.EncType} levels={crxHeader.Levels} tile={crxHeader.TileWidth}x{crxHeader.TileHeight} " +
                $"mdat=[{crxHeader.MdatOffset}, {crxHeader.MdatOffset + crxHeader.MdatSize}). " +
                $"Metadata + thumbnail extraction work via `decodeMosaic=false`.");

        return new CanonRawFile(
            Width: crxHeader.Width,
            Height: crxHeader.Height,
            BayerMosaic: Array.Empty<ushort>(),
            BitDepth: crxHeader.BitDepth,
            CfaPattern: crxHeader.Cfa,
            Exif: exif,
            MakerNote: makerNote);
    }

    /// <summary>Extract the largest embedded JPEG preview. CR3 carries up to
    /// two: <c>THMB</c> (160×120, inside moov/canon-metadata-uuid) and
    /// <c>PRVW</c> (1620×1080-ish, inside the top-level
    /// <see cref="PreviewContainerUuid"/>). Prefers PRVW when present
    /// because it's the bigger one; THMB is the fallback.</summary>
    internal static byte[]? ExtractThumbnail(ReadOnlySpan<byte> bytes)
    {
        // PRVW first (the bigger preview).
        foreach (var topBox in IsoBmffReader.ParseTopLevel(bytes))
        {
            if (topBox.Type != "uuid" || topBox.UuidGuid != PreviewContainerUuid) continue;
            // The preview container UUID has 8 bytes of Canon-specific preamble
            // (looks like version+flags+subbox-count, undocumented in BMFF) before
            // its first child box. Then PRVW: 4-byte size + "PRVW" 4CC + 16-byte
            // header (8 bytes version/flags + 4 bytes width + 2 bytes height +
            // 2 bytes size) + JPEG bitstream.
            var prvw = IsoBmffReader.FindChild(bytes, topBox, "PRVW", skipLeadingBytes: 8);
            if (prvw is not { } p) continue;
            var jpeg = ExtractJpegFromBoxPayload(bytes, p, headerBytesBeforeJpeg: 16);
            if (jpeg is not null) return jpeg;
        }

        // THMB fallback.
        var moov = IsoBmffReader.FindTopLevel(bytes, "moov");
        if (moov is not { } m) return null;
        var canonMeta = FindUuidChild(bytes, m, CanonMetadataUuid);
        if (canonMeta is not { } cm) return null;
        var thmb = IsoBmffReader.FindChild(bytes, cm, "THMB");
        if (thmb is not { } t) return null;
        return ExtractJpegFromBoxPayload(bytes, t, headerBytesBeforeJpeg: 16);
    }

    /// <summary>Find an immediate-child <c>uuid</c> box with the given UUID.
    /// Top-level uuid boxes and <c>moov</c>-child uuid boxes both follow this
    /// pattern in CR3.</summary>
    private static IsoBmffReader.Box? FindUuidChild(
        ReadOnlySpan<byte> bytes, IsoBmffReader.Box parent, Guid uuid)
    {
        foreach (var child in IsoBmffReader.ParseChildren(bytes, parent))
        {
            if (child.Type == "uuid" && child.UuidGuid == uuid) return child;
        }
        return null;
    }

    /// <summary>Parse the EXIF (CMT1) sub-box of the Canon metadata UUID box.
    /// CMT1 is a self-contained TIFF starting with the standard byte-order
    /// marker — drop-in for <see cref="ExifReader.FromTiff"/>. Returns null
    /// when the box is absent or fails to parse.</summary>
    private static ExifMetadata? ParseExifFromCanonMetadata(
        ReadOnlySpan<byte> bytes, IsoBmffReader.Box canonMeta)
    {
        var cmt1 = IsoBmffReader.FindChild(bytes, canonMeta, "CMT1");
        if (cmt1 is not { } c) return null;
        var payload = bytes.Slice(c.PayloadOffset, c.PayloadLength);
        return ExifReader.FromTiff(payload);
    }

    /// <summary>Parse the Canon MakerNote (CMT3) sub-box. CMT3 wraps the
    /// MakerNote IFD as a self-contained TIFF (same format as CMT1, except
    /// the IFD0 tags are Canon's MakerNote sub-tags rather than standard
    /// EXIF). We pass it through <see cref="ExifReader.FromTiff"/> to get a
    /// tag dictionary, then translate to <see cref="CanonMakerNote"/>
    /// reusing the same dispatch <see cref="Cr2Decoder"/> uses
    /// (well-known sub-tags 0x0010 ModelId, 0x00E0 SensorInfo,
    /// 0x4001 ColorData, plus the WB_RGGB_LEVELS_AS_SHOT extraction).</summary>
    private static CanonMakerNote? ParseMakerNoteFromCanonMetadata(
        ReadOnlySpan<byte> bytes, IsoBmffReader.Box canonMeta)
    {
        var cmt3 = IsoBmffReader.FindChild(bytes, canonMeta, "CMT3");
        if (cmt3 is not { } c) return null;
        var payload = bytes.Slice(c.PayloadOffset, c.PayloadLength);
        var tiff = ExifReader.FromTiff(payload);
        if (tiff?.RawTags is null) return null;

        // CMT3 byte order from the first two bytes of its TIFF header.
        var fileIsLE = payload.Length >= 2 && payload[0] == 'I' && payload[1] == 'I';

        int? modelId = null;
        int? sensorWidth = null;
        int? sensorHeight = null;
        float[]? colorMatrix = null;
        CanonWhiteBalance? asShotWb = null;
        var subtags = new Dictionary<ushort, byte[]>();

        foreach (var (tag, raw) in tiff.RawTags)
        {
            subtags[tag] = raw.Bytes.ToArray();

            // Same well-known sub-tag dispatch as Cr2Decoder.TryParseMakerNote.
            // The tag IDs come from ExifTool's Canon.pm and are model-agnostic.
            if (tag == 0x0010 && raw.Bytes.Length >= 4)
            {
                modelId = (int)ReadUInt32(raw.Bytes, fileIsLE);
            }
            else if (tag == 0x00E0 && raw.Bytes.Length >= 6)
            {
                // SensorInfo array of SHORTs; indices 1/2 are sensor width/height.
                sensorWidth = ReadUInt16(raw.Bytes.AsSpan(2, 2), fileIsLE);
                sensorHeight = ReadUInt16(raw.Bytes.AsSpan(4, 2), fileIsLE);
            }
            else if (tag == 0x4001 && raw.Bytes.Length >= 18)
            {
                // First 9 shorts form the ColorMatrix1 row-major; values are
                // signed int16 / 1024.0 → float. Same layout as Cr2Decoder.
                colorMatrix = new float[9];
                for (var k = 0; k < 9; k++)
                {
                    var v = (short)ReadUInt16(raw.Bytes.AsSpan(k * 2, 2), fileIsLE);
                    colorMatrix[k] = v / 1024f;
                }
                asShotWb = TryParseColorDataWhiteBalance(raw.Bytes, fileIsLE);
            }
        }

        return new CanonMakerNote(modelId, sensorWidth, sensorHeight, colorMatrix, asShotWb, subtags);
    }

    /// <summary>WB_RGGB_LEVELS_AS_SHOT extractor — identical dispatch to
    /// Cr2Decoder's. CR2 and CR3 share the ColorData sub-tag layout because
    /// it lives entirely inside the MakerNote, independent of the container
    /// format around it.</summary>
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
        var r = ReadUInt16(colorData.Slice(byteOffset + 0, 2), fileIsLE);
        var g1 = ReadUInt16(colorData.Slice(byteOffset + 2, 2), fileIsLE);
        var g2 = ReadUInt16(colorData.Slice(byteOffset + 4, 2), fileIsLE);
        var b = ReadUInt16(colorData.Slice(byteOffset + 6, 2), fileIsLE);
        if (g1 == 0) return null;
        if (r < 100 || r > 20000 || b < 100 || b > 20000) return null;
        var g = (float)g1;
        return new CanonWhiteBalance(r / g, 1.0f, g2 / g, b / g);
    }

    /// <summary>Walk all <c>trak</c> boxes inside <c>moov</c> and return the
    /// fully-resolved <see cref="CrxImageHeader"/> for the sensor-native
    /// raw track (the largest CMP1 by pixel count). Per-track mdat byte
    /// range comes from the track's <c>co64</c> chunk-offset and <c>stsz</c>
    /// sample-size boxes. The path
    /// <c>moov/trak[i]/mdia/minf/stbl/stsd/CRAW/CMP1</c> is the spec-correct
    /// descent — properly handling the 8-byte FullBox preamble on <c>stsd</c>
    /// and the 78-byte VisualSampleEntry preamble on <c>CRAW</c>.</summary>
    private static CrxImageHeader? ResolveRawTrack(ReadOnlySpan<byte> bytes, IsoBmffReader.Box moov)
    {
        CrxImageHeader? best = null;
        long bestPixels = 0;

        foreach (var trak in IsoBmffReader.ParseChildren(bytes, moov))
        {
            if (trak.Type != "trak") continue;
            var header = ParseTrak(bytes, trak);
            if (header is null) continue;
            long pixels = (long)header.Width * header.Height;
            if (pixels > bestPixels)
            {
                bestPixels = pixels;
                best = header;
            }
        }
        return best;
    }

    /// <summary>Parse a single <c>trak</c> box to extract its CRX header.
    /// Returns null when the track doesn't carry a CRAW SampleEntry with
    /// CMP1 (e.g. the metadata track <c>CTMD</c>, or the thumbnail JPEG
    /// track whose SampleEntry is <c>JPEG</c> not <c>CRAW</c>).</summary>
    private static CrxImageHeader? ParseTrak(ReadOnlySpan<byte> bytes, IsoBmffReader.Box trak)
    {
        var mdia = IsoBmffReader.FindChild(bytes, trak, "mdia");
        if (mdia is not { } m) return null;
        var minf = IsoBmffReader.FindChild(bytes, m, "minf");
        if (minf is not { } mf) return null;
        var stbl = IsoBmffReader.FindChild(bytes, mf, "stbl");
        if (stbl is not { } sb) return null;
        var stsd = IsoBmffReader.FindChild(bytes, sb, "stsd");
        if (stsd is not { } sd) return null;

        // stsd is a FullBox carrying an entry count before its child sample
        // entries — descend with the 8-byte preamble skip.
        IsoBmffReader.Box? craw = null;
        foreach (var entry in IsoBmffReader.ParseFullBoxChildren(bytes, sd))
        {
            if (entry.Type == "CRAW") { craw = entry; break; }
        }
        if (craw is not { } cr) return null;

        // CRAW is a VisualSampleEntry; its 78-byte preamble is followed by
        // codec-specific child boxes (CMP1, CDI1, JPEG sometimes).
        var cmp1 = (IsoBmffReader.Box?)null;
        foreach (var entry in IsoBmffReader.ParseVisualSampleEntryChildren(bytes, cr))
        {
            if (entry.Type == "CMP1") { cmp1 = entry; break; }
        }
        if (cmp1 is not { } c) return null;

        var (mdatOffset, mdatSize) = ReadChunkOffsetAndSize(bytes, sb);
        return ParseCmp1(bytes, c, mdatOffset, mdatSize);
    }

    /// <summary>Read the (offset, size) of this track's single sample
    /// (the compressed CRX payload) from <c>co64</c> + <c>stsz</c>.
    /// CR3 tracks emit exactly one sample, so this is a degenerate but
    /// well-defined read: one 64-bit chunk offset + one 32-bit sample size.</summary>
    private static (long Offset, int Size) ReadChunkOffsetAndSize(
        ReadOnlySpan<byte> bytes, IsoBmffReader.Box stbl)
    {
        long offset = 0;
        var size = 0;
        var co64 = IsoBmffReader.FindChild(bytes, stbl, "co64");
        if (co64 is { } co)
        {
            // FullBox: 4 bytes version+flags + 4 bytes entry_count + 8*N offsets.
            // We take the first offset (entry_count is always 1 in CR3).
            var p = co.PayloadOffset;
            offset = (long)BinaryPrimitives.ReadUInt64BigEndian(bytes.Slice(p + 8, 8));
        }
        var stsz = IsoBmffReader.FindChild(bytes, stbl, "stsz");
        if (stsz is { } sz)
        {
            // FullBox: 4 bytes version+flags + 4 bytes sample_size + 4 bytes sample_count.
            // When sample_size != 0, all samples share that size (which is what CR3 emits).
            var p = sz.PayloadOffset;
            size = (int)BinaryPrimitives.ReadUInt32BigEndian(bytes.Slice(p + 4, 4));
        }
        return (offset, size);
    }

    /// <summary>Parse the CMP1 codec-params box into a <see cref="CrxImageHeader"/>.
    /// Field layout per Laurent's docs (offsets relative to box payload start):
    /// +2 header size (short), +4 version (short), +8 image width (uint32),
    /// +12 image height (uint32), +16 tile width (uint32), +20 tile height
    /// (uint32), +24 bits per sample (byte), +25 plane count (high nibble) +
    /// CFA layout (low nibble), +26 encType (high nibble) + image levels
    /// (low nibble), +27 flags, +28 something (subband-related).</summary>
    private static CrxImageHeader ParseCmp1(
        ReadOnlySpan<byte> bytes, IsoBmffReader.Box cmp1, long mdatOffset, int mdatSize)
    {
        var p = cmp1.PayloadOffset;
        var width = (int)BinaryPrimitives.ReadUInt32BigEndian(bytes.Slice(p + 8, 4));
        var height = (int)BinaryPrimitives.ReadUInt32BigEndian(bytes.Slice(p + 12, 4));
        var tileWidth = (int)BinaryPrimitives.ReadUInt32BigEndian(bytes.Slice(p + 16, 4));
        var tileHeight = (int)BinaryPrimitives.ReadUInt32BigEndian(bytes.Slice(p + 20, 4));
        var bitDepth = bytes[p + 24];
        var planeCount = bytes[p + 25] >> 4;
        var cfaByte = bytes[p + 25] & 0xF;
        var encType = bytes[p + 26] >> 4;
        var levels = bytes[p + 26] & 0xF;
        var cfa = cfaByte switch
        {
            0 => CanonCfaPattern.Rggb,
            1 => CanonCfaPattern.Grbg,
            2 => CanonCfaPattern.Gbrg,
            3 => CanonCfaPattern.Bggr,
            _ => CanonCfaPattern.Rggb,
        };
        return new CrxImageHeader(
            Width: width,
            Height: height,
            TileWidth: tileWidth,
            TileHeight: tileHeight,
            BitDepth: bitDepth,
            PlaneCount: planeCount,
            Cfa: cfa,
            EncType: encType,
            Levels: levels,
            MdatOffset: mdatOffset,
            MdatSize: mdatSize);
    }

    /// <summary>Legacy brute-force scan retained for the metadata-only
    /// Phase A code path. Phase B's <see cref="ResolveRawTrack"/> produces
    /// the same width/height/bit-depth/CFA via the proper trak descent
    /// but additionally surfaces tile dimensions and the per-track mdat
    /// byte range. Both produce identical (width, height, bitDepth, cfa)
    /// triples — we'd remove the brute-force scan now but keeping it
    /// as the fallback for malformed files isn't a bad idea.</summary>
    private static (int Width, int Height, int BitDepth, CanonCfaPattern Cfa)? FindLargestCmp1(
        ReadOnlySpan<byte> bytes, IsoBmffReader.Box moov)
    {
        // CMP1 is buried under moov/trak/mdia/minf/stbl/stsd/CRAW. The
        // descent has two BMFF warts: stsd is a FullBox carrying 8 bytes
        // of version/flags+entry_count before child boxes, and CRAW is a
        // VisualSampleEntry with 78 bytes of fixed-shape preamble before
        // its child boxes. Encoding both quirks in the box walker is fiddly
        // and brittle across firmware versions. Pragmatic alternative:
        // brute-force scan the moov payload for the `CMP1` 4CC. The 4CC
        // collision probability against arbitrary data is effectively zero
        // (it's a Canon-coined identifier appearing only as box type), and
        // a single linear pass through a 30 MB CR3 takes microseconds.
        // Phase B will need the proper container walk for tile geometry,
        // but Phase A's "find the largest CMP1" use case is well-served by
        // this shortcut.
        (int Width, int Height, int BitDepth, CanonCfaPattern Cfa)? best = null;
        long bestPixels = 0;
        var moovEnd = moov.PayloadOffset + moov.PayloadLength;
        var pos = moov.PayloadOffset;
        while (pos < moovEnd - 4)
        {
            // ASCII compare against 'C' 'M' 'P' '1' = 0x43 0x4D 0x50 0x31.
            if (bytes[pos] == 0x43 && bytes[pos + 1] == 0x4D
                && bytes[pos + 2] == 0x50 && bytes[pos + 3] == 0x31)
            {
                // The 4-byte box size lives immediately before the type 4CC.
                // Skip "CMP1" hits at the start of moov (impossible since
                // pos starts at moovEnd-ish), and require the layout
                // documented in Laurent's reference.
                if (pos >= 4 && pos + 30 <= moovEnd)
                {
                    var payloadStart = pos + 4; // box-payload starts after the 4-CC type
                    var width = (int)BinaryPrimitives.ReadUInt32BigEndian(bytes.Slice(payloadStart + 8, 4));
                    var height = (int)BinaryPrimitives.ReadUInt32BigEndian(bytes.Slice(payloadStart + 12, 4));
                    var bitDepth = bytes[payloadStart + 24];
                    var cfaByte = bytes[payloadStart + 25];
                    var cfa = (cfaByte & 0xF) switch
                    {
                        0 => CanonCfaPattern.Rggb,
                        1 => CanonCfaPattern.Grbg,
                        2 => CanonCfaPattern.Gbrg,
                        3 => CanonCfaPattern.Bggr,
                        _ => CanonCfaPattern.Rggb,
                    };
                    // Sanity-clamp: a real CMP1 has plausible sensor dimensions
                    // (we expect 1..50_000 in each axis). Garbage matches get
                    // filtered out by this — though the 4CC search makes false
                    // hits very unlikely in practice.
                    if (width > 0 && width < 50000 && height > 0 && height < 50000)
                    {
                        long pixels = (long)width * height;
                        if (pixels > bestPixels)
                        {
                            bestPixels = pixels;
                            best = (width, height, bitDepth, cfa);
                        }
                    }
                }
            }
            pos++;
        }
        return best;
    }

    /// <summary>Slice JPEG bytes out of a box payload that has
    /// <paramref name="headerBytesBeforeJpeg"/> of preamble (BMFF FullBox
    /// version+flags + Canon dimension/length metadata) before the JPEG
    /// SOI. Returns null if the preamble bytes don't lead to <c>FFD8 FFXX</c>
    /// (a valid JPEG start) — defends against payload-layout drift between
    /// camera firmwares.</summary>
    private static byte[]? ExtractJpegFromBoxPayload(
        ReadOnlySpan<byte> bytes, IsoBmffReader.Box box, int headerBytesBeforeJpeg)
    {
        if (box.PayloadLength <= headerBytesBeforeJpeg + 4) return null;
        var jpegStart = box.PayloadOffset + headerBytesBeforeJpeg;
        if (bytes[jpegStart] != 0xFF || bytes[jpegStart + 1] != 0xD8) return null;
        var jpegEnd = box.PayloadOffset + box.PayloadLength;
        return bytes.Slice(jpegStart, jpegEnd - jpegStart).ToArray();
    }

    private static string ReadFourCcFromPayload(ReadOnlySpan<byte> bytes, IsoBmffReader.Box box, int offset)
    {
        Span<char> chars = stackalloc char[4];
        for (var i = 0; i < 4; i++) chars[i] = (char)bytes[box.PayloadOffset + offset + i];
        return new string(chars);
    }

    private static ushort ReadUInt16(ReadOnlySpan<byte> bytes, bool littleEndian)
        => littleEndian
            ? BinaryPrimitives.ReadUInt16LittleEndian(bytes)
            : BinaryPrimitives.ReadUInt16BigEndian(bytes);

    private static uint ReadUInt32(ReadOnlySpan<byte> bytes, bool littleEndian)
        => littleEndian
            ? BinaryPrimitives.ReadUInt32LittleEndian(bytes)
            : BinaryPrimitives.ReadUInt32BigEndian(bytes);
}
