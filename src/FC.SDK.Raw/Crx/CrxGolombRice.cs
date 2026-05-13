using System.Runtime.CompilerServices;

namespace FC.SDK.Raw.Crx;

/// <summary>
/// Adaptive Golomb-Rice symbol coder used throughout the CRX entropy stream
/// (per-subband coefficients, per-tile QP deltas, run-length refinement bits).
/// State is just the K (Rice quotient bit-count) and S (run-length order
/// index) parameters; both adapt every symbol per the rules in LibRaw's
/// <c>crxPredictKParameter</c>.
///
/// <para>This class only handles the bare Rice symbol decode (quotient +
/// remainder + escape). Run-length escape sequences and the per-line
/// median-prediction logic live in the line decoder one layer up; both
/// build on this primitive.</para>
///
/// <para>Two parameter regimes:</para>
/// <list type="bullet">
/// <item>Subband coefficients: <c>MaxK = 15</c>, run-length index uses
///   <c>J[32]</c> / <c>JS[32]</c> from the JPEG-LS LOCO-I specification.</item>
/// <item>QP-map deltas (cRAW only): <c>MaxK = 7</c>, no run-length escape.</item>
/// </list>
/// </summary>
internal sealed class CrxGolombRice
{
    /// <summary>Standard JPEG-LS LOCO-I run-length skip table (ISO/IEC 14495-1).
    /// Indexed by the adaptive S parameter (0..31). The value at <c>JS[s]</c>
    /// is the number of identical-prediction symbols a run-of-length entry
    /// represents at that level — the run-length encoding is exponential.</summary>
    internal static readonly uint[] JS =
    [
        1, 1, 1, 1, 2, 2, 2, 2,
        4, 4, 4, 4, 8, 8, 8, 8,
        0x10, 0x10, 0x20, 0x20, 0x40, 0x40, 0x80, 0x80,
        0x100, 0x200, 0x400, 0x800, 0x1000, 0x2000, 0x4000, 0x8000,
    ];

    /// <summary>Standard JPEG-LS LOCO-I run-length refinement bit count.
    /// Indexed by the adaptive S parameter (0..31). <c>J[s]</c> bits of
    /// run-length refinement are appended to each run-length entry once
    /// the prefix "longer run continues" decision has been read.</summary>
    internal static readonly uint[] J =
    [
        0, 0, 0, 0, 1, 1, 1, 1,
        2, 2, 2, 2, 3, 3, 3, 3,
        4, 4, 5, 5, 6, 6, 7, 7,
        8, 9, 0x0A, 0x0B, 0x0C, 0x0D, 0x0E, 0x0F,
    ];

    /// <summary>Maximum Rice K parameter. CRX caps K at 15 for subband
    /// coefficients (the natural ceiling for 14-bit raw data) and 7 for
    /// QP-map deltas (which have much narrower dynamic range).</summary>
    public int MaxK { get; }

    /// <summary>Current Rice K parameter (the quotient bit-count exponent).
    /// Adapts on every symbol; persists across the line / band buffer.</summary>
    public int KParam;

    /// <summary>Current run-length S index. Adapts on every run-length
    /// transition. Unused by the QP-map path (MaxK == 7).</summary>
    public int SParam;

    public CrxGolombRice(int maxK = 15)
    {
        MaxK = maxK;
        KParam = 0;
        SParam = 0;
    }

    /// <summary>Read one Rice-coded symbol — just the bit decoding step,
    /// no K adaptation. Use when the caller needs to smooth the bitCode
    /// against a neighbour gradient before driving the K update
    /// (LibRaw's <c>crxDecodeSymbolL1</c> non-EOL path). After reading,
    /// invoke <see cref="AdaptK"/> with the final (possibly smoothed)
    /// bitCode to update <see cref="KParam"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public uint ReadBitCode(CrxBitstream stream)
    {
        var q = stream.GetZeros();
        if (q >= 41)
        {
            // Escape: the unary prefix is the "I give up, here's a literal"
            // signal. Read a raw 21-bit value. 21 covers 14-bit raw data
            // amplified through the wavelet without overflow.
            return stream.GetBits(21);
        }
        if (KParam > 0)
        {
            // Standard Rice decode: quotient = q (unary prefix), remainder =
            // next KParam bits, combined as `quotient << K | remainder`.
            return (uint)q << KParam | stream.GetBits(KParam);
        }
        // K == 0 reduces Rice to pure unary; the prefix length IS the value.
        return (uint)q;
    }

    /// <summary>Update <see cref="KParam"/> from <paramref name="bitCode"/>.
    /// LibRaw's <c>crxPredictKParameter</c> — three deltas summed onto the
    /// previous K, then clamped to <see cref="MaxK"/>. The
    /// <c>(1 &lt;&lt; oldK &gt;&gt; 1)</c> trick yields 0 when <c>oldK == 0</c>,
    /// so the decrement never fires at K=0 (which would otherwise drive
    /// K negative).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void AdaptK(uint bitCode)
    {
        KParam = PredictK(KParam, bitCode, MaxK);
    }

    /// <summary>Stateless <c>crxPredictKParameter</c> — the H-band decoder
    /// (<see cref="CrxHighBandLineDecoder"/>) drives its own K state per
    /// position via <c>lineBuf2</c> overrides, so it can't use the
    /// instance-bound <see cref="AdaptK"/>. <paramref name="maxK"/> of 0 means
    /// "no cap" (LibRaw's default — used in the run-flat path where K is
    /// adjusted again by the per-position lineBuf2 logic).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int PredictK(int prevK, uint bitCode, int maxK = 0)
    {
        var newK = prevK
            - (bitCode < (1u << prevK >> 1) ? 1 : 0)
            + ((bitCode >> prevK) > 2 ? 1 : 0)
            + ((bitCode >> prevK) > 5 ? 1 : 0);
        return maxK == 0 || newK < maxK ? newK : maxK;
    }

    /// <summary>Read one Rice-coded symbol AND update <see cref="KParam"/>
    /// using the raw bitCode. Convenience for callers that don't smooth
    /// (top-line decode, simple subband bands). Returns the unsigned
    /// magnitude — use <see cref="FoldSign"/> for the signed coefficient.</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public uint DecodeSymbol(CrxBitstream stream)
    {
        var bitCode = ReadBitCode(stream);
        AdaptK(bitCode);
        return bitCode;
    }

    /// <summary>Zigzag / sign-fold an unsigned bit-code into a signed
    /// coefficient. The LSB carries the sign bit; the upper bits carry the
    /// magnitude. <c>0 -> 0, 1 -> -1, 2 -> 1, 3 -> -2, 4 -> 2, ...</c>
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int FoldSign(uint bitCode) => -(int)(bitCode & 1) ^ (int)(bitCode >> 1);
}
