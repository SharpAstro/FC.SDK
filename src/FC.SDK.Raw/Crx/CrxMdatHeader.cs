using System;
using System.Buffers.Binary;
using System.Collections.Generic;

namespace FC.SDK.Raw.Crx;

/// <summary>
/// Parser for the structured header zone at the start of a CR3 mdat
/// track payload. Layout (per LibRaw + Laurent Clévy's docs):
///
/// <code>
/// 0xFF01 [size:4] [reserved:4]      — tile header (1 per tile)
///   0xFF02 [size:4] [reserved:4]    — plane header (1 per plane = 4 planes)
///     0xFF03 [size:4] [flags:4]     — subband header (1 per subband)
///     ...
///   ...
/// ...
/// </code>
///
/// <para>For encType=0 levels=0 (no wavelet): one subband (LL) per plane.
/// For levels=N (1..3): <c>3*N + 1</c> subbands per plane.</para>
///
/// <para>The <c>size</c> field on the <c>0xFF03</c> marker is the BYTE COUNT
/// of the subband's packed payload INCLUDING any tail padding. The actual
/// bitstream that the entropy decoder may consume is shorter — LibRaw subtracts
/// <c>flags &amp; 0x7FFFF</c> from the size to get the bitstream-allowed
/// length. The padding exists so the bitstream's word-aligned 32-bit refill
/// can read past the last meaningful byte without overrunning into the next
/// subband's payload. Data offsets, on the other hand, accumulate by the
/// RAW size so consecutive subbands stay byte-aligned in the mdat.</para>
///
/// <para>The high bits of <c>flags</c> carry <c>qParam</c> (bits 19..26),
/// the per-band quantization scale exponent used by the wavelet decoder's
/// inverse-quantize step. For lossless (encType=0) <c>qParam</c> is normally
/// 4 (which produces a multiplier of 1 — identity). cRAW lossy paths use
/// other values to scale the band's dynamic range.</para>
/// </summary>
internal static class CrxMdatHeader
{
    private const ushort TileMarker = 0xFF01;
    /// <summary>Alternate tile marker used by some firmware revisions —
    /// same 8-byte payload as FF01, structurally interchangeable. (FF11
    /// also supports 16-byte extended payload but our M50 fixtures don't
    /// exercise that; if a future fixture does, the tagLen check forwards
    /// to <c>pos += tagLen</c> as usual and we skip the extension bytes.)</summary>
    private const ushort TileMarkerAlt = 0xFF11;
    private const ushort PlaneMarker = 0xFF02;
    /// <summary>Alternate plane marker — analogous to <see cref="TileMarkerAlt"/>.</summary>
    private const ushort PlaneMarkerAlt = 0xFF12;
    private const ushort BandMarker = 0xFF03;
    // 0xFF13 is the "new" subband header format used by some firmware revisions
    // (16-byte payload, carrying qStepBase + qStepMult instead of qParam).
    // We don't decode it yet — the M50 fixtures use 0xFF03.
    private const ushort BandMarkerNew = 0xFF13;

    /// <summary>Per-tile descriptor. <see cref="HasQpData"/> is set only
    /// when the file uses the 16-byte FF11 tile header (lossy cRAW); in
    /// that case <see cref="QpDataOffset"/>/<see cref="QpDataSize"/> point
    /// at the Golomb-coded per-position QP bitstream that lives between
    /// the mdat header zone and the first plane's band data, and the
    /// per-tile QStep table is derived from it via <c>CrxQStep.Build</c>.
    /// <see cref="QpExtraSize"/> is reserved padding between the QP
    /// stream and the band data (usually 0).</summary>
    public readonly record struct Tile(
        int Index,
        int TileSize,
        bool HasQpData,
        long QpDataOffset,
        int QpDataSize,
        int QpExtraSize);

    /// <summary>One entry per (tile, plane, band) leaf — the unit at which
    /// <see cref="CrxLineDecoder"/> operates.
    /// <para>FF03 (lossless) bands populate <see cref="QParam"/> and leave
    /// <see cref="QStepBase"/>/<see cref="QStepMult"/> at 0. FF13 (per-position
    /// quantization) bands do the inverse — <see cref="QParam"/> is 0
    /// and the qStep params plus the per-tile <c>CrxQStep</c> table drive
    /// the inverse-quantization step in the wavelet decoder. The
    /// <see cref="IsLossy"/> bit lets the wavelet decoder pick its path
    /// without re-checking which marker the band came from.</para></summary>
    public readonly record struct Subband(
        int TileIndex,
        int PlaneIndex,
        int BandIndex,
        long DataOffset,
        int DataSize,
        int QParam,
        int QStepBase,
        int QStepMult,
        bool IsLossy,
        bool SupportsPartial,
        int RoundedBitsMask);

    /// <summary>Aggregate result of <see cref="Parse"/>: per-tile QP-data
    /// pointers (only populated for lossy cRAW) and the flat subband list.</summary>
    public readonly record struct ParseResult(
        IReadOnlyList<Tile> Tiles,
        IReadOnlyList<Subband> Subbands);

    /// <summary>Parse the mdat header zone into the per-tile + per-subband
    /// descriptors. The header zone spans
    /// <c>[mdatStart, mdatStart + mdatHdrSize)</c> and is consumed
    /// linearly. Band data offsets accumulate past the header end and
    /// — for lossy tiles — past the QP-data + extra zones too.</summary>
    public static ParseResult Parse(
        ReadOnlySpan<byte> bytes, long mdatStart, int mdatHdrSize)
    {
        var tiles = new List<Tile>();
        var subbands = new List<Subband>();
        var pos = (int)mdatStart;
        var hdrEnd = pos + mdatHdrSize;
        // Each (tile, plane) gets its own band-offset accumulator that
        // resets to 0 at the FF02 plane marker. The plane-base offset
        // tracks the start of the current plane's data within mdat (and
        // jumps by `compSize` from FF02, not by the sum of subband sizes).
        // This matters because plane boundaries can carry padding that
        // simple subbandSize accumulation misses — for CRAW.CR3 this
        // landed plane 1's bands at the wrong byte offset and the H-band
        // decoder eventually mis-aligned past row ~131.
        var planeBase = (long)hdrEnd;
        // Tile base — for multi-tile files, tile boundaries also have
        // their own size accounting. tileSize from FF01 advances this.
        var tileBase = (long)hdrEnd;
        long planeOffsetWithinTile = 0;
        long bandOffsetWithinPlane = 0;
        long lastTileSize = 0;
        long lastPlaneSize = 0;
        // Per-plane bits extracted from FF02/FF12 byte +4 low nibble (FF12
        // adds non-zero values for lossy cRAW). Cleared at each FF02/FF12;
        // applied to every Subband emitted before the next plane boundary.
        var planeSupportsPartial = false;
        var planeRoundedBitsMask = 0;

        var tileIdx = -1;
        var planeIdx = -1;
        var bandIdx = -1;
        while (pos + 4 <= hdrEnd)
        {
            // Marker is a big-endian uint16 + 2 bytes of tag length. Tag length
            // is 8 for FF01/FF02/FF03/FF12, 16 for the extended FF11 (QP-data
            // flavour) and FF13 (per-position qStep table).
            var marker = BinaryPrimitives.ReadUInt16BigEndian(bytes.Slice(pos, 2));
            var tagLen = BinaryPrimitives.ReadUInt16BigEndian(bytes.Slice(pos + 2, 2));
            pos += 4;
            if (tagLen < 4 || pos + tagLen > hdrEnd) break;

            switch (marker)
            {
                case TileMarker:
                case TileMarkerAlt:
                    // FF01 (8-byte payload) / FF11 (8 or 16 bytes). Layout:
                    //   +0..+3 tileSize
                    //   +4..+5 tile index (must equal curTile)
                    //   +6..+7 tail sign (0 for 8-byte FF01; 0x4000 for FF11/16)
                    // FF11/16 extension (lossy cRAW only):
                    //   +8..+11 mdatQPDataSize
                    //   +12..+13 mdatExtraSize
                    //   +14..+15 terminator (must be 0)
                    if (tileIdx >= 0)
                        tileBase += lastTileSize;
                    tileIdx++;
                    lastTileSize = (long)BinaryPrimitives.ReadUInt32BigEndian(bytes.Slice(pos, 4));
                    var hasQpData = marker == TileMarkerAlt && tagLen == 16;
                    long qpOffset = 0;
                    var qpSize = 0;
                    var qpExtra = 0;
                    var bandDataBase = tileBase;
                    if (hasQpData)
                    {
                        qpSize = (int)BinaryPrimitives.ReadUInt32BigEndian(bytes.Slice(pos + 8, 4));
                        qpExtra = BinaryPrimitives.ReadUInt16BigEndian(bytes.Slice(pos + 12, 2));
                        qpOffset = tileBase;
                        // Band data starts AFTER the QP stream + extra zone.
                        bandDataBase = tileBase + qpSize + qpExtra;
                    }
                    tiles.Add(new Tile(
                        Index: tileIdx,
                        TileSize: (int)lastTileSize,
                        HasQpData: hasQpData,
                        QpDataOffset: qpOffset,
                        QpDataSize: qpSize,
                        QpExtraSize: qpExtra));
                    planeOffsetWithinTile = 0;
                    bandOffsetWithinPlane = 0;
                    planeBase = bandDataBase;
                    planeIdx = -1;
                    break;
                case PlaneMarker:
                case PlaneMarkerAlt:
                    // FF02 (8-byte) / FF12 (8-byte). Layout:
                    //   +0..+3 compSize (this plane's total compressed bytes)
                    //   +4 packed: high nibble = plane index;
                    //              low nibble = roundedBitsMask exponent (bits 1..2 shifted)
                    //                         + supportsPartial flag (bit 3)
                    //   +5..+7 reserved (must be 0)
                    if (planeIdx >= 0)
                    {
                        planeOffsetWithinTile += lastPlaneSize;
                        planeBase = tileBase + planeOffsetWithinTile;
                        // tileBase here means "start of band data" — for lossy
                        // tiles we adjusted planeBase at FF11 to skip QP+extra;
                        // re-apply the same adjustment for each subsequent plane.
                        if (tileIdx >= 0 && tiles[tileIdx].HasQpData)
                        {
                            planeBase += tiles[tileIdx].QpDataSize + tiles[tileIdx].QpExtraSize;
                        }
                    }
                    lastPlaneSize = (long)BinaryPrimitives.ReadUInt32BigEndian(bytes.Slice(pos, 4));
                    var planeFlagsByte = bytes[pos + 4];
                    planeSupportsPartial = (planeFlagsByte & 0x08) != 0;
                    var roundedBits = (planeFlagsByte >> 1) & 0x3;
                    planeRoundedBitsMask = roundedBits == 0 ? 0 : 1 << (roundedBits - 1);
                    bandOffsetWithinPlane = 0;
                    planeIdx++;
                    bandIdx = -1;
                    break;
                case BandMarker:
                    // FF03 (8-byte). Layout:
                    //   +0..+3 subbandSize (bytes the band claims, INCLUDING padding)
                    //   +4..+7 flags packed as bitData:
                    //     bits  0..18 (mask 0x7FFFF) = tail padding
                    //     bits 19..26 = qParam
                    //     bit  27 (0x08000000) = supportsPartial
                    {
                        bandIdx++;
                        var subbandSize = (int)BinaryPrimitives.ReadUInt32BigEndian(bytes.Slice(pos, 4));
                        var flags = BinaryPrimitives.ReadUInt32BigEndian(bytes.Slice(pos + 4, 4));
                        var padding = (int)(flags & 0x7FFFF);
                        var qParam = (int)((flags >> 19) & 0xFF);
                        var bandPartial = (flags & 0x08000000) != 0;
                        subbands.Add(new Subband(
                            TileIndex: tileIdx,
                            PlaneIndex: planeIdx,
                            BandIndex: bandIdx,
                            DataOffset: planeBase + bandOffsetWithinPlane,
                            DataSize: subbandSize - padding,
                            QParam: qParam,
                            QStepBase: 0,
                            QStepMult: 0,
                            IsLossy: false,
                            SupportsPartial: planeSupportsPartial || bandPartial,
                            RoundedBitsMask: planeRoundedBitsMask));
                        bandOffsetWithinPlane += subbandSize;
                    }
                    break;
                case BandMarkerNew:
                    // FF13 (16-byte). Layout:
                    //   +0..+3  subbandSize
                    //   +4..+5  word A: high nibble of byte+4 = subband index,
                    //           low 12 bits must be 0 (partial/qParam unsupported)
                    //   +6..+7  qStepMult (u16)
                    //   +8..+11 qStepBase (i32)
                    //   +12..+13 padding (u16) — dataSize = subbandSize - padding
                    //   +14..+15 terminator (must be 0)
                    {
                        if (tagLen != 16)
                        {
                            throw new InvalidDataException(
                                $"CRX FF13 subband marker at byte 0x{pos - 4:X8} has tagLen={tagLen}, expected 16.");
                        }
                        bandIdx++;
                        var subbandSize = (int)BinaryPrimitives.ReadUInt32BigEndian(bytes.Slice(pos, 4));
                        var wordA = BinaryPrimitives.ReadUInt16BigEndian(bytes.Slice(pos + 4, 2));
                        // High nibble of byte+4 should match band index; low 12
                        // bits are reserved. LibRaw bails on either being wrong.
                        if ((wordA & 0xFFF) != 0)
                        {
                            throw new InvalidDataException(
                                $"CRX FF13 at byte 0x{pos - 4:X8} has non-zero low 12 bits 0x{wordA:X4} — partial/qParam path not supported.");
                        }
                        var qStepMult = BinaryPrimitives.ReadUInt16BigEndian(bytes.Slice(pos + 6, 2));
                        var qStepBase = BinaryPrimitives.ReadInt32BigEndian(bytes.Slice(pos + 8, 4));
                        var padding = BinaryPrimitives.ReadUInt16BigEndian(bytes.Slice(pos + 12, 2));
                        var terminator = BinaryPrimitives.ReadUInt16BigEndian(bytes.Slice(pos + 14, 2));
                        if (terminator != 0)
                        {
                            throw new InvalidDataException(
                                $"CRX FF13 at byte 0x{pos - 4:X8} terminator is 0x{terminator:X4}, expected 0.");
                        }
                        subbands.Add(new Subband(
                            TileIndex: tileIdx,
                            PlaneIndex: planeIdx,
                            BandIndex: bandIdx,
                            DataOffset: planeBase + bandOffsetWithinPlane,
                            DataSize: subbandSize - padding,
                            QParam: 0,
                            QStepBase: qStepBase,
                            QStepMult: qStepMult,
                            IsLossy: true,
                            SupportsPartial: planeSupportsPartial,
                            RoundedBitsMask: planeRoundedBitsMask));
                        bandOffsetWithinPlane += subbandSize;
                    }
                    break;
                // Unknown markers: skip the payload.
            }
            pos += tagLen;
        }
        return new ParseResult(tiles, subbands);
    }
}
