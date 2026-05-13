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
    /// <summary>Previous-line buffer. Layout matches LibRaw's
    /// <c>CrxBandParam.lineBuf0</c>: 1 left-padding sample at index 0,
    /// then <see cref="Width"/> decoded samples at indices [1..Width],
    /// then a 1-sample sentinel slot at index Width+1 (set to
    /// <c>lastSample + 1</c> after each line, so the next line's median
    /// predictor sees a distinct top-right when reading past the row end).
    /// We allocate Width+3 to cover the worst-case top-right gradient
    /// lookahead at <c>topPos + 2</c> when topPos = Width.</summary>
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
        // Buffer size = Width + 2: 1 left-pad slot at index 0, then Width
        // sample slots at indices 1..Width, then the sentinel slot at index
        // Width+1. Matches LibRaw's `lineLength = subbandWidth + 2`. The
        // gradient lookahead at topPos+2 stays in-bounds because the loop
        // exits before topPos > Width-2 (the length==1 EOL case doesn't
        // touch topPos+2).
        _prevLine = new int[width + 2];
        _currLine = new int[width + 2];
    }

    /// <summary>0-based count of rows produced. Drives the top-line /
    /// non-top-line dispatch when callers use the unified
    /// <see cref="DecodeNextRow"/> entry point (wavelet pump path).</summary>
    public int CurLine { get; private set; }

    /// <summary>Decode one row into <paramref name="destination"/>. Top-line
    /// vs subsequent-line dispatch is internal — the wavelet pump just
    /// pulls one row per band per pump step without tracking phase.</summary>
    public void DecodeNextRow(Span<int> destination)
    {
        if (CurLine == 0)
            DecodeTopLine(destination);
        else
            DecodeLine(destination);
        CurLine++;
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

        // Zero the buffer — _currLine[0] is the synthetic left-of-line slot
        // for the predictor (initially 0 since top-line has no left context).
        // K and S adaptive parameters live on _rice; this decoder is built
        // per-band so they're already 0 from the constructor.
        Array.Clear(_currLine);

        // pos = index of the "current left" slot (matches LibRaw's
        // lineBuf1+0 pointer offset). Loop body writes the new sample to
        // _currLine[pos+1] and then advances pos++. With pos starting at 0,
        // the first sample lands at _currLine[1] and the last at _currLine[Width].
        var pos = 0;
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
        // out-of-bounds right neighbour cleanly (lastSample + 1 is
        // guaranteed != lastSample, which forces the run-length flat check
        // to fail at the row boundary).
        _currLine[pos + 1] = _currLine[pos] + 1;

        // Copy the active region (excluding padding) to the caller's buffer.
        // Samples land at indices 1..Width; destination wants Width ints.
        _currLine.AsSpan(1, Width).CopyTo(destination);

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

        // No Array.Clear needed — every cell of _currLine[1..Width] is
        // written below (DecodeSymbolL1, run-fill loop, or final sample);
        // _currLine[0] is set explicitly on the next line; and the right
        // sentinel _currLine[Width+1] is set at end-of-line via
        // `_currLine[pos + 1] = _currLine[pos] + 1`. The buffer arrives stale
        // from two rows ago via the ping-pong swap, which is harmless when
        // every cell gets overwritten before any read.
        //
        // Seed _currLine[0] (the synthetic-left slot for the first sample)
        // with _prevLine[1] = the top neighbour of the first sample. This
        // mirrors LibRaw's `param->lineBuf1[0] = param->lineBuf0[1];` —
        // the first sample's "current left" predictor input is the value
        // directly above it from the prev line, so an identical-value
        // column tracks cleanly through the run-length flat detector.
        _currLine[0] = _prevLine[1];

        var pos = 0;          // current-line position (LibRaw lineBuf1+0)
        var topPos = 0;       // previous-line position (LibRaw lineBuf0+0)
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
        // Samples occupy _currLine[1..Width] (offset 1 after the 1-byte left
        // pad). DecodeTopLine uses the same CopyTo range; both must match
        // the buffer layout LibRaw's lineBuf1[1..W] convention establishes.
        _currLine.AsSpan(1, Width).CopyTo(destination);
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
            // LOCO-I median predictor: four candidate predictions chosen by
            // a gradient-based context. delta = top - top-left captures the
            // horizontal trend in the previous line; sign of delta toggles
            // the context-quadrant bits below. Per LibRaw's crxDecodeSymbolL1.
            var topLeft = _prevLine[topPos];
            var topMid = _prevLine[topPos + 1];
            var curLeft = _currLine[pos];
            var delta = topMid - topLeft;
            Span<int> symb = stackalloc int[4];
            symb[2] = curLeft;
            symb[0] = symb[1] = delta + curLeft;
            symb[3] = topMid;
            // Branch selection: bit 1 = (topLeft < curLeft) XOR (delta<0);
            // bit 0 = (curLeft < topMid) XOR (delta<0). Note that ALL THREE
            // comparisons involve curLeft — the current-line's left neighbour
            // is the swing vote between the four candidate predictions.
            // (An earlier version of this code used (topMid<topRight) which
            // looks similar but doesn't bring in the current-line context and
            // is wrong.)
            var idx = (((topLeft < curLeft ? 1 : 0) ^ (delta < 0 ? 1 : 0)) << 1)
                    | ((curLeft < topMid ? 1 : 0) ^ (delta < 0 ? 1 : 0));
            predicted = symb[idx];
        }
        else
        {
            // Run continuation: predicted = top neighbour.
            predicted = _prevLine[topPos + 1];
        }

        // Two-stage decode so we can override bitCode before the K update:
        // ReadBitCode pulls the Rice/unary symbol but doesn't touch K;
        // AdaptK below performs the single update with the (possibly
        // smoothed) bitCode. Matches LibRaw's crxDecodeSymbolL1 exactly:
        // K is predicted once per symbol, using the smoothed magnitude when
        // we're not at end-of-line.
        var bitCode = _rice.ReadBitCode(_stream);
        _currLine[pos + 1] = predicted + CrxGolombRice.FoldSign(bitCode);

        if (notEOL)
        {
            // Look one sample ahead in the previous line to estimate the
            // next symbol's magnitude. nextDelta is pre-shifted by 1 (so it's
            // 2 * (topRight - topMid)) and abs() folds into a uint magnitude
            // already weighted x2 — matches LibRaw's
            // `(bitCode + _abs(nextDelta)) >> 1` exactly.
            var nextDelta = (_prevLine[topPos + 2] - _prevLine[topPos + 1]) << 1;
            var nextAbs = (uint)Math.Abs(nextDelta);
            bitCode = (bitCode + nextAbs) >> 1;
        }
        _rice.AdaptK(bitCode);
    }
}
