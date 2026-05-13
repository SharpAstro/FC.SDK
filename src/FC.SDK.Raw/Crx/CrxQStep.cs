using System;

namespace FC.SDK.Raw.Crx;

/// <summary>
/// Per-level inverse-quantization step tables for lossy cRAW (FF13).
/// <para>The raw <c>qpTable[qpHeight × qpWidth]</c> from
/// <see cref="CrxQpDecoder"/> describes the encoder's per-(8×2)-block
/// quantization parameter across the tile. <see cref="BuildLevels"/>
/// folds that into one <see cref="QStepTable"/> per wavelet decomposition
/// level — averaging neighbouring rows of qpTable to match the level's
/// coarser sampling, then mapping each averaged QP value to a multiplier
/// via the <see cref="BaseTable"/> (LibRaw's <c>q_step_tbl[6]</c>).</para>
///
/// <para>The wavelet decoder then computes each band's effective per-row
/// quantization scale as</para>
/// <code>quantVal = band.QStepBase + ((qStep[row, col] * band.QStepMult) &gt;&gt; 3)</code>
/// <para>and multiplies each decoded coefficient by <c>clamp(quantVal, 1, 0x168000)</c>
/// before feeding the inverse 5/3 lift.</para>
///
/// <para>Mirrors LibRaw's <c>crxMakeQStep</c> and the
/// <c>q_step_tbl</c> base table.</para>
/// </summary>
internal static class CrxQStep
{
    /// <summary>Base multiplier table indexed by <c>quantVal % 6</c>; the
    /// quotient <c>quantVal / 6</c> controls a power-of-two shift. Same
    /// values LibRaw uses (<c>q_step_tbl[]</c> in <c>crx.cpp</c>) — these
    /// are common JPEG-LS quantization tables that approximate a
    /// 2^(quantVal/6) geometric ramp on a 6-bin frame.</summary>
    public static readonly uint[] BaseTable = { 0x28, 0x2D, 0x33, 0x39, 0x40, 0x48 };

    /// <summary>One quantization step table at one wavelet decomposition
    /// level. <see cref="Values"/> is a flat <c>Height × Width</c>
    /// row-major u32 grid; <see cref="Width"/> matches the tile's column
    /// grid (qpWidth = ceil(tileWidth / 8)); <see cref="Height"/>
    /// shrinks with level (level-1 = qpHeight, level-2 = qpHeight/2,
    /// level-3 = qpHeight/4).</summary>
    public readonly record struct QStepTable(int Width, int Height, uint[] Values);

    /// <summary>Build per-level qStep tables from a freshly decoded
    /// qpTable. The returned array is indexed by wavelet level in
    /// LibRaw's convention: <c>[0]</c> = coarsest (level = <paramref name="levels"/>),
    /// <c>[levels-1]</c> = finest (level = 1).
    /// <para>Tile dimensions are <paramref name="tileWidth"/> ×
    /// <paramref name="tileHeight"/>; <paramref name="qpTable"/> must be
    /// the LibRaw-shape <c>ceil(h/2) × ceil(w/8)</c> array returned by
    /// <see cref="CrxQpDecoder.DecodeTile"/>.</para></summary>
    public static QStepTable[] BuildLevels(
        int[] qpTable, int tileWidth, int tileHeight, int levels)
    {
        if (levels < 1 || levels > 3)
            throw new ArgumentOutOfRangeException(nameof(levels), "levels must be 1..3");
        ArgumentNullException.ThrowIfNull(qpTable);

        var qpWidth = CeilDiv(tileWidth, 8);
        var qpHeight = CeilDiv(tileHeight, 2);
        var qpHeight4 = CeilDiv(tileHeight, 4);
        var qpHeight8 = CeilDiv(tileHeight, 8);
        if (qpTable.Length != qpWidth * qpHeight)
        {
            throw new ArgumentException(
                $"qpTable length {qpTable.Length} does not match expected {qpWidth * qpHeight} " +
                $"for tile {tileWidth}x{tileHeight} (qpWidth={qpWidth}, qpHeight={qpHeight}).",
                nameof(qpTable));
        }

        var result = new QStepTable[levels];
        // Walk levels = 3 → 1 in LibRaw's case-fallthrough order so the
        // output array's [0] is the coarsest level (which is what
        // crxIdwt53FilterDecode reads via `qStep + level` for the
        // highest level index).
        var slot = 0;
        if (levels >= 3)
        {
            result[slot++] = BuildLevel3(qpTable, qpWidth, qpHeight, qpHeight8);
        }
        if (levels >= 2)
        {
            result[slot++] = BuildLevel2(qpTable, qpWidth, qpHeight, qpHeight4);
        }
        // Level 1 (finest) is qpTable itself, just lookup-mapped.
        result[slot] = BuildLevel1(qpTable, qpWidth, qpHeight);
        return result;
    }

    /// <summary>Level 3 — averages 4 rows of qpTable into one
    /// (<c>qpHeight8 × qpWidth</c>). The "negative-rounding nonsense"
    /// LibRaw apologises for is preserved verbatim — pre-shifting
    /// negative averages by 3 instead of 4 keeps the encoder/decoder
    /// rounding consistent.</summary>
    private static QStepTable BuildLevel3(int[] qpTable, int qpWidth, int qpHeight, int qpHeight8)
    {
        var values = new uint[qpHeight8 * qpWidth];
        var write = 0;
        for (var qpRow = 0; qpRow < qpHeight8; qpRow++)
        {
            var r0 = qpWidth * Math.Min(4 * qpRow, qpHeight - 1);
            var r1 = qpWidth * Math.Min(4 * qpRow + 1, qpHeight - 1);
            var r2 = qpWidth * Math.Min(4 * qpRow + 2, qpHeight - 1);
            var r3 = qpWidth * Math.Min(4 * qpRow + 3, qpHeight - 1);
            for (var qpCol = 0; qpCol < qpWidth; qpCol++)
            {
                var quantVal = qpTable[r0 + qpCol] + qpTable[r1 + qpCol]
                             + qpTable[r2 + qpCol] + qpTable[r3 + qpCol];
                // LibRaw: `quantVal = ((quantVal < 0) * 3 + quantVal) >> 2;`
                // — i.e. round-toward-zero division by 4 when negative.
                quantVal = ((quantVal < 0 ? 3 : 0) + quantVal) >> 2;
                values[write++] = MapToQStep(quantVal);
            }
        }
        return new QStepTable(qpWidth, qpHeight8, values);
    }

    /// <summary>Level 2 — averages 2 rows of qpTable. Plain
    /// <c>(a + b) / 2</c>; the negative-rounding case from level 3
    /// doesn't repeat here (LibRaw's comment "not sure why level 3 is
    /// different" notwithstanding).</summary>
    private static QStepTable BuildLevel2(int[] qpTable, int qpWidth, int qpHeight, int qpHeight4)
    {
        var values = new uint[qpHeight4 * qpWidth];
        var write = 0;
        for (var qpRow = 0; qpRow < qpHeight4; qpRow++)
        {
            var r0 = qpWidth * Math.Min(2 * qpRow, qpHeight - 1);
            var r1 = qpWidth * Math.Min(2 * qpRow + 1, qpHeight - 1);
            for (var qpCol = 0; qpCol < qpWidth; qpCol++)
            {
                var quantVal = (qpTable[r0 + qpCol] + qpTable[r1 + qpCol]) / 2;
                values[write++] = MapToQStep(quantVal);
            }
        }
        return new QStepTable(qpWidth, qpHeight4, values);
    }

    /// <summary>Level 1 — direct passthrough of qpTable through
    /// <see cref="MapToQStep"/>.</summary>
    private static QStepTable BuildLevel1(int[] qpTable, int qpWidth, int qpHeight)
    {
        var values = new uint[qpHeight * qpWidth];
        for (var i = 0; i < qpTable.Length; i++)
        {
            values[i] = MapToQStep(qpTable[i]);
        }
        return new QStepTable(qpWidth, qpHeight, values);
    }

    /// <summary>QP-index → multiplier mapping. For <c>q/6 &lt; 6</c> the
    /// step is right-shifted (= power-of-two divide); for <c>q/6 &gt;= 6</c>
    /// it's left-shifted (= power-of-two multiply). The <c>& 0x1f</c>
    /// mask matches LibRaw's defensive bound on the shift count.</summary>
    private static uint MapToQStep(int quantVal)
    {
        var idx = ((quantVal % 6) + 6) % 6;  // C# % can be negative; clamp into [0..5]
        var div = quantVal / 6;
        if (div >= 6)
        {
            return BaseTable[idx] << ((div - 6) & 0x1F);
        }
        // LibRaw allows div to go negative; >> with a negative shift is UB in C
        // but works on x86 (mask & 31). In C# uint shifts mask the count by 31
        // already, so the natural expression is safe.
        return BaseTable[idx] >> (6 - div);
    }

    private static int CeilDiv(int a, int b) => (a + b - 1) / b;
}
