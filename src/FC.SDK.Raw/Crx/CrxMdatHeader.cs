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

    /// <summary>One entry per (tile, plane, band) leaf — the unit at which
    /// <see cref="CrxLineDecoder"/> operates.</summary>
    public readonly record struct Subband(
        int TileIndex,
        int PlaneIndex,
        int BandIndex,
        long DataOffset,
        int DataSize,
        int QParam,
        uint Flags);

    /// <summary>Parse the mdat header zone into a flat list of subband
    /// descriptors. The header zone spans
    /// <c>[mdatStart, mdatStart + mdatHdrSize)</c> and is consumed
    /// linearly; data offsets are accumulated past the header end.</summary>
    public static IReadOnlyList<Subband> Parse(
        ReadOnlySpan<byte> bytes, long mdatStart, int mdatHdrSize)
    {
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

        var tileIdx = -1;
        var planeIdx = -1;
        var bandIdx = -1;
        while (pos + 4 <= hdrEnd)
        {
            // Marker is a big-endian uint16 + 2 bytes of (size length? or pad?).
            // Per LibRaw the layout is: 2 bytes marker + 2 bytes size-of-tag
            // (always 8 for the markers we care about) + payload.
            var marker = BinaryPrimitives.ReadUInt16BigEndian(bytes.Slice(pos, 2));
            var tagLen = BinaryPrimitives.ReadUInt16BigEndian(bytes.Slice(pos + 2, 2));
            pos += 4;
            if (tagLen < 4 || pos + tagLen > hdrEnd) break;

            switch (marker)
            {
                case TileMarker:
                case TileMarkerAlt:
                    // FF01/FF11: tile header. Payload[+0..+4] = tile size.
                    // Advance the tile base by the previous tile's size (0
                    // for the first tile, since planeOffsetWithinTile was 0).
                    if (tileIdx >= 0)
                        tileBase += lastTileSize;
                    tileIdx++;
                    lastTileSize = (long)BinaryPrimitives.ReadUInt32BigEndian(bytes.Slice(pos, 4));
                    planeOffsetWithinTile = 0;
                    bandOffsetWithinPlane = 0;
                    planeBase = tileBase;
                    planeIdx = -1;
                    break;
                case PlaneMarker:
                case PlaneMarkerAlt:
                    // FF02/FF12: plane header. Payload[+0..+4] = plane's
                    // total compressed size. Plane base advances by the
                    // PREVIOUS plane's compSize (0 for the first plane).
                    if (planeIdx >= 0)
                    {
                        planeOffsetWithinTile += lastPlaneSize;
                        planeBase = tileBase + planeOffsetWithinTile;
                    }
                    lastPlaneSize = (long)BinaryPrimitives.ReadUInt32BigEndian(bytes.Slice(pos, 4));
                    bandOffsetWithinPlane = 0;
                    planeIdx++;
                    bandIdx = -1;
                    break;
                case BandMarker:
                    bandIdx++;
                    var subbandSize = (int)BinaryPrimitives.ReadUInt32BigEndian(bytes.Slice(pos, 4));
                    var flags = BinaryPrimitives.ReadUInt32BigEndian(bytes.Slice(pos + 4, 4));
                    // bitstream-allowed length = raw size minus tail padding;
                    // qParam exponent for the inverse-quantize step.
                    var padding = (int)(flags & 0x7FFFF);
                    var qParam = (int)((flags >> 19) & 0xFF);
                    subbands.Add(new Subband(
                        TileIndex: tileIdx,
                        PlaneIndex: planeIdx,
                        BandIndex: bandIdx,
                        DataOffset: planeBase + bandOffsetWithinPlane,
                        DataSize: subbandSize - padding,
                        QParam: qParam,
                        Flags: flags));
                    // Band offset advances by RAW subbandSize so consecutive
                    // bands within a plane stay byte-aligned even when one
                    // carries tail padding.
                    bandOffsetWithinPlane += subbandSize;
                    break;
                case BandMarkerNew:
                    // 0xFF13 = qStepBase/qStepMult per-position table. The
                    // levels>0 path can in principle handle it, but our M50
                    // fixtures don't exercise it — we throw with a clear
                    // pointer so any future fixture surfaces the gap immediately
                    // instead of silently mis-parsing.
                    throw new NotImplementedException(
                        $"CRX subband marker 0xFF13 (new qStep table) at byte 0x{pos - 4:X8} not yet supported. " +
                        "Firmware variant outside the M50 (lossless HQ, levels<=3) test set — add a fixture to wire this in.");
                // Unknown markers: skip the payload.
            }
            pos += tagLen;
        }
        return subbands;
    }
}
