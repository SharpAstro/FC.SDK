using System;

namespace FC.SDK.Raw.Crx;

/// <summary>
/// Per-line CRX entropy decoder. Decodes one row of a subband at a time
/// from the bitstream into a pre-allocated <c>int[]</c> line buffer using
/// LOCO-I-style median prediction + Rice symbol coding + run-length escape
/// (the same algorithmic family as JPEG-LS).
///
/// <para>State carried across lines: previous-line buffer (for top
/// predictor context), current-line ring (lineBuf0 / lineBuf1 / lineBuf2
/// — three rows of ping-pong storage with 1-sample border on each side
/// for the median predictor's left/right neighbour access), the adaptive
/// K parameter (Rice quotient bit-count), and the adaptive S parameter
/// (run-length index into the JPEG-LS J/JS tables).</para>
///
/// <para>Phase B.4 supports the standard <see cref="DecodeTopLine"/> +
/// <see cref="DecodeLine"/> pair used by encType=0 (lossless HQ) at any
/// wavelet level. The "Rounded" + "NoRefPrevLine" variants from LibRaw
/// are not implemented — they cover encType=3 (cRAW lossy) and partial-
/// precision modes that need a separate fixture to validate.</para>
/// </summary>
internal sealed class CrxLineDecoder
{
    private readonly CrxBitstream _stream;
    private readonly CrxGolombRice _rice;
    /// <summary>Previous-line buffer with 2-sample left padding + 1-sample
    /// right padding. Layout in memory: <c>[pad_left, pad_left, row[0..N-1], pad_right]</c>.
    /// The padding lets the median predictor's neighbour accesses
    /// (lineBuf0[-1], lineBuf0[0], lineBuf0[1]) never need bounds checks.</summary>
    private int[] _prevLine = [];
    /// <summary>Current-line buffer, same layout convention as <see cref="_prevLine"/>.</summary>
    private int[] _currLine = [];
    /// <summary>Active width (excluding padding). Equals the subband width
    /// at the level being decoded. For encType=0 levels=0 plane data this
    /// equals <c>tileWidth / 2</c> (per-plane subsampled width).</summary>
    public int Width { get; }

    public CrxLineDecoder(CrxBitstream stream, int width)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (width <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        _stream = stream;
        _rice = new CrxGolombRice(maxK: 15);
        Width = width;
        // Allocate line buffers with padding on each side. The +3 left and
        // +1 right matches LibRaw's CrxBandParam.lineBuf0/1 layout: the
        // predictor uses indices [-1, 0, 1, 2] relative to the current
        // position, so we need room on both sides.
        _prevLine = new int[width + 4];
        _currLine = new int[width + 4];
    }

    /// <summary>K parameter (Rice quotient bit-count). Exposed for test
    /// inspection; the decoder updates it after each symbol.</summary>
    public int KParam
    {
        get => _rice.KParam;
        set => _rice.KParam = value;
    }

    /// <summary>S parameter (run-length index). Adapts on every
    /// run-of-N transition.</summary>
    public int SParam
    {
        get => _rice.SParam;
        set => _rice.SParam = value;
    }

    /// <summary>Decode the first line of a subband into
    /// <paramref name="destination"/>. No previous-line context yet
    /// (predicted value is just the left neighbour, defaulting to 0 at
    /// the start of the line). After the call, <paramref name="destination"/>
    /// holds <see cref="Width"/> ints — the raw Rice-decoded coefficients,
    /// folded back to signed values via zigzag.</summary>
    public void DecodeTopLine(Span<int> destination)
    {
        if (destination.Length < Width)
            throw new ArgumentException("destination too small for line width", nameof(destination));

        // Reset state for a new band: K starts at 0 (per LibRaw's crxParamInit),
        // S starts at 0, both line buffers zeroed (padding intact).
        Array.Clear(_currLine);
        _currLine[2] = 0; // synthetic left-of-line; predictor reads currLine[idx] where idx starts at 2

        // The current-position pointer walks _currLine starting at index 2
        // (the first actual sample slot, after the 2-byte left padding).
        var pos = 2;
        var length = Width;

        while (length > 1)
        {
            // Run-length escape: when the predicted value (current left
            // neighbour) is zero, the encoder may have emitted a run-of-zeros
            // marker bit. Bit 0 = "no run, decode this symbol normally"; bit 1
            // = "run follows, read run-length encoding".
            if (_currLine[pos] != 0)
            {
                // Non-zero left neighbour → no run escape, predicted = left.
                _currLine[pos + 1] = _currLine[pos];
            }
            else
            {
                // Left neighbour is zero — possibly a run.
                var nSyms = 0;
                if (_stream.GetBits(1) != 0)
                {
                    // Start a run. Each extra "1" bit doubles the suspected
                    // run length (with JS[s] step counts).
                    nSyms = 1;
                    while (_stream.GetBits(1) != 0)
                    {
                        nSyms += (int)CrxGolombRice.JS[_rice.SParam];
                        if (nSyms > length) { nSyms = length; break; }
                        if (_rice.SParam < 31) _rice.SParam++;
                        if (nSyms == length) break;
                    }
                    // Refinement bits: J[s] LSB bits to add to the run count.
                    if (nSyms < length)
                    {
                        if (CrxGolombRice.J[_rice.SParam] != 0)
                            nSyms += (int)_stream.GetBits((int)CrxGolombRice.J[_rice.SParam]);
                        if (_rice.SParam > 0) _rice.SParam--;
                        if (nSyms > length)
                            throw new InvalidOperationException("CRX run-length overrun in top-line decode");
                    }
                    length -= nSyms;
                    // Copy left value forward nSyms times — the run produced
                    // a sequence of identical zeros.
                    while (nSyms-- > 0)
                    {
                        _currLine[pos + 1] = _currLine[pos];
                        pos++;
                    }
                    if (length <= 0) break;
                }
                // No (or end of) run — predicted value is 0 (left was 0).
                _currLine[pos + 1] = 0;
            }

            // Rice-decode the residual error, sign-fold, and add to the
            // predicted value. KParam adapts inside DecodeSymbol.
            var bitCode = _rice.DecodeSymbol(_stream);
            _currLine[pos + 1] += CrxGolombRice.FoldSign(bitCode);
            pos++;
            length--;
        }

        // Final sample (no notEOL gate).
        if (length == 1)
        {
            _currLine[pos + 1] = _currLine[pos];
            var bitCode = _rice.DecodeSymbol(_stream);
            _currLine[pos + 1] += CrxGolombRice.FoldSign(bitCode);
            pos++;
        }

        // Sentinel at end-of-line: prev_line[end+1] = prev_line[end] + 1.
        // Used by the next line's median predictor to detect the
        // out-of-bounds right neighbour cleanly.
        _currLine[pos + 1] = _currLine[pos] + 1;

        // Copy the active region (excluding padding) to the caller's buffer.
        _currLine.AsSpan(2, Width).CopyTo(destination);

        // Swap currLine -> prevLine for the next line's predictor context.
        (_prevLine, _currLine) = (_currLine, _prevLine);
    }

    /// <summary>Decode a non-top line into <paramref name="destination"/>.
    /// Uses LOCO-I-style median prediction with full 4-neighbour context
    /// (left, top, top-left, top-right) and the run-length escape kicks in
    /// when current-left == top-current == top-right (flat region).</summary>
    public void DecodeLine(Span<int> destination)
    {
        if (destination.Length < Width)
            throw new ArgumentException("destination too small for line width", nameof(destination));

        Array.Clear(_currLine);
        // Seed currLine[2] with prevLine[3] = the top neighbour of the first
        // sample (per LibRaw: `param->lineBuf1[0] = param->lineBuf0[1]`).
        _currLine[2] = _prevLine[3];

        var pos = 2;          // current-line position
        var topPos = 2;       // previous-line position
        var length = Width;

        while (length > 1)
        {
            // Run-length detection: only flat regions (current-left == top ==
            // top-right) collapse to a run. Otherwise decode a residual.
            if (_currLine[pos] != _prevLine[topPos + 1] || _currLine[pos] != _prevLine[topPos + 2])
            {
                DecodeSymbolL1(pos, topPos, doMedianPrediction: true, notEOL: true);
                pos++;
                topPos++;
                length--;
            }
            else
            {
                var nSyms = 0;
                if (_stream.GetBits(1) != 0)
                {
                    nSyms = 1;
                    while (_stream.GetBits(1) != 0)
                    {
                        nSyms += (int)CrxGolombRice.JS[_rice.SParam];
                        if (nSyms > length) { nSyms = length; break; }
                        if (_rice.SParam < 31) _rice.SParam++;
                        if (nSyms == length) break;
                    }
                    if (nSyms < length)
                    {
                        if (CrxGolombRice.J[_rice.SParam] != 0)
                            nSyms += (int)_stream.GetBits((int)CrxGolombRice.J[_rice.SParam]);
                        if (_rice.SParam > 0) _rice.SParam--;
                        if (nSyms > length)
                            throw new InvalidOperationException("CRX run-length overrun in line decode");
                    }
                    length -= nSyms;
                    topPos += nSyms;
                    while (nSyms-- > 0)
                    {
                        _currLine[pos + 1] = _currLine[pos];
                        pos++;
                    }
                }
                if (length > 0)
                {
                    DecodeSymbolL1(pos, topPos, doMedianPrediction: false, notEOL: length > 1);
                    pos++;
                    topPos++;
                    length--;
                }
            }
        }

        if (length == 1)
        {
            DecodeSymbolL1(pos, topPos, doMedianPrediction: true, notEOL: false);
            pos++;
        }

        _currLine[pos + 1] = _currLine[pos] + 1;
        _currLine.AsSpan(2, Width).CopyTo(destination);
        (_prevLine, _currLine) = (_currLine, _prevLine);
    }

    /// <summary>Decode one residual symbol at <paramref name="pos"/> with
    /// LOCO-I median prediction context from <paramref name="topPos"/> in
    /// the previous line. Mirrors LibRaw's <c>crxDecodeSymbolL1</c>.</summary>
    private void DecodeSymbolL1(int pos, int topPos, bool doMedianPrediction, bool notEOL)
    {
        int predicted;
        if (doMedianPrediction)
        {
            // LOCO-I median predictor: four context samples produce a
            // gradient-based prediction. delta = top - top-left.
            var delta = _prevLine[topPos + 1] - _prevLine[topPos];
            var topMid = _prevLine[topPos + 1];
            Span<int> symb = stackalloc int[4];
            symb[2] = _currLine[pos];        // left
            symb[0] = symb[1] = delta + symb[2]; // left + gradient
            symb[3] = topMid;                 // top
            // Branch selection per LibRaw: bit 1 = (topLeft<top) XOR (delta<0);
            // bit 0 = (top<topRight) XOR (delta<0). Picks one of the 4 symbs.
            var topLeft = _prevLine[topPos];
            var topRight = _prevLine[topPos + 2];
            var idx = (((topLeft < topMid ? 1 : 0) ^ (delta < 0 ? 1 : 0)) << 1)
                    | ((topMid < topRight ? 1 : 0) ^ (delta < 0 ? 1 : 0));
            predicted = symb[idx];
        }
        else
        {
            // Run continuation: predicted = top.
            predicted = _prevLine[topPos + 1];
        }

        var bitCode = _rice.DecodeSymbol(_stream);
        _currLine[pos + 1] = predicted + CrxGolombRice.FoldSign(bitCode);

        // For non-end-of-line, peek at the next gradient to bias the K update.
        // LibRaw averages the bitCode with abs(next-delta) before adapting K.
        if (notEOL)
        {
            var nextDelta = (_prevLine[topPos + 2] - _prevLine[topPos + 1]) << 1;
            var nextAbs = (uint)Math.Abs(nextDelta);
            // The 2nd K-update arg is the smoothed bitCode; we replicate
            // LibRaw's formula (bitCode + 2*abs(nextDelta)) >> 1.
            // Already-updated KParam from DecodeSymbol → re-update with the
            // smoothed magnitude is intentional (matches LibRaw exactly).
            var smoothed = (bitCode + 2 * nextAbs) >> 1;
            _rice.KParam = ClampedKUpdate(_rice.KParam, smoothed, maxK: 15);
        }
    }

    /// <summary>K-parameter update with the same formula
    /// <see cref="CrxGolombRice.DecodeSymbol"/> uses, but applied a second
    /// time with the smoothed magnitude in the non-EOL case. Replicates
    /// LibRaw's K-prediction re-call inside the inner symbol loop.</summary>
    private static int ClampedKUpdate(int prevK, uint bitCode, int maxK)
    {
        var newK = prevK
            - (bitCode < (1u << prevK >> 1) ? 1 : 0)
            + ((bitCode >> prevK) > 2 ? 1 : 0)
            + ((bitCode >> prevK) > 5 ? 1 : 0);
        return newK >= maxK ? maxK : newK;
    }
}
