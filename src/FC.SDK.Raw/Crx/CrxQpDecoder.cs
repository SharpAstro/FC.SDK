using System;

namespace FC.SDK.Raw.Crx;

/// <summary>
/// Per-tile QP-data stream decoder for lossy cRAW (FF13). Decodes the
/// short Golomb-Rice stream that lives between the mdat structural-marker
/// header zone and the first plane's band data, producing a 2D
/// <c>qpTable[qpHeight × qpWidth]</c> of raw quantization-parameter values.
/// That table is then folded into per-level qStep tables by
/// <see cref="CrxQStep"/>; the wavelet decoder reads from those when
/// inverse-quantizing each band's coefficients.
///
/// <para>The stream itself is a simple top-down LOCO-I scan — no wavelet,
/// no run-length, no zig-zag — distinct from the H-band / LL-band line
/// decoders because the QP signal has different statistics (slowly
/// varying integer field, not natural-image residual energy).
/// Mirrors LibRaw's <c>crxDecodeGolombTop</c> + <c>crxDecodeGolombNormal</c>
/// + <c>crxReadQP</c>.</para>
///
/// <para>Each decoded value is offset by +4 on store — LibRaw's encoder
/// subtracts that bias so the unsigned-coded residuals stay symmetric
/// around 0; the +4 makes the on-disk q-index land back in the range
/// that <c>q_step_tbl</c> expects.</para>
/// </summary>
internal static class CrxQpDecoder
{
    /// <summary>Decode the per-tile QP table. The bitstream span must
    /// contain exactly the tile's QP-data zone (length =
    /// <see cref="CrxMdatHeader.Tile.QpDataSize"/>). Returns a flat
    /// row-major array of length <paramref name="qpWidth"/> *
    /// <paramref name="qpHeight"/>; reshape on the consumer side.</summary>
    public static int[] DecodeTile(
        ReadOnlySpan<byte> bytes, int qpWidth, int qpHeight)
    {
        if (qpWidth <= 0) throw new ArgumentOutOfRangeException(nameof(qpWidth));
        if (qpHeight <= 0) throw new ArgumentOutOfRangeException(nameof(qpHeight));

        var stream = new CrxBitstream(bytes);
        var qpTable = new int[qpHeight * qpWidth];
        // Two ping-pong line buffers padded with 2 extra slots — index 0
        // is the synthetic left-edge sentinel (always 0 / lastSample+1),
        // index [1..qpWidth] holds the actual samples, and index qpWidth+1
        // is the right-edge sentinel for the next row's top-right context
        // read. Total width = qpWidth + 2.
        var ring = new int[2 * (qpWidth + 2)];
        var kParam = 0;
        var writeIdx = 0;

        for (var row = 0; row < qpHeight; row++)
        {
            // Even rows write into the "second" slot, odd into the "first" —
            // LibRaw alternates them so the previous row's buffer is the
            // top context for the current row's prediction.
            var line0Off = (row & 1) != 0 ? qpWidth + 2 : 0;        // previous
            var line1Off = (row & 1) != 0 ? 0 : qpWidth + 2;        // current
            Array.Clear(ring, line1Off, qpWidth + 2);

            if (row == 0)
            {
                DecodeTopLine(stream, qpWidth, ring, line1Off, ref kParam);
            }
            else
            {
                DecodeNormalLine(stream, qpWidth, ring, line0Off, line1Off, ref kParam);
            }

            // qpTable[row, col] = ring[line1Off + col + 1] + 4 — the +4 is
            // LibRaw's bias undo so values land in the 0..63-ish range that
            // q_step_tbl[6] expects.
            for (var col = 0; col < qpWidth; col++)
            {
                qpTable[writeIdx++] = ring[line1Off + col + 1] + 4;
            }
        }
        return qpTable;
    }

    /// <summary>Read one QP symbol. Distinct from the H-band / LL-band
    /// symbol readers: escape threshold is 23 (not 41) and the escape
    /// payload is 8 bits (not 21), reflecting that QP values are bounded
    /// to a small range. Returns the raw bitCode before sign-folding —
    /// the caller folds it via <c>-(qp &amp; 1) ^ (qp &gt;&gt; 1)</c>.</summary>
    private static uint ReadQp(CrxBitstream stream, int kParam)
    {
        var q = stream.GetZeros();
        if (q >= 23)
            return stream.GetBits(8);
        if (kParam > 0)
            return ((uint)q << kParam) | stream.GetBits(kParam);
        return (uint)q;
    }

    /// <summary>Top line of the QP table — no top context, so each sample
    /// just zigzag-extends the running left neighbour. <c>lineBuf[0]</c>
    /// is the synthetic 0 sentinel; samples land at <c>[1..qpWidth]</c>
    /// and <c>lineBuf[qpWidth+1]</c> gets the lastSample+1 sentinel
    /// (matching LibRaw's <c>crxDecodeGolombTop</c>).</summary>
    private static void DecodeTopLine(
        CrxBitstream stream, int width, int[] ring, int lineOff, ref int kParam)
    {
        for (var i = 0; i < width; i++)
        {
            ring[lineOff + i + 1] = ring[lineOff + i];
            var qp = ReadQp(stream, kParam);
            ring[lineOff + i + 1] += -(int)(qp & 1) ^ (int)(qp >> 1);
            kParam = CrxGolombRice.PredictK(kParam, qp, 7);
        }
        ring[lineOff + width + 1] = ring[lineOff + width] + 1;
    }

    /// <summary>Subsequent rows. The 4-way LOCO-I predictor (left, top,
    /// top-right-delta, gradient-sign selector) plus the
    /// <c>(qp + 2*|deltaH|) &gt;&gt; 1</c> K-update rule comes from
    /// LibRaw's <c>crxDecodeGolombNormal</c>.</summary>
    private static void DecodeNormalLine(
        CrxBitstream stream, int width, int[] ring, int line0Off, int line1Off, ref int kParam)
    {
        // ring[line1Off + 0] seeds from the previous row's column 1 — the
        // synthetic left edge of the new row inherits the "above-right"
        // pixel so the first prediction is well-defined.
        ring[line1Off] = ring[line0Off + 1];
        var deltaH = ring[line0Off + 1] - ring[line0Off];
        for (var i = 0; i < width; i++)
        {
            var left = ring[line1Off + i];
            var top = ring[line0Off + i + 1];
            var deltaV = ring[line0Off + i] - left;
            ring[line1Off + i + 1] = Predict(left, top, deltaH, deltaV);
            var qp = ReadQp(stream, kParam);
            ring[line1Off + i + 1] += -(int)(qp & 1) ^ (int)(qp >> 1);
            if (i < width - 1)
            {
                deltaH = ring[line0Off + i + 2] - ring[line0Off + i + 1];
                kParam = CrxGolombRice.PredictK(kParam, (uint)(((int)qp + 2 * Math.Abs(deltaH)) >> 1), 7);
            }
            else
            {
                kParam = CrxGolombRice.PredictK(kParam, qp, 7);
            }
        }
        ring[line1Off + width + 1] = ring[line1Off + width] + 1;
    }

    /// <summary>4-way gradient-direction predictor. Picks between
    /// <c>left + deltaH</c>, <c>left</c>, or <c>top</c> based on the
    /// signs of the local gradient + the left-vs-top relationship.
    /// Mirrors LibRaw's <c>crxPrediction</c> verbatim.</summary>
    private static int Predict(int left, int top, int deltaH, int deltaV)
    {
        // symb[0] = left + deltaH  (continue horizontal trend)
        // symb[1] = left + deltaH  (same — LibRaw stores it twice)
        // symb[2] = left
        // symb[3] = top
        var selector = (((deltaV < 0 ? 1 : 0) ^ (deltaH < 0 ? 1 : 0)) << 1)
                     + ((left < top ? 1 : 0) ^ (deltaH < 0 ? 1 : 0));
        return selector switch
        {
            0 or 1 => left + deltaH,
            2 => left,
            _ => top,
        };
    }
}
