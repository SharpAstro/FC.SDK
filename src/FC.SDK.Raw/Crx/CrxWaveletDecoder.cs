using System;
using System.Collections.Generic;
using System.IO;

namespace FC.SDK.Raw.Crx;

/// <summary>
/// CRX wavelet+Golomb-Rice decoder for a single (tile, plane) pair when
/// <c>levels &gt; 0</c> (Canon "CRAW" lossless mode — encType=0, 1..3 levels
/// of CDF 5/3 wavelet decomposition). Orchestrates 3*N+1 entropy-coded
/// subbands per plane through an N-level inverse-wavelet pyramid, producing
/// one row of plane samples per call to <see cref="DecodeNextRow"/>.
///
/// <para>Port of LibRaw's <c>crxIdwt53Filter*</c> family (clean-room
/// algorithmic mapping — see <c>src/decoders/crx.cpp</c> in the LibRaw
/// LGPL 2.1 source tree). The pump model is identical:</para>
/// <list type="number">
/// <item><see cref="Initialize"/> primes <c>levels</c> wavelet stages by
///   decoding the first row of each subband and running an extra horizontal
///   pass at level 0 (the coarsest) plus a vertical seed lift to produce
///   the first H-band output of each pyramid level.</item>
/// <item>Each <see cref="DecodeNextRow"/> call invokes
///   <see cref="FilterDecode"/> then <see cref="FilterTransform"/> at the
///   finest level. Both recurse downward: when the current level's ring
///   has emptied (<c>curH == 0</c>), it pulls fresh band data from the
///   next-coarser level (which may in turn recurse), and runs the inverse
///   5/3 lifting to produce 2 (or 3 at the end) new output rows.
///   <see cref="GetLine"/> grabs one row from the ring and decrements
///   <c>curH</c>.</item>
/// </list>
///
/// <para>Subband geometry (<see cref="ComputeSubbandGeometry"/>) is derived
/// from tile dimensions + the <c>exCoefNumTbl[144]</c> boundary-extension
/// table from LibRaw — for tile edges that border another tile, an extra
/// 0..2 columns/rows of overlap coefficients are encoded so the inverse
/// lifting at the seam matches what the next tile produces independently.</para>
///
/// <para>The Bayer-CFA interleave (plane index ↔ 2×2 cell position) is
/// done by the caller in <see cref="CrxDecoder"/>; this class is plane-
/// agnostic.</para>
/// </summary>
internal sealed class CrxWaveletPlaneDecoder
{
    /// <summary>Plane sample width — what each <see cref="DecodeNextRow"/>
    /// call produces. Equals the tile width in LibRaw plane units (i.e.
    /// half the image-units tile width for nPlanes=4 Bayer CFA).</summary>
    public int OutputWidth { get; }

    /// <summary>Plane sample height — total rows the caller should pump.</summary>
    public int OutputHeight { get; }

    /// <summary>Per-subband state. Index layout: [0]=LL, [1,2,3]=HL_0/LH_0/HH_0
    /// (coarsest detail), [4,5,6]=HL_1/LH_1/HH_1, [7,8,9]=HL_2/LH_2/HH_2
    /// (finest detail), for levels=3. The pump consumes them in this order
    /// during recursion.</summary>
    private readonly Subband[] _subbands;
    /// <summary>Per-level wavelet state. <c>_levels[0]</c> is the COARSEST
    /// (produces a quarter-res LL' for level 1's reconstruction);
    /// <c>_levels[levels-1]</c> is the FINEST (output of GetLine here is
    /// the final plane row).</summary>
    private readonly WaveletLevel[] _levels;
    private readonly byte _tileFlag;
    /// <summary>Configured levels count. Equal to <c>_levels.Length</c>;
    /// kept for clarity in the pump recursions.</summary>
    private readonly int _nLevels;
    /// <summary>Initialization is deferred to the first
    /// <see cref="DecodeNextRow"/> — the constructor only builds geometry
    /// and wires bitstreams. This avoids running the LibRaw pump-prime
    /// step (which reads from the bitstreams) while we're still mid-setup
    /// across many planes.</summary>
    private bool _initialized;

    public CrxWaveletPlaneDecoder(
        ReadOnlySpan<byte> bytes,
        int planeWidth,
        int planeHeight,
        int levels,
        byte tileFlag,
        IReadOnlyList<CrxMdatHeader.Subband> planeSubbands)
    {
        if (levels < 1 || levels > 3)
            throw new ArgumentOutOfRangeException(nameof(levels),
                "CRX wavelet decoder supports levels=1..3 (per LibRaw's exCoefNumTbl coverage).");
        OutputWidth = planeWidth;
        OutputHeight = planeHeight;
        _nLevels = levels;
        _tileFlag = tileFlag;

        var expectedBands = 3 * levels + 1;
        if (planeSubbands.Count != expectedBands)
            throw new InvalidDataException(
                $"CRX wavelet plane expects {expectedBands} subbands (3*{levels}+1) but got {planeSubbands.Count}.");

        // Compute per-subband width/height + extension bookkeeping from
        // tile geometry. The mdat-header walk only gives us dataOffset/
        // dataSize/qParam; widths/heights come from the geometry pass.
        var geom = ComputeSubbandGeometry(planeWidth, planeHeight, levels, tileFlag);

        _subbands = new Subband[expectedBands];
        for (var i = 0; i < expectedBands; i++)
        {
            var src = planeSubbands[i];
            if (src.BandIndex != i)
                throw new InvalidDataException(
                    $"CRX wavelet plane subband[{i}] has bandIndex={src.BandIndex} (expected sequential).");

            var width = geom.Widths[i];
            var height = geom.Heights[i];
            // LL band uses LOCO-I + run-length (supportsPartial=true in
            // LibRaw). Detail bands (HL/LH/HH) use the no-reference-prev-
            // line variant with zero-centred coefficients.
            var supportsPartial = i == 0;
            var bandSlice = bytes.Slice((int)src.DataOffset, src.DataSize);
            _subbands[i] = new Subband(width, height, src.QParam, supportsPartial, bandSlice);
        }

        // Wavelet level wiring. wavelet[L].width / height come from a one-
        // level-finer subband's dimensions (see LibRaw's crxSetupSubbandData):
        // the wavelet at level L is producing the input to level L+1's lift,
        // so its output extent matches the NEXT-LEVEL band sizes. At the
        // finest level (levels-1), the output is the full tile.
        _levels = new WaveletLevel[levels];
        for (var lvl = 0; lvl < levels; lvl++)
        {
            int w, h;
            if (lvl >= levels - 1)
            {
                w = planeWidth;
                h = planeHeight;
            }
            else
            {
                // band = 3*lvl + 1 -> HL of this level. We want LH of next level
                // (band+4) for width and HL of next level (band+3) for height.
                var band = 3 * lvl + 1;
                h = _subbands[band + 3].Height;
                w = _subbands[band + 4].Width;
            }
            var subband1 = _subbands[3 * lvl + 1];
            var subband2 = _subbands[3 * lvl + 2];
            var subband3 = _subbands[3 * lvl + 3];
            _levels[lvl] = new WaveletLevel(w, h, subband1, subband2, subband3);
        }
        // Initial subband0Buf for the coarsest level: the LL band's bandBuf.
        // The pump refills it via the LL decoder. Higher levels overwrite their
        // subband0Buf reference each pump step (from GetLine on the level below).
        _levels[0].Subband0Buf = _subbands[0].BandBuf;
    }

    /// <summary>Decode one row of plane samples (length <see cref="OutputWidth"/>)
    /// into <paramref name="destination"/>. Caller pumps <see cref="OutputHeight"/>
    /// rows total per plane.</summary>
    public void DecodeNextRow(Span<int> destination)
    {
        if (destination.Length < OutputWidth)
            throw new ArgumentException("destination too small for plane width", nameof(destination));
        if (!_initialized)
        {
            Initialize();
            _initialized = true;
        }
        var top = _nLevels - 1;
        FilterDecode(top);
        FilterTransform(top);
        var line = GetLine(top);
        line.AsSpan(0, OutputWidth).CopyTo(destination);
    }

    // =================================================================
    //  Internal types
    // =================================================================

    /// <summary>Per-subband state: geometry + the line decoder + a one-row
    /// scratch buffer that the wavelet pump consumes.</summary>
    private sealed class Subband
    {
        public int Width { get; }
        public int Height { get; }
        public int QParam { get; }
        /// <summary>One-row decoded coefficients (size <see cref="Width"/>).
        /// Refilled each time <see cref="DecodeNextRow"/> is called. The
        /// wavelet level points its subband0/1/2/3Buf at this array.</summary>
        public int[] BandBuf { get; }
        /// <summary>LL band uses LOCO-I + median predictor; H bands use the
        /// no-reference-prev-line variant.</summary>
        public CrxLineDecoder? LowDecoder { get; }
        public CrxHighBandLineDecoder? HighDecoder { get; }
        /// <summary>Inverse-quantization multiplier derived from qParam. For
        /// lossless qParam=4 -> qScale=1 (identity, multiplication skipped).
        /// </summary>
        public int QScale { get; }

        public Subband(int width, int height, int qParam, bool supportsPartial, ReadOnlySpan<byte> bandBytes)
        {
            Width = width;
            Height = height;
            QParam = qParam;
            BandBuf = new int[width];
            var stream = new CrxBitstream(bandBytes);
            if (supportsPartial)
                LowDecoder = new CrxLineDecoder(stream, width);
            else
                HighDecoder = new CrxHighBandLineDecoder(stream, width);
            QScale = ComputeQScale(qParam);
        }

        /// <summary>Decode one row into <see cref="BandBuf"/> and apply
        /// inverse quantization. Idempotent against the bitstream — the
        /// caller pumps rows in monotonic order.</summary>
        public void DecodeNextRow()
        {
            if (LowDecoder is not null) LowDecoder.DecodeNextRow(BandBuf);
            else HighDecoder!.DecodeNextRow(BandBuf);

            // Inverse-quantize. For the lossless path qScale is normally 1
            // (qParam=4), so this loop is skipped entirely on the hot path.
            var s = QScale;
            if (s != 1 && s != 0)
            {
                var b = BandBuf;
                for (var i = 0; i < b.Length; i++) b[i] *= s;
            }
        }

        /// <summary>Port of LibRaw's qScale derivation in
        /// <c>crxDecodeLineWithIQuantization</c>'s "prev. version" branch
        /// (FF03 headers, no qStep table). For lossless this returns 1.</summary>
        private static int ComputeQScale(int qParam)
        {
            var qpHi = qParam / 6;
            var qpLo = qParam % 6;
            // q_step_tbl values from LibRaw — 6 entries indexed by qParam % 6.
            ReadOnlySpan<int> table = stackalloc int[] { 0x28, 0x2D, 0x33, 0x39, 0x40, 0x48 };
            if (qpHi >= 6)
                return table[qpLo] * (1 << (qpHi + 26));
            return table[qpLo] >> (6 - qpHi);
        }
    }

    /// <summary>Per-level wavelet pump state. Holds 8 line buffers
    /// (3 L-band scratch + 5 H-band output ring) plus rotation/index
    /// bookkeeping that mirrors LibRaw's <c>CrxWaveletTransform</c>.</summary>
    private sealed class WaveletLevel
    {
        public int Width { get; }
        public int Height { get; }
        /// <summary>Number of output rows produced so far at this level
        /// (capped by <see cref="Height"/>). Includes rows the upstream
        /// caller hasn't yet consumed via <see cref="GetLine"/>.</summary>
        public int CurLine;
        /// <summary>Number of output rows currently in the H-band ring.
        /// Decremented by <see cref="GetLine"/>, refilled by
        /// <see cref="FilterTransform"/>. When 0, the next pump call must
        /// produce more rows.</summary>
        public int CurH;
        /// <summary>Ring rotation index, mod 5. The output ring's
        /// "newest" slot is at <c>lineBuf[FltTapH + 3]</c>; each transform
        /// pass advances by 2 (regular) or 3 (final, odd-height) rows.</summary>
        public int FltTapH;
        /// <summary>Eight scratch / ring buffers, each <see cref="Width"/>
        /// ints. <c>[0..2]</c> are the L-band working scratch (current row
        /// L0, plus previous L1/L2 for the vertical lift's 3-tap kernel).
        /// <c>[3..7]</c> are the H-band output ring (5 slots so the
        /// double-step transform can keep two rows ready while a pending
        /// pull pops the third).</summary>
        public int[][] LineBuf { get; }
        /// <summary>Source LL row for this level. At level 0 this points
        /// at the LL subband's <c>BandBuf</c> directly; at higher levels
        /// it's re-pointed each pump step to a row pulled from
        /// <c>level-1</c>'s ring via <see cref="GetLine"/>.</summary>
        public int[] Subband0Buf = [];
        public Subband Subband1 { get; }
        public Subband Subband2 { get; }
        public Subband Subband3 { get; }

        public WaveletLevel(int width, int height, Subband subband1, Subband subband2, Subband subband3)
        {
            Width = width;
            Height = height;
            LineBuf = new int[8][];
            for (var i = 0; i < 8; i++) LineBuf[i] = new int[width];
            Subband1 = subband1;
            Subband2 = subband2;
            Subband3 = subband3;
        }
    }

    // =================================================================
    //  Pump primitives — direct ports of LibRaw's wavelet machinery
    // =================================================================

    /// <summary>LibRaw <c>crxIdwt53FilterGetLine</c> port. Pull one row out
    /// of the H-band ring at <paramref name="level"/>. The ring is a
    /// 5-slot rotation indexed by <c>fltTapH</c> — the OLDEST unread slot
    /// is at <c>(fltTapH - curH + 5) % 5 + 3</c>. Returns a REFERENCE to
    /// the ring slot; the caller must consume it before another transform
    /// at this level overwrites the slot.</summary>
    private int[] GetLine(int level)
    {
        var w = _levels[level];
        var slot = ((w.FltTapH - w.CurH + 5) % 5) + 3;
        w.CurH--;
        return w.LineBuf[slot];
    }

    /// <summary>LibRaw <c>crxIdwt53FilterDecode</c> port. Pre-load the next
    /// row of band data at <paramref name="level"/> (and recursively at
    /// lower levels) so a subsequent <see cref="FilterTransform"/> has
    /// fresh subband coefficients to feed the inverse lift.</summary>
    private void FilterDecode(int level)
    {
        var w = _levels[level];
        if (w.CurH != 0) return;

        // End-of-band special case (LibRaw's `if (height - 3 <= curLine &&
        // !HasBottom)` branch). The structure splits THREE ways:
        //
        //   1. !nearEnd: regular case — decode 1 LL + 3 H rows.
        //   2. nearEnd && height-is-odd: pull 1 LL + only 1 HL row (the
        //      encoder folded the LH/HH tail away because the odd-row
        //      lift only needs an HL contribution).
        //   3. nearEnd && height-is-even: DECODE NOTHING. The lift at
        //      this point reuses the cached L2 + H0 (LibRaw's "L2 + H0"
        //      branch in crxIdwt53FilterTransform). Decoding extra bands
        //      here would overshoot the band's row count and underrun
        //      its bitstream — the failure mode this guard prevents.
        //
        // (HasBottom=true means there's another tile below, so the band
        // does carry full data through to its declared height; the regular
        // branch fires and consumes the boundary-overlap rows.)
        if ((w.Height - 3 <= w.CurLine) && ((_tileFlag & TileFlag.HasBottom) == 0))
        {
            if ((w.Height & 1) != 0)
            {
                if (level > 0) FilterDecode(level - 1);
                else _subbands[0].DecodeNextRow();
                w.Subband1.DecodeNextRow();
            }
            // else: even-height end — no decoding (cached lift state suffices).
            return;
        }

        // Regular case.
        if (level > 0) FilterDecode(level - 1);
        else _subbands[0].DecodeNextRow();
        w.Subband1.DecodeNextRow();
        w.Subband2.DecodeNextRow();
        w.Subband3.DecodeNextRow();
    }

    /// <summary>LibRaw <c>crxIdwt53FilterTransform</c> port — the meat of
    /// the inverse wavelet. Two phases:
    /// <list type="number">
    /// <item>Horizontal pass: combine subband0 (LL row) + subband1 (HL row)
    ///   into lineBuf[0] (the new L0), and subband2 (LH row) + subband3 (HH
    ///   row) into the next L1 slot. Boundary handling per the LEFT/RIGHT
    ///   tile flags.</item>
    /// <item>Vertical pass: lift the three L rows (L0, L1, L2) into two
    ///   new H-band output rows at <c>lineBuf[fltTapH+3 mod 5 + 3]</c>
    ///   and the slot after.</item>
    /// </list></summary>
    private void FilterTransform(int level)
    {
        var w = _levels[level];
        if (w.CurH != 0) return;

        // === End-of-band: last 1-or-2 rows when height is odd / even
        //     and tile has no bottom neighbour. Produces 2 or 3 final
        //     ring outputs through a simplified vertical lift. ===
        if (w.CurLine >= w.Height - 3)
        {
            if ((_tileFlag & TileFlag.HasBottom) == 0)
            {
                if ((w.Height & 1) == 1)
                {
                    // Odd-height end: 3 final rows. Same horizontal pass
                    // as the regular case, but the vertical lift uses a
                    // 2-tap kernel over (L0, L1) instead of the regular
                    // 3-tap (L0, L1, L2) — there's no fresh L2 to add at
                    // the tail. Produces 3 ring outputs.
                    if (level > 0)
                    {
                        if (_levels[level - 1].CurH == 0) FilterTransform(level - 1);
                        w.Subband0Buf = GetLine(level - 1);
                    }
                    var band0 = w.Subband0Buf;
                    var band1 = w.Subband1.BandBuf;

                    var h0 = w.LineBuf[w.FltTapH + 3];
                    var h1 = w.LineBuf[(w.FltTapH + 1) % 5 + 3];
                    var h2 = w.LineBuf[(w.FltTapH + 2) % 5 + 3];
                    var l0 = w.LineBuf[0];
                    var l1 = w.LineBuf[1];
                    // L1 <-> L2 swap mirrors RegularInteriorTransform —
                    // shuffle so the L-band ring rotates while the new
                    // (single-pair) L0 output lands in lineBuf[0].
                    w.LineBuf[1] = w.LineBuf[2];
                    w.LineBuf[2] = l1;

                    HorizontalLiftSingle(band0, band1, l0, w.Width);

                    // RE-READ post-swap: vertical kernel needs new
                    // lineBuf[1] (which is the PREVIOUS iteration's
                    // pair-2 row), not the captured-before-swap l1
                    // (which was the older row of older L1).
                    l0 = w.LineBuf[0];
                    l1 = w.LineBuf[1];
                    for (var i = 0; i < w.Width; i++)
                    {
                        var delta = l0[i] - ((l1[i] + 1) >> 1);
                        h1[i] = l1[i] + ((delta + h0[i]) >> 1);
                        h2[i] = delta;
                    }
                    w.CurH += 3;
                    w.CurLine += 3;
                    w.FltTapH = (w.FltTapH + 3) % 5;
                }
                else
                {
                    // Even-height end: 2 final rows via vertical lift with
                    // an L2 + H0 reconstruction (no fresh L0 decode).
                    var l2 = w.LineBuf[2];
                    var h0 = w.LineBuf[w.FltTapH + 3];
                    var h1 = w.LineBuf[(w.FltTapH + 1) % 5 + 3];
                    // LibRaw swaps lineBuf[1] = lineBuf[2] and then
                    // lineBuf[2] = lineBuf[1] (now equal). Net effect:
                    // lineBuf[1] points to L2's data; lineBuf[2] also
                    // points to L2's data. This collapses the 3-tap
                    // kernel for the band-tail.
                    w.LineBuf[1] = l2;
                    w.LineBuf[2] = w.LineBuf[1];
                    for (var i = 0; i < w.Width; i++)
                        h1[i] = h0[i] + l2[i];
                    w.CurH += 2;
                    w.CurLine += 2;
                    w.FltTapH = (w.FltTapH + 2) % 5;
                }
            }
            // (HasBottom case at end-of-band: do nothing here, fall through
            //  to the regular interior case. The actual end is past Height.)
            else
            {
                RegularInteriorTransform(level);
            }
        }
        else
        {
            RegularInteriorTransform(level);
        }
    }

    /// <summary>The "interior of band" case in LibRaw's
    /// <c>crxIdwt53FilterTransform</c>: pull a fresh L0 row from upstream,
    /// run horizontal lift, then vertical lift to produce 2 new H rows
    /// (or 3 if at the bottom edge with odd height — handled by the caller).</summary>
    private void RegularInteriorTransform(int level)
    {
        var w = _levels[level];

        if (level > 0)
        {
            if (_levels[level - 1].CurH == 0) FilterTransform(level - 1);
            w.Subband0Buf = GetLine(level - 1);
        }

        var band0 = w.Subband0Buf;
        var band1 = w.Subband1.BandBuf;
        var band2 = w.Subband2.BandBuf;
        var band3 = w.Subband3.BandBuf;

        var l0 = w.LineBuf[0];
        var l1 = w.LineBuf[1];
        var l2 = w.LineBuf[2];
        var h0 = w.LineBuf[w.FltTapH + 3];
        var h1 = w.LineBuf[(w.FltTapH + 1) % 5 + 3];
        var h2 = w.LineBuf[(w.FltTapH + 2) % 5 + 3];

        // L1 <-> L2 swap: the new pair (L0, L1, L2) is becoming the
        // (L-1, L0, L1) of the next iteration. LibRaw rotates these
        // by swapping pointers in lineBuf[1] and lineBuf[2].
        w.LineBuf[1] = l2;
        w.LineBuf[2] = l1;

        // Horizontal pass: build new L0 (from band0/band1) and new L1
        // (from band2/band3). The double-band horizontal lift handles
        // the LEFT/RIGHT/odd-width edge variants.
        HorizontalLiftDouble(band0, band1, band2, band3, l0, l1, w.Width);

        // Vertical pass: 3-tap kernel produces 2 new H rows.
        l0 = w.LineBuf[0];
        l1 = w.LineBuf[1];
        l2 = w.LineBuf[2];
        for (var i = 0; i < w.Width; i++)
        {
            var delta = l0[i] - ((l2[i] + l1[i] + 2) >> 2);
            h1[i] = l1[i] + ((delta + h0[i]) >> 1);
            h2[i] = delta;
        }

        if (w.CurLine >= w.Height - 3 && (w.Height & 1) == 1)
        {
            // The tile DOES have a bottom neighbour but height is odd —
            // produce 3 rows so the next tile sees the boundary correctly.
            w.CurH += 3;
            w.CurLine += 3;
            w.FltTapH = (w.FltTapH + 3) % 5;
        }
        else
        {
            w.CurH += 2;
            w.CurLine += 2;
            w.FltTapH = (w.FltTapH + 2) % 5;
        }
    }

    /// <summary>Two-band horizontal lift: build <paramref name="lOut0"/>
    /// from <paramref name="band0"/>+<paramref name="band1"/> (HL pair)
    /// and <paramref name="lOut1"/> from <paramref name="band2"/>+
    /// <paramref name="band3"/> (LH pair). Mirrors LibRaw's inline
    /// horizontal pass in <c>crxIdwt53FilterTransform</c>'s regular case.</summary>
    private void HorizontalLiftDouble(
        int[] band0, int[] band1, int[] band2, int[] band3,
        int[] lOut0, int[] lOut1, int width)
    {
        if (width <= 1)
        {
            lOut0[0] = band0[0];
            lOut1[0] = band2[0];
            return;
        }

        int i0 = 0, i1 = 0, i2 = 0, i3 = 0, o0 = 0, o1 = 0;
        if ((_tileFlag & TileFlag.HasLeft) != 0)
        {
            lOut0[0] = band0[0] - ((band1[0] + band1[1] + 2) >> 2);
            lOut1[0] = band2[0] - ((band3[0] + band3[1] + 2) >> 2);
            i1++;
            i3++;
        }
        else
        {
            lOut0[0] = band0[0] - ((band1[0] + 1) >> 1);
            lOut1[0] = band2[0] - ((band3[0] + 1) >> 1);
        }
        i0++;
        i2++;

        for (var i = 0; i < width - 3; i += 2)
        {
            var deltaA = band0[i0] - ((band1[i1] + band1[i1 + 1] + 2) >> 2);
            lOut0[o0 + 1] = band1[i1] + ((deltaA + lOut0[o0]) >> 1);
            lOut0[o0 + 2] = deltaA;

            var deltaB = band2[i2] - ((band3[i3] + band3[i3 + 1] + 2) >> 2);
            lOut1[o1 + 1] = band3[i3] + ((deltaB + lOut1[o1]) >> 1);
            lOut1[o1 + 2] = deltaB;

            i0++; i1++; i2++; i3++;
            o0 += 2; o1 += 2;
        }

        if ((_tileFlag & TileFlag.HasRight) != 0)
        {
            var deltaA = band0[i0] - ((band1[i1] + band1[i1 + 1] + 2) >> 2);
            lOut0[o0 + 1] = band1[i1] + ((deltaA + lOut0[o0]) >> 1);

            var deltaB = band2[i2] - ((band3[i3] + band3[i3 + 1] + 2) >> 2);
            lOut1[o1 + 1] = band3[i3] + ((deltaB + lOut1[o1]) >> 1);

            if ((width & 1) != 0)
            {
                lOut0[o0 + 2] = deltaA;
                lOut1[o1 + 2] = deltaB;
            }
        }
        else if ((width & 1) != 0)
        {
            var deltaA = band0[i0] - ((band1[i1] + 1) >> 1);
            lOut0[o0 + 1] = band1[i1] + ((deltaA + lOut0[o0]) >> 1);
            lOut0[o0 + 2] = deltaA;

            var deltaB = band2[i2] - ((band3[i3] + 1) >> 1);
            lOut1[o1 + 1] = band3[i3] + ((deltaB + lOut1[o1]) >> 1);
            lOut1[o1 + 2] = deltaB;
        }
        else
        {
            lOut0[o0 + 1] = lOut0[o0] + band1[i1];
            lOut1[o1 + 1] = lOut1[o1] + band3[i3];
        }
    }

    /// <summary>Single-band horizontal lift: build <paramref name="lOut"/>
    /// from <paramref name="band0"/>+<paramref name="band1"/>. Used in
    /// the odd-height tail case where only one (band0/band1) pair is
    /// available.</summary>
    private void HorizontalLiftSingle(int[] band0, int[] band1, int[] lOut, int width)
    {
        if (width <= 1)
        {
            lOut[0] = band0[0];
            return;
        }

        int i0 = 0, i1 = 0, o = 0;
        if ((_tileFlag & TileFlag.HasLeft) != 0)
        {
            lOut[0] = band0[0] - ((band1[0] + band1[1] + 2) >> 2);
            i1++;
        }
        else
        {
            lOut[0] = band0[0] - ((band1[0] + 1) >> 1);
        }
        i0++;

        for (var i = 0; i < width - 3; i += 2)
        {
            var delta = band0[i0] - ((band1[i1] + band1[i1 + 1] + 2) >> 2);
            lOut[o + 1] = band1[i1] + ((lOut[o] + delta) >> 1);
            lOut[o + 2] = delta;
            i0++; i1++;
            o += 2;
        }

        if ((_tileFlag & TileFlag.HasRight) != 0)
        {
            var delta = band0[i0] - ((band1[i1] + band1[i1 + 1] + 2) >> 2);
            lOut[o + 1] = band1[i1] + ((lOut[o] + delta) >> 1);
            if ((width & 1) != 0) lOut[o + 2] = delta;
        }
        else if ((width & 1) != 0)
        {
            var delta = band0[i0] - ((band1[i1] + 1) >> 1);
            lOut[o + 1] = band1[i1] + ((lOut[o] + delta) >> 1);
            lOut[o + 2] = delta;
        }
        else
        {
            lOut[o + 1] = band1[i1] + lOut[o];
        }
    }

    /// <summary>Port of LibRaw's <c>crxIdwt53FilterInitialize</c>: prime
    /// the pump by decoding the first row of each subband and running an
    /// initial horizontal+vertical lift at each level. The top tile-edge
    /// gets special treatment (HasTop case decodes an extra band row to
    /// seed the vertical kernel's history).</summary>
    private void Initialize()
    {
        // Walk levels coarsest-first; each iteration primes one pyramid stage.
        for (var curLevel = 0; curLevel < _nLevels; curLevel++)
        {
            var w = _levels[curLevel];
            var bandBase = 3 * curLevel; // index into _subbands for this level's H bands (+1, +2, +3)

            if (curLevel > 0)
            {
                // Pull a fresh L0 row from the next-coarser level.
                w.Subband0Buf = GetLine(curLevel - 1);
            }
            else
            {
                // Coarsest level: decode the FIRST row of the LL subband.
                _subbands[0].DecodeNextRow();
                // Subband0Buf already set to _subbands[0].BandBuf in ctor.
            }

            var h0 = w.LineBuf[w.FltTapH + 3];

            if (w.Height > 1)
            {
                // Decode first row of the 3 H bands at this level.
                _subbands[bandBase + 1].DecodeNextRow();
                _subbands[bandBase + 2].DecodeNextRow();
                _subbands[bandBase + 3].DecodeNextRow();

                var l0 = w.LineBuf[0];
                var l1 = w.LineBuf[1];
                var l2 = w.LineBuf[2];

                if ((_tileFlag & TileFlag.HasTop) != 0)
                {
                    // Tile has a top neighbour — decode an extra (band3, band2)
                    // pair to seed the vertical kernel's L2 from above.
                    HorizontalLiftSingle(
                        w.Subband0Buf, w.Subband1.BandBuf, l0, w.Width);
                    // LibRaw uses lineBuf[1] as the second output of crxHorizontal53
                    // when HasTop is set — port that distinction.
                    HorizontalLiftSingle(
                        _subbands[bandBase + 2].BandBuf, _subbands[bandBase + 3].BandBuf, w.LineBuf[1], w.Width);

                    _subbands[bandBase + 3].DecodeNextRow();
                    _subbands[bandBase + 2].DecodeNextRow();

                    var band2 = _subbands[bandBase + 2].BandBuf;
                    var band3 = _subbands[bandBase + 3].BandBuf;
                    if (w.Width <= 1)
                    {
                        l2[0] = band2[0];
                    }
                    else
                    {
                        int i2 = 0, i3 = 0, o2 = 0;
                        if ((_tileFlag & TileFlag.HasLeft) != 0)
                        {
                            l2[0] = band2[0] - ((band3[0] + band3[1] + 2) >> 2);
                            i3++;
                        }
                        else
                        {
                            l2[0] = band2[0] - ((band3[0] + 1) >> 1);
                        }
                        i2++;
                        for (var i = 0; i < w.Width - 3; i += 2)
                        {
                            var delta = band2[i2] - ((band3[i3] + band3[i3 + 1] + 2) >> 2);
                            l2[o2 + 1] = band3[i3] + ((l2[o2] + delta) >> 1);
                            l2[o2 + 2] = delta;
                            i2++; i3++;
                            o2 += 2;
                        }
                        if ((_tileFlag & TileFlag.HasRight) != 0)
                        {
                            var delta = band2[i2] - ((band3[i3] + band3[i3 + 1] + 2) >> 2);
                            l2[o2 + 1] = band3[i3] + ((l2[o2] + delta) >> 1);
                            if ((w.Width & 1) != 0) l2[o2 + 2] = delta;
                        }
                        else if ((w.Width & 1) != 0)
                        {
                            var delta = band2[i2] - ((band3[i3] + 1) >> 1);
                            l2[o2 + 1] = band3[i3] + ((l2[o2] + delta) >> 1);
                            l2[o2 + 2] = delta;
                        }
                        else
                        {
                            l2[o2 + 1] = band3[i3] + l2[o2];
                        }
                    }

                    for (var i = 0; i < w.Width; i++)
                        h0[i] = l0[i] - ((l1[i] + l2[i] + 2) >> 2);
                }
                else
                {
                    // No top neighbour — use a degenerate seed: l2 = lineBuf[2]
                    // gets filled by crxHorizontal53 writing to lineBuf[0] +
                    // lineBuf[2] (i.e. the second output goes to slot 2, not 1).
                    HorizontalLiftSingle(
                        w.Subband0Buf, w.Subband1.BandBuf, l0, w.Width);
                    HorizontalLiftSingle(
                        _subbands[bandBase + 2].BandBuf, _subbands[bandBase + 3].BandBuf, l2, w.Width);
                    for (var i = 0; i < w.Width; i++)
                        h0[i] = l0[i] - ((l2[i] + 1) >> 1);
                }

                // Prime the ring by running FilterDecode + FilterTransform
                // once at this level. This produces the first 2 (or 3) H
                // outputs and advances curLine/curH for subsequent pumps.
                FilterDecode(curLevel);
                FilterTransform(curLevel);
            }
            else
            {
                // Height==1: only one H-band row to decode.
                _subbands[bandBase + 1].DecodeNextRow();
                var band0 = w.Subband0Buf;
                var band1 = w.Subband1.BandBuf;
                if (w.Width <= 1)
                {
                    h0[0] = band0[0];
                }
                else
                {
                    int i0 = 0, i1 = 0, o = 0;
                    if ((_tileFlag & TileFlag.HasLeft) != 0)
                    {
                        h0[0] = band0[0] - ((band1[0] + band1[1] + 2) >> 2);
                        i1++;
                    }
                    else
                    {
                        h0[0] = band0[0] - ((band1[0] + 1) >> 1);
                    }
                    i0++;
                    for (var i = 0; i < w.Width - 3; i += 2)
                    {
                        var delta = band0[i0] - ((band1[i1] + band1[i1 + 1] + 2) >> 2);
                        h0[o + 1] = band1[i1] + ((h0[o] + delta) >> 1);
                        h0[o + 2] = delta;
                        i0++; i1++;
                        o += 2;
                    }
                    if ((_tileFlag & TileFlag.HasRight) != 0)
                    {
                        var delta = band0[i0] - ((band1[i1] + band1[i1 + 1] + 2) >> 2);
                        h0[o + 1] = band1[i1] + ((h0[o] + delta) >> 1);
                        h0[o + 2] = delta;
                    }
                    else if ((w.Width & 1) != 0)
                    {
                        var delta = band0[i0] - ((band1[i1] + 1) >> 1);
                        h0[o + 1] = band1[i1] + ((h0[o] + delta) >> 1);
                        h0[o + 2] = delta;
                    }
                    else
                    {
                        h0[o + 1] = band1[i1] + h0[o];
                    }
                }
                w.CurH++;
                w.CurLine++;
                w.FltTapH = (w.FltTapH + 1) % 5;
            }
        }
    }

    // =================================================================
    //  Subband geometry — port of crxProcessSubbands + exCoefNumTbl
    // =================================================================

    private static class TileFlag
    {
        public const byte HasRight = 1;
        public const byte HasLeft = 2;
        public const byte HasBottom = 4;
        public const byte HasTop = 8;
    }

    /// <summary>LibRaw's <c>exCoefNumTbl[144]</c> verbatim — three blocks
    /// of 48 entries (one per <c>img-&gt;levels-1</c> value of 0/1/2).
    /// Each block is 8 sub-tables of 6 ints indexed by
    /// <c>(tileWidth | tileHeight) &amp; 7</c>. The 6 values give the per-
    /// level boundary-extension counts for HasRight (idx 0,1), HasBottom
    /// (idx 0,1) at each of up to 3 levels.</summary>
    private static readonly int[] ExCoefNumTbl =
    [
        1, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 1, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 1, 1, 0, 0, 0,
        0, 1, 0, 0, 0, 0, 0, 1, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 1, 1, 1, 1, 0, 0, 1, 0, 1, 0,
        0, 0, 1, 2, 2, 1, 0, 0, 1, 1, 1, 1, 0, 0, 1, 1, 1, 1, 0, 0, 1, 0, 1, 0, 0, 0, 1, 2, 2,
        1, 0, 0, 1, 1, 1, 1, 0, 0, 1, 1, 1, 1, 1, 1, 1, 0, 1, 0, 1, 0, 1, 2, 2, 2, 2, 1, 1, 1,
        1, 2, 2, 1, 1, 1, 1, 2, 2, 1, 1, 0, 1, 1, 1, 1, 1, 2, 2, 1, 1, 1, 1, 1, 1, 1, 1, 1
    ];

    private readonly record struct SubbandGeometry(int[] Widths, int[] Heights);

    /// <summary>Compute the <c>3*levels + 1</c> per-band widths and heights
    /// for a given tile. Implements LibRaw's <c>crxProcessSubbands</c>: walk
    /// finest-band-first, halving the running (bandWidth, bandHeight) each
    /// step and applying <see cref="ExCoefNumTbl"/> overlap counters based
    /// on which sides have neighbouring tiles. LL is filled in last with
    /// the final halved dimensions plus the right/bottom-overlap residuals.</summary>
    private static SubbandGeometry ComputeSubbandGeometry(
        int tileWidth, int tileHeight, int levels, byte tileFlag)
    {
        var totalBands = 3 * levels + 1;
        var widths = new int[totalBands];
        var heights = new int[totalBands];

        var bandWidth = tileWidth;
        var bandHeight = tileHeight;
        var blockBase = 0x30 * (levels - 1);
        var rowOffset = blockBase + 6 * (tileWidth & 7);
        var colOffset = blockBase + 6 * (tileHeight & 7);

        // Bands are filled finest-first into the END of the subBands array
        // (subBands[3*levels..3*levels-3, then 3*levels-3..3*levels-6, etc.).
        // The "level" loop variable goes 0..levels-1 but writes the FINEST
        // bands first (subbands[3*levels..3*levels-2] in iter 0).
        var bandTail = totalBands - 1; // index of HH at the finest level
        for (var level = 0; level < levels; level++)
        {
            var widthOdd = bandWidth & 1;
            var heightOdd = bandHeight & 1;
            bandWidth = (widthOdd + bandWidth) >> 1;
            bandHeight = (heightOdd + bandHeight) >> 1;

            int bandWidthExCoef0 = 0;
            int bandWidthExCoef1 = 0;
            int bandHeightExCoef0 = 0;
            int bandHeightExCoef1 = 0;
            if ((tileFlag & TileFlag.HasRight) != 0)
            {
                bandWidthExCoef0 = ExCoefNumTbl[rowOffset + 2 * level];
                bandWidthExCoef1 = ExCoefNumTbl[rowOffset + 2 * level + 1];
            }
            if ((tileFlag & TileFlag.HasLeft) != 0)
            {
                bandWidthExCoef0++;
            }
            if ((tileFlag & TileFlag.HasBottom) != 0)
            {
                bandHeightExCoef0 = ExCoefNumTbl[colOffset + 2 * level];
                bandHeightExCoef1 = ExCoefNumTbl[colOffset + 2 * level + 1];
            }
            if ((tileFlag & TileFlag.HasTop) != 0)
            {
                bandHeightExCoef0++;
            }

            // band[0] = HH at this level
            widths[bandTail] = bandWidth + bandWidthExCoef0 - widthOdd;
            heights[bandTail] = bandHeight + bandHeightExCoef0 - heightOdd;
            // band[-1] = LH (lowpass horizontal, highpass vertical)
            widths[bandTail - 1] = bandWidth + bandWidthExCoef1;
            heights[bandTail - 1] = bandHeight + bandHeightExCoef0 - heightOdd;
            // band[-2] = HL (highpass horizontal, lowpass vertical)
            widths[bandTail - 2] = bandWidth + bandWidthExCoef0 - widthOdd;
            heights[bandTail - 2] = bandHeight + bandHeightExCoef1;

            bandTail -= 3;
        }

        // LL band — final (coarsest) extents plus right/bottom overlap.
        int llExCoefW = 0, llExCoefH = 0;
        if ((tileFlag & TileFlag.HasRight) != 0)
            llExCoefW = ExCoefNumTbl[rowOffset + 2 * levels - 1];
        if ((tileFlag & TileFlag.HasBottom) != 0)
            llExCoefH = ExCoefNumTbl[colOffset + 2 * levels - 1];
        widths[0] = bandWidth + llExCoefW;
        heights[0] = bandHeight + llExCoefH;

        return new SubbandGeometry(widths, heights);
    }
}
