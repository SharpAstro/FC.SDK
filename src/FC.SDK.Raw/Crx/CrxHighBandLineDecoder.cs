using System;

namespace FC.SDK.Raw.Crx;

/// <summary>
/// CRX per-line entropy decoder for the wavelet H-bands (HL / LH / HH at
/// each decomposition level). These bands are zero-centred by construction
/// — the wavelet's high-pass output is signed residual energy around 0,
/// not natural-image samples — so they use a fundamentally different code
/// path from <see cref="CrxLineDecoder"/> (which handles the LL band with
/// LOCO-I-style median prediction).
///
/// <para>Mirrors LibRaw's <c>crxDecodeTopLineNoRefPrevLine</c> +
/// <c>crxDecodeLineNoRefPrevLine</c> exactly. Key differences from the LL
/// decoder:</para>
/// <list type="bullet">
/// <item>No median predictor — predicted value is 0; <c>lineBuf1</c> stores
///   raw coefficients (= signed residuals).</item>
/// <item>Run-flat trigger uses three context samples instead of one: the
///   top-right + top + current-left from the previous + current lines.
///   Only when ALL THREE are zero does the encoder consider a run.</item>
/// <item>The first symbol after a run is decoded with a <c>(bitCode + 1)</c>
///   zigzag fold instead of <c>bitCode</c>, so the smallest representable
///   value is +/-1 (the encoder would have continued the run for 0).</item>
/// <item>Per-position K parameter via <c>lineBuf2</c>: each position stores
///   its current K, and the next line's decoder reads <c>lineBuf2[i+1]</c>
///   one position AHEAD to bias the adaptive K toward consistent values
///   along vertical edges. The in-flight K update is UNCAPPED
///   (<see cref="CrxGolombRice.PredictK"/> with <c>maxK=0</c>); the
///   per-position adjust then caps at 15 conditionally.</item>
/// </list>
///
/// <para>The "last-symbol" branches (i == width-1) use the capped
/// <c>maxK=15</c> form because there's no <c>lineBuf2[i+1]</c> ahead to
/// reference for the per-position adjust.</para>
/// </summary>
internal sealed class CrxHighBandLineDecoder
{
    private readonly CrxBitstream _stream;
    /// <summary>Active subband width (excluding padding). Equals the
    /// per-band coefficient count along the row.</summary>
    public int Width { get; }

    /// <summary>Previous-line samples. Layout mirrors LibRaw's
    /// <c>lineBuf0</c>: 1-byte left pad at index 0, samples at [1..Width],
    /// sentinel at index Width+1 (left at 0 for H-bands; the next line's
    /// context check at <c>lineBuf0[i+2]</c> needs only "non-zero" detection,
    /// not the lastSample+1 distinct-value trick the LL band uses).</summary>
    private int[] _prevLine = [];
    /// <summary>Current-line samples being written this row. Ping-pongs
    /// with <see cref="_prevLine"/> at end-of-line.</summary>
    private int[] _currLine = [];
    /// <summary>Per-position K parameter history. Size <see cref="Width"/>
    /// — each cell holds the K parameter that was active when the same
    /// position was decoded on the previous line. Updated in-place as the
    /// current line writes (the "one position ahead" read into the old K
    /// happens before the same index is overwritten).</summary>
    private readonly int[] _kHist;

    private int _kParam;
    private int _sParam;

    /// <summary>0-based count of how many rows this decoder has produced.
    /// Drives the top-line / non-top-line dispatch.</summary>
    public int CurLine { get; private set; }

    public CrxHighBandLineDecoder(CrxBitstream stream, int width)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (width <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        _stream = stream;
        Width = width;
        _prevLine = new int[width + 2];
        _currLine = new int[width + 2];
        _kHist = new int[width];
        _kParam = 0;
        _sParam = 0;
    }

    /// <summary>Decode one row into <paramref name="destination"/>. Top-line
    /// vs subsequent-line dispatch is internal; the caller just pumps rows.</summary>
    public void DecodeNextRow(Span<int> destination)
    {
        if (CurLine == 0)
            DecodeTopLine(destination);
        else
            DecodeLine(destination);
        CurLine++;
    }

    /// <summary>Read one bare Rice/Golomb-Rice symbol using the current K.
    /// Distinct from <see cref="CrxGolombRice.ReadBitCode"/> only in that
    /// it operates on this decoder's K field — the H-band decoder's K
    /// adaptation rules differ enough from the LL decoder's that sharing
    /// the <see cref="CrxGolombRice"/> instance would obscure the logic.</summary>
    private uint ReadSymbol()
    {
        var q = _stream.GetZeros();
        if (q >= 41)
            return _stream.GetBits(21);
        if (_kParam > 0)
            return ((uint)q << _kParam) | _stream.GetBits(_kParam);
        return (uint)q;
    }

    /// <summary>First-line of a band. No previous-line context exists yet,
    /// so the run-flat test reduces to "is the current left neighbour
    /// (<c>lineBuf1[pos]</c>) zero?" — equivalent to LibRaw's
    /// <c>crxDecodeTopLineNoRefPrevLine</c>.</summary>
    private void DecodeTopLine(Span<int> destination)
    {
        Array.Clear(_currLine);
        Array.Clear(_kHist);

        var pos = 0;     // lineBuf1 write cursor — writes to _currLine[pos+1]
        var kPos = 0;    // lineBuf2 write cursor
        var length = Width;

        while (length > 1)
        {
            if (_currLine[pos] != 0)
            {
                // Left neighbour non-zero → no run check; decode normal.
                var bitCode = ReadSymbol();
                _currLine[pos + 1] = CrxGolombRice.FoldSign(bitCode);
                _kParam = CrxGolombRice.PredictK(_kParam, bitCode, 15);
            }
            else
            {
                // Possibly a run of zeros (LibRaw's nSyms accumulator).
                var nSyms = 0;
                if (_stream.GetBits(1) != 0)
                {
                    nSyms = 1;
                    while (_stream.GetBits(1) != 0)
                    {
                        nSyms += (int)CrxGolombRice.JS[_sParam];
                        if (nSyms > length) { nSyms = length; break; }
                        if (_sParam < 31) _sParam++;
                        if (nSyms == length) break;
                    }
                    if (nSyms < length)
                    {
                        if (CrxGolombRice.J[_sParam] != 0)
                            nSyms += (int)_stream.GetBits((int)CrxGolombRice.J[_sParam]);
                        if (_sParam > 0) _sParam--;
                        if (nSyms > length)
                            throw new InvalidOperationException("CRX H-band top-line run overrun");
                    }
                }

                length -= nSyms;
                // Run produces explicit zeros in both the sample buffer
                // AND the per-position K history (so the next line sees
                // K=0 at those columns when it checks lineBuf2[i+1]).
                while (nSyms-- > 0)
                {
                    _currLine[pos + 1] = 0;
                    _kHist[kPos] = 0;
                    pos++;
                    kPos++;
                }
                if (length <= 0) break;

                // First sym after a run uses (bitCode+1) fold — the encoder
                // wouldn't have ended the run if the next sample was zero.
                {
                    var bitCode = ReadSymbol();
                    var v = bitCode + 1u;
                    _currLine[pos + 1] = -(int)(v & 1) ^ (int)(v >> 1);
                    _kParam = CrxGolombRice.PredictK(_kParam, bitCode, 15);
                }
            }
            _kHist[kPos] = _kParam;
            pos++;
            kPos++;
            length--;
        }

        if (length == 1)
        {
            // Final sym — straight FoldSign (no run check at end-of-line).
            var bitCode = ReadSymbol();
            _currLine[pos + 1] = CrxGolombRice.FoldSign(bitCode);
            _kParam = CrxGolombRice.PredictK(_kParam, bitCode, 15);
            _kHist[kPos] = _kParam;
            pos++;
        }

        // H-band sentinel = 0 (NOT lastSample+1 like the LL band). Saves
        // a column read in the next line's run-flat context check, where
        // _prevLine[Width+1] = 0 contributes to the OR-test correctly.
        _currLine[pos + 1] = 0;
        _currLine.AsSpan(1, Width).CopyTo(destination);
        (_prevLine, _currLine) = (_currLine, _prevLine);
    }

    /// <summary>Subsequent lines (curLine &gt;= 1). Context check uses three
    /// samples — top-right (<c>lineBuf0[i+2]</c>), top
    /// (<c>lineBuf0[i+1]</c>), left (<c>lineBuf1[i]</c>). Only when ALL
    /// THREE are zero does the encoder admit a run.</summary>
    private void DecodeLine(Span<int> destination)
    {
        // The for-loop below writes _currLine[1..Width] in every code path
        // (regular symbol, run-fill, post-run sample, final symbol), so the
        // only cells that need to start at zero are the two sentinels:
        //   _currLine[0]        = synthetic left neighbour for i=0's context
        //   _currLine[Width+1]  = right sentinel read by NEXT row's i==Width-1
        // The buffer arrives stale from two rows ago via the ping-pong swap,
        // so leaving cells [1..Width] alone is fine — they'll get overwritten
        // before any read. Avoiding the full Array.Clear here is a ~50% win
        // on the CR3 wavelet decode (the per-row clears were 63% of decode
        // time on R5 cRAW, per dotnet-trace cpu sampling).
        _currLine[0] = 0;
        _currLine[Width + 1] = 0;

        var i = 0;
        for (; i < Width - 1; i++)
        {
            if ((_prevLine[i + 2] | _prevLine[i + 1] | _currLine[i]) != 0)
            {
                // Context has signal — decode normal symbol with uncapped
                // K prediction, then apply the per-position cap-or-bump.
                var bitCode = ReadSymbol();
                _currLine[i + 1] = CrxGolombRice.FoldSign(bitCode);
                _kParam = CrxGolombRice.PredictK(_kParam, bitCode);
                if (_kHist[i + 1] - _kParam <= 1)
                {
                    if (_kParam >= 15) _kParam = 15;
                }
                else
                    _kParam++;
            }
            else
            {
                // All-zero context — possibly a run.
                var nSyms = 0;
                if (_stream.GetBits(1) != 0)
                {
                    nSyms = 1;
                    if (i != Width - 1)
                    {
                        while (_stream.GetBits(1) != 0)
                        {
                            nSyms += (int)CrxGolombRice.JS[_sParam];
                            if (i + nSyms > Width) { nSyms = Width - i; break; }
                            if (_sParam < 31) _sParam++;
                            if (i + nSyms == Width) break;
                        }
                        if (i + nSyms < Width)
                        {
                            if (CrxGolombRice.J[_sParam] != 0)
                                nSyms += (int)_stream.GetBits((int)CrxGolombRice.J[_sParam]);
                            if (_sParam > 0) _sParam--;
                            if (i + nSyms > Width)
                                throw new InvalidOperationException("CRX H-band line run overrun");
                        }
                    }
                }

                if (nSyms > 0)
                {
                    // Zero the run in both sample + K-history buffers.
                    for (var j = 0; j < nSyms; j++)
                    {
                        _currLine[i + 1 + j] = 0;
                        _kHist[i + j] = 0;
                    }
                    i += nSyms;
                }

                if (i >= Width - 1)
                {
                    if (i == Width - 1)
                    {
                        // Final symbol after a run. Capped K-update — no
                        // per-position adjust at the very end of line.
                        var bitCode = ReadSymbol();
                        var v = bitCode + 1u;
                        _currLine[i + 1] = -(int)(v & 1) ^ (int)(v >> 1);
                        _kParam = CrxGolombRice.PredictK(_kParam, bitCode, 15);
                        _kHist[i] = _kParam;
                    }
                    // Run that consumed to end-of-line or beyond — exit
                    // the for-loop by advancing past Width-1.
                    continue;
                }
                else
                {
                    // Symbol after a non-terminal run — (bitCode+1) fold,
                    // uncapped K prediction, then per-position adjust.
                    var bitCode = ReadSymbol();
                    var v = bitCode + 1u;
                    _currLine[i + 1] = -(int)(v & 1) ^ (int)(v >> 1);
                    _kParam = CrxGolombRice.PredictK(_kParam, bitCode);
                    if (_kHist[i + 1] - _kParam <= 1)
                    {
                        if (_kParam >= 15) _kParam = 15;
                    }
                    else
                        _kParam++;
                }
            }
            _kHist[i] = _kParam;
        }

        if (i == Width - 1)
        {
            // Final symbol at end-of-line (reached normally, not via run).
            var bitCode = ReadSymbol();
            _currLine[i + 1] = CrxGolombRice.FoldSign(bitCode);
            _kParam = CrxGolombRice.PredictK(_kParam, bitCode, 15);
            _kHist[i] = _kParam;
        }

        _currLine.AsSpan(1, Width).CopyTo(destination);
        (_prevLine, _currLine) = (_currLine, _prevLine);
    }
}
