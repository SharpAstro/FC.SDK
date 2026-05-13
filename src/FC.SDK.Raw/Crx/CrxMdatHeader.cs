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
/// <para>The size field is the BYTE COUNT of the section's compressed
/// payload (not including the structural markers themselves). The data
/// offsets are accumulated: subband[0] starts at <c>mdatStart +
/// mdatHdrSize</c>, subband[i+1] starts at <c>subband[i].DataOffset +
/// subband[i].DataSize</c>.</para>
/// </summary>
internal static class CrxMdatHeader
{
    private const ushort TileMarker = 0xFF01;
    private const ushort PlaneMarker = 0xFF02;
    private const ushort BandMarker = 0xFF03;

    /// <summary>One entry per (tile, plane, band) leaf — the unit at which
    /// <see cref="CrxLineDecoder"/> operates.</summary>
    public readonly record struct Subband(
        int TileIndex,
        int PlaneIndex,
        int BandIndex,
        long DataOffset,
        int DataSize,
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
        // Data offset starts AFTER the header zone — each subband's bytes
        // live in order, butted up against each other.
        var dataOffset = (long)hdrEnd;

        var tileIdx = -1;
        var planeIdx = -1;
        var bandIdx = -1;
        // Each tile/plane has its own band counter
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
                    tileIdx++;
                    planeIdx = -1;
                    // Payload: 4 bytes tile size + 4 bytes reserved. We don't
                    // strictly need tile-total because we accumulate data offsets,
                    // but record it for sanity-check.
                    break;
                case PlaneMarker:
                    planeIdx++;
                    bandIdx = -1;
                    break;
                case BandMarker:
                    bandIdx++;
                    var dataSize = (int)BinaryPrimitives.ReadUInt32BigEndian(bytes.Slice(pos, 4));
                    var flags = BinaryPrimitives.ReadUInt32BigEndian(bytes.Slice(pos + 4, 4));
                    subbands.Add(new Subband(
                        tileIdx, planeIdx, bandIdx,
                        DataOffset: dataOffset, DataSize: dataSize, Flags: flags));
                    dataOffset += dataSize;
                    break;
                // Unknown markers: skip the payload.
            }
            pos += tagLen;
        }
        return subbands;
    }
}
