using System;
using System.Collections.Generic;
using System.IO;

namespace FC.SDK.Raw.Crx;

/// <summary>
/// Top-level orchestrator for the CRX wavelet+Golomb-Rice codec — the
/// integration layer that glues <see cref="CrxMdatHeader"/> (structural
/// subband table), <see cref="CrxLineDecoder"/> (per-line LOCO-I + Rice
/// entropy decode), and <see cref="Cdf53Wavelet"/> (multi-level inverse
/// wavelet, once levels &gt; 0 lands) into a complete sensor-frame
/// decode that produces the Bayer mosaic ready for downstream pipeline
/// stages (debayer, white balance, colour conversion).
///
/// <para>Phase B.4 ships the encType=0 levels=0 path — Canon "RAW" mode
/// on EOS M-series and similar bodies. The wavelet machinery is already
/// in <see cref="Cdf53Wavelet"/>; level &gt; 0 plumbing through this
/// orchestrator lands in B.5. encType=3 (cRAW lossy) and encType=1
/// (monochrome) need additional pieces (QP-map decode, colour transform)
/// that B.5+ will deliver.</para>
///
/// <para>Format reference: LibRaw <c>src/decoders/crx.cpp</c>
/// (LGPL 2.1) — algorithmic documentation only, clean-room C# port. The
/// plane-to-CFA interleave convention (plane 0 = (0,0), 1 = (0,1),
/// 2 = (1,0), 3 = (1,1) inside the 2×2 Bayer cell) and the median offset
/// trick (encoder subtracts <c>1 &lt;&lt; (bitDepth-1)</c> so residuals
/// stay symmetric around 0; decoder adds it back before clamp) follow
/// Laurent Clévy's <c>canon_cr3</c> notes verified against LibRaw's
/// <c>crxRender</c>.</para>
/// </summary>
internal static class CrxDecoder
{
    /// <summary>Decode the CRX-compressed sensor frame referenced by
    /// <paramref name="header"/> out of the <paramref name="bytes"/> file
    /// span. Returns a freshly-allocated Bayer mosaic of
    /// <c>header.Width × header.Height</c> ushorts; values are clamped
    /// to <c>[0, (1 &lt;&lt; header.BitDepth) - 1]</c> after the median
    /// recentering step.</summary>
    public static ushort[] Decode(ReadOnlySpan<byte> bytes, CrxImageHeader header)
    {
        if (header.EncType != 0)
        {
            // encType=3 (cRAW lossy) and encType=1 (monochrome) both need
            // additional codec stages this orchestrator doesn't yet wire in.
            // Surface a clear message so callers know which fixture-family
            // landed in the unsupported branch.
            throw new NotImplementedException(
                $"CRX encType={header.EncType} not yet supported (Phase B.4 ships encType=0 lossless HQ only). " +
                "encType=3 (cRAW lossy) needs QP-map + colour-transform integration; " +
                "encType=1 (monochrome) needs separate fixture validation.");
        }

        if (header.Levels < 0 || header.Levels > 3)
        {
            // exCoefNumTbl in the wavelet path covers up to 3 levels of CDF 5/3
            // decomposition. Canon's encoders haven't been observed using more,
            // but if a future fixture does, fail loudly rather than walk past
            // the table.
            throw new NotImplementedException(
                $"CRX levels={header.Levels} outside the supported 0..3 range. " +
                "Add a fixture and extend exCoefNumTbl if a body emits more.");
        }

        if (header.PlaneCount != 4)
        {
            // 4 planes is the Bayer-CFA universe (R/G1/G2/B). Bodies that emit
            // monochrome or RGB-packed CRX use a different plane count — we'd
            // need to verify the interleave for those before claiming support.
            throw new NotImplementedException(
                $"CRX planeCount={header.PlaneCount} not supported (4 planes for Bayer CFA required).");
        }

        // The mdat header zone (the first MdatHdrSize bytes of the track's
        // mdat payload) carries the structural 0xFF01/0xFF02/0xFF03 markers;
        // we walk it once to materialise the per-(tile, plane, band) byte
        // ranges and then decode each subband independently.
        var subbands = CrxMdatHeader.Parse(bytes, header.MdatOffset, header.MdatHdrSize);
        if (subbands.Count == 0)
        {
            throw new InvalidDataException(
                "CRX mdat header parse produced no subbands — header zone is malformed or " +
                "MdatHdrSize is wrong (check CMP1[+28..+32] parse).");
        }

        var output = new ushort[(long)header.Width * header.Height];
        var maxValue = (1 << header.BitDepth) - 1;
        // Encoder subtracts the level-shift offset so signed-coded residuals
        // stay symmetric around 0; decoder adds it back before clamping to
        // the bit-depth range. For 14-bit raw data: median = 8192.
        var median = 1 << (header.BitDepth - 1);
        var tilesPerRow = header.TileColumns;
        var tileRows = header.TileRows;
        // Per-plane geometry: each plane carries one quadrant of the Bayer
        // CFA at half resolution, so the plane buffer is tileWidth/2 cols ×
        // tileHeight/2 rows. For M50 RAW.CR3 (tile=3144×4056): 1572×2028.
        var planeWidth = header.TileWidth / 2;
        var planeHeight = header.TileHeight / 2;

        if (header.Levels == 0)
        {
            DecodeLevelsZero(bytes, header, subbands, output, planeWidth, planeHeight,
                tilesPerRow, median, maxValue);
        }
        else
        {
            DecodeLevelsWavelet(bytes, header, subbands, output, planeWidth, planeHeight,
                tilesPerRow, tileRows, median, maxValue);
        }
        return output;
    }

    /// <summary>levels=0 path — single LL band per plane, no wavelet. Each
    /// subband holds the full plane data row-by-row with LOCO-I + run-length
    /// entropy coding. The byte-exact validation against LibRaw lives here.</summary>
    private static void DecodeLevelsZero(
        ReadOnlySpan<byte> bytes, CrxImageHeader header,
        IReadOnlyList<CrxMdatHeader.Subband> subbands,
        ushort[] output, int planeWidth, int planeHeight,
        int tilesPerRow, int median, int maxValue)
    {
        // Scratch line buffer — same shape for every band when levels=0, so
        // we allocate once and reuse across all (tile × plane) pairs.
        var planeLine = new int[planeWidth];

        foreach (var sub in subbands)
        {
            // levels=0 invariant: exactly one band (the LL band, which IS the
            // plane data — no wavelet decomposition). Anything else means
            // the structural parse picked up an extra marker or the codec
            // parameters disagree with what the file actually contains.
            if (sub.BandIndex != 0)
            {
                throw new InvalidDataException(
                    $"CRX levels=0 expects 1 band/plane but mdat header reports bandIndex={sub.BandIndex} " +
                    $"(tile={sub.TileIndex}, plane={sub.PlaneIndex}).");
            }

            // Fresh line decoder per subband: K and S adaptive parameters
            // both reset to 0 at every band boundary per LibRaw's
            // crxParamInit, and the line-history ring is wiped to enforce
            // the synthetic-zero left neighbour rule on the first line.
            var stream = new CrxBitstream(bytes.Slice((int)sub.DataOffset, sub.DataSize));
            var decoder = new CrxLineDecoder(stream, planeWidth);

            var planeYOff = sub.PlaneIndex >> 1;
            var planeXOff = sub.PlaneIndex & 1;
            var tileRow = sub.TileIndex / tilesPerRow;
            var tileCol = sub.TileIndex % tilesPerRow;
            var tileY0 = tileRow * header.TileHeight;
            var tileX0 = tileCol * header.TileWidth;

            for (var py = 0; py < planeHeight; py++)
            {
                if (py == 0) decoder.DecodeTopLine(planeLine);
                else decoder.DecodeLine(planeLine);

                var dstRow = tileY0 + py * 2 + planeYOff;
                if (dstRow >= header.Height) continue;

                var rowBase = (long)dstRow * header.Width;
                for (var px = 0; px < planeWidth; px++)
                {
                    var dstCol = tileX0 + px * 2 + planeXOff;
                    if (dstCol >= header.Width) continue;
                    var v = planeLine[px] + median;
                    if (v < 0) v = 0;
                    else if (v > maxValue) v = maxValue;
                    output[rowBase + dstCol] = (ushort)v;
                }
            }
        }
    }

    /// <summary>levels&gt;0 path — N-level CDF 5/3 wavelet pyramid with
    /// 3*N+1 subbands per plane. Instantiates one
    /// <see cref="CrxWaveletPlaneDecoder"/> per (tile, plane) and pumps
    /// rows out into the Bayer mosaic. Tile edges that share a seam with
    /// another tile carry boundary-extension overlap coefficients so the
    /// inverse lift reconstructs the join cleanly — the per-tile tileFlag
    /// (HasLeft/HasRight/HasTop/HasBottom) is derived here and passed into
    /// the wavelet decoder for its geometry and lift-edge handling.</summary>
    private static void DecodeLevelsWavelet(
        ReadOnlySpan<byte> bytes, CrxImageHeader header,
        IReadOnlyList<CrxMdatHeader.Subband> subbands,
        ushort[] output, int planeWidth, int planeHeight,
        int tilesPerRow, int tileRows, int median, int maxValue)
    {
        var bandsPerPlane = 3 * header.Levels + 1;
        // Group the flat subband list by (tile, plane) so we can hand each
        // plane decoder a sequential 3*N+1 slice. The mdat header walk
        // already emits in tile-major / plane-minor / band-minor order.
        var grouped = new Dictionary<(int Tile, int Plane), List<CrxMdatHeader.Subband>>();
        foreach (var sub in subbands)
        {
            var key = (sub.TileIndex, sub.PlaneIndex);
            if (!grouped.TryGetValue(key, out var list))
            {
                list = new List<CrxMdatHeader.Subband>(bandsPerPlane);
                grouped[key] = list;
            }
            list.Add(sub);
        }

        var planeLine = new int[planeWidth];
        foreach (var ((tileIdx, planeIdx), planeBands) in grouped)
        {
            if (planeBands.Count != bandsPerPlane)
                throw new InvalidDataException(
                    $"CRX tile={tileIdx} plane={planeIdx} has {planeBands.Count} subbands; expected {bandsPerPlane}.");

            var tileRow = tileIdx / tilesPerRow;
            var tileCol = tileIdx % tilesPerRow;
            byte tileFlag = 0;
            if (tilesPerRow > 1)
            {
                if (tileCol < tilesPerRow - 1) tileFlag |= 0x01; // HasRight
                if (tileCol > 0) tileFlag |= 0x02; // HasLeft
            }
            if (tileRows > 1)
            {
                if (tileRow < tileRows - 1) tileFlag |= 0x04; // HasBottom
                if (tileRow > 0) tileFlag |= 0x08; // HasTop
            }

            var plane = new CrxWaveletPlaneDecoder(
                bytes, planeWidth, planeHeight, header.Levels, tileFlag, planeBands);

            var planeYOff = planeIdx >> 1;
            var planeXOff = planeIdx & 1;
            var tileY0 = tileRow * header.TileHeight;
            var tileX0 = tileCol * header.TileWidth;

            for (var py = 0; py < planeHeight; py++)
            {
                plane.DecodeNextRow(planeLine);

                var dstRow = tileY0 + py * 2 + planeYOff;
                if (dstRow >= header.Height) continue;

                var rowBase = (long)dstRow * header.Width;
                for (var px = 0; px < planeWidth; px++)
                {
                    var dstCol = tileX0 + px * 2 + planeXOff;
                    if (dstCol >= header.Width) continue;
                    var v = planeLine[px] + median;
                    if (v < 0) v = 0;
                    else if (v > maxValue) v = maxValue;
                    output[rowBase + dstCol] = (ushort)v;
                }
            }
        }
    }
}
