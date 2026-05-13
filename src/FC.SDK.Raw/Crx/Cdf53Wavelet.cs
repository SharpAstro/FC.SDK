using System;

namespace FC.SDK.Raw.Crx;

/// <summary>
/// Le Gall 5/3 (CDF 5/3) integer-lifting reversible wavelet. The wavelet
/// CR3's CRX codec uses to decompose each sensor-plane into LL / HL / LH / HH
/// subbands. Lossless by construction — the lifting is integer-only with
/// floor-rounding, so the forward+inverse round-trip is bit-exact.
///
/// <para>This class is the math primitive. The CRX-specific orchestration
/// (which subband data comes from where in the entropy stream, how levels
/// stack, which tile-edge boundary mode applies) lives in <c>CrxDecoder</c>
/// at B.4 — that's where the band-split storage convention LibRaw uses
/// gets wired in. The pieces here just have to be mathematically correct.</para>
///
/// <para>Lifting formulas (1D, standard CDF 5/3):</para>
/// <list type="bullet">
/// <item>Forward predict: <c>d[n] = x[2n+1] - floor((x[2n] + x[2n+2]) / 2)</c></item>
/// <item>Forward update:  <c>s[n] = x[2n] + floor((d[n-1] + d[n] + 2) / 4)</c></item>
/// <item>Inverse update:  <c>x[2n] = s[n] - floor((d[n-1] + d[n] + 2) / 4)</c></item>
/// <item>Inverse predict: <c>x[2n+1] = d[n] + floor((x[2n] + x[2n+2]) / 2)</c></item>
/// </list>
///
/// <para>Boundary handling is whole-point symmetric extension: missing samples
/// at -1 and N reflect back to indices 1 and N-2 respectively. This is the
/// JPEG 2000 default for CDF 5/3 and matches the symmetric variant CRX
/// uses for tile interiors (tile edges with neighbours use the "extended"
/// variant where the missing sample comes from the next tile's data; that
/// branch is handled at the orchestration layer).</para>
/// </summary>
internal static class Cdf53Wavelet
{
    /// <summary>In-place 1D forward 5/3 lifting on an interleaved signal.
    /// After the call, even indices hold the lowpass (<c>s</c>) coefficients
    /// and odd indices hold the highpass (<c>d</c>) coefficients.</summary>
    /// <param name="signal">Any length &gt;= 2. Both odd and even lengths
    /// are supported via symmetric boundary extension.</param>
    public static void Forward1D(Span<int> signal)
    {
        var n = signal.Length;
        if (n < 2) return;

        // Step 1 — predict (odd indices). d[n] = x[2n+1] - floor((x[2n] + x[2n+2]) / 2).
        // The right neighbour reflects (x[n]=x[n-2]) when we run off the end.
        for (var i = 1; i < n; i += 2)
        {
            var left = signal[i - 1];
            var right = i + 1 < n ? signal[i + 1] : signal[i - 1];
            signal[i] -= (left + right) >> 1;
        }

        // Step 2 — update (even indices). s[n] = x[2n] + floor((d[n-1] + d[n] + 2) / 4).
        // Boundary: at i=0, the missing left neighbour reflects (d[-1]=d[0]); at
        // i=N-1 (when n is odd), the missing right neighbour reflects.
        for (var i = 0; i < n; i += 2)
        {
            var left = i > 0 ? signal[i - 1] : (i + 1 < n ? signal[i + 1] : 0);
            var right = i + 1 < n ? signal[i + 1] : (i > 0 ? signal[i - 1] : 0);
            signal[i] += (left + right + 2) >> 2;
        }
    }

    /// <summary>In-place 1D inverse 5/3 lifting. Exact inverse of
    /// <see cref="Forward1D"/> — must be applied to the same length signal
    /// with even indices = lowpass, odd indices = highpass.</summary>
    public static void Inverse1D(Span<int> signal)
    {
        var n = signal.Length;
        if (n < 2) return;

        // Step 1 — undo update. x[2n] = s[n] - floor((d[n-1] + d[n] + 2) / 4).
        // Same boundary reflections as the forward pass; the symmetric extension
        // makes the inverse exact because the floor-rounding biases cancel.
        for (var i = 0; i < n; i += 2)
        {
            var left = i > 0 ? signal[i - 1] : (i + 1 < n ? signal[i + 1] : 0);
            var right = i + 1 < n ? signal[i + 1] : (i > 0 ? signal[i - 1] : 0);
            signal[i] -= (left + right + 2) >> 2;
        }

        // Step 2 — undo predict. x[2n+1] = d[n] + floor((x[2n] + x[2n+2]) / 2).
        for (var i = 1; i < n; i += 2)
        {
            var left = signal[i - 1];
            var right = i + 1 < n ? signal[i + 1] : signal[i - 1];
            signal[i] += (left + right) >> 1;
        }
    }

    /// <summary>2D forward 5/3 lifting applied separably — horizontal then
    /// vertical 1D pass. Output layout follows the JPEG 2000 / wavelet
    /// convention: LL goes to (even row, even col), HL to (even row, odd col),
    /// LH to (odd row, even col), HH to (odd row, odd col).</summary>
    /// <param name="data">Row-major 2D buffer of size <paramref name="height"/>
    /// rows × <paramref name="stride"/> ints. The active region is
    /// <paramref name="width"/> columns within each row; the stride lets the
    /// caller use a larger buffer for cache-line alignment.</param>
    public static void Forward2D(int[] data, int width, int height, int stride)
    {
        // Horizontal pass: 1D transform on each row.
        for (var y = 0; y < height; y++)
            Forward1D(data.AsSpan(y * stride, width));

        // Vertical pass: 1D transform on each column. We use a scratch buffer
        // since the in-place 1D primitive expects a contiguous span; copying
        // a column out + back keeps the helper simple at the cost of one
        // pass through memory.
        var col = new int[height];
        for (var x = 0; x < width; x++)
        {
            for (var y = 0; y < height; y++) col[y] = data[y * stride + x];
            Forward1D(col);
            for (var y = 0; y < height; y++) data[y * stride + x] = col[y];
        }
    }

    /// <summary>2D inverse 5/3 lifting. Mirror of <see cref="Forward2D"/> —
    /// vertical pass first to undo the column transform, then horizontal.
    /// Order matters: separable wavelets are invertible if and only if you
    /// undo the passes in reverse.</summary>
    public static void Inverse2D(int[] data, int width, int height, int stride)
    {
        // Vertical pass first (the last forward pass).
        var col = new int[height];
        for (var x = 0; x < width; x++)
        {
            for (var y = 0; y < height; y++) col[y] = data[y * stride + x];
            Inverse1D(col);
            for (var y = 0; y < height; y++) data[y * stride + x] = col[y];
        }

        // Horizontal pass last.
        for (var y = 0; y < height; y++)
            Inverse1D(data.AsSpan(y * stride, width));
    }
}
