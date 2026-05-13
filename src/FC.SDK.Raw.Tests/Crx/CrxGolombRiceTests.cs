using FC.SDK.Raw.Crx;
using Shouldly;
using Xunit;

namespace FC.SDK.Raw.Tests.Crx;

/// <summary>
/// Unit tests for the adaptive Golomb-Rice symbol decoder
/// (<see cref="CrxGolombRice"/>). Each test hand-crafts a bitstream
/// that produces a known sequence of (q, remainder) pairs and verifies
/// the symbol values + the K-adaptation behaviour.
///
/// <para>Coding convention recap: each symbol is encoded as
/// <c>q</c> zero bits followed by a 1 (the unary quotient terminator),
/// followed by <c>K</c> remainder bits MSB-first. The combined value is
/// <c>(q &lt;&lt; K) | remainder</c>. The sign-fold (<see cref="CrxGolombRice.FoldSign"/>)
/// then maps unsigned bit-codes to signed integers via zigzag.</para>
/// </summary>
public class CrxGolombRiceTests
{
    [Fact]
    public void DecodeSymbol_K0_PureUnary()
    {
        // K=0 → no remainder, value = q. Encode the sequence (0, 1, 2, 3):
        //   "1" + "01" + "001" + "0001" = "1 01 001 0001"  (10 bits)
        // Packed MSB-first: 1010 0100 01 = 0xA4 0x40
        var stream = new CrxBitstream([0xA4, 0x40]);
        var rice = new CrxGolombRice(maxK: 15);

        rice.DecodeSymbol(stream).ShouldBe(0u);
        rice.DecodeSymbol(stream).ShouldBe(1u);
        rice.DecodeSymbol(stream).ShouldBe(2u);
        rice.DecodeSymbol(stream).ShouldBe(3u);
    }

    [Fact]
    public void DecodeSymbol_K0_AdaptsUpwardOnLargeValues()
    {
        // Decode a single symbol with q=6 (value 6 at K=0):
        //   bitCode = 6, oldK = 0
        //   decrement: bitCode < (1 << 0 >> 1) == bitCode < 0 → false → 0
        //   increment 1: (bitCode >> 0) > 2 → 6 > 2 → true → +1
        //   increment 2: (bitCode >> 0) > 5 → 6 > 5 → true → +1
        //   newK = 0 + 0 + 1 + 1 = 2
        // Encoding: "0000001" (6 zeros + 1) = 7 bits = 0xFE (next bit padding 0)
        // Packed MSB-first: 0000 0010 = 0x02
        var stream = new CrxBitstream([0x02]);
        var rice = new CrxGolombRice(maxK: 15);
        rice.KParam.ShouldBe(0);

        var value = rice.DecodeSymbol(stream);
        value.ShouldBe(6u);
        rice.KParam.ShouldBe(2);
    }

    [Fact]
    public void DecodeSymbol_KPositive_CombinesQuotientAndRemainder()
    {
        // K=3, q=2, remainder=5 → bitCode = (2 << 3) | 5 = 21
        //   Bitstream: "001" (q=2, unary "001") + "101" (3-bit remainder = 5)
        //   = "001 101" = 6 bits packed MSB-first as 0011 0100 = 0x34
        var stream = new CrxBitstream([0x34]);
        var rice = new CrxGolombRice(maxK: 15) { KParam = 3 };

        var value = rice.DecodeSymbol(stream);
        value.ShouldBe(21u);

        // Adaptation: oldK = 3, bitCode = 21
        //   decrement: 21 < (1 << 3 >> 1) == 21 < 4 → false → 0
        //   inc1: 21 >> 3 = 2, 2 > 2 → false → 0
        //   inc2: 21 >> 3 = 2, 2 > 5 → false → 0
        //   newK = 3 + 0 + 0 + 0 = 3
        rice.KParam.ShouldBe(3);
    }

    [Fact]
    public void DecodeSymbol_LongQuotient_HitsEscapePath()
    {
        // q >= 41 triggers the 21-bit escape. Encode q = 41 (41 zero bits +
        // a 1 terminator = 42 bits), then 21 raw bits with value 0x123456.
        //
        // 41 zeros + "1" + 21 bits "0001 0010 0011 0100 0101 0110":
        //   bits 0..40 = 0
        //   bit 41 = 1
        //   bits 42..62 = 0x123456 (21-bit code)
        //
        // We don't need to hand-pack 8 bytes of bits — instead, use the
        // bitstream constructor on a precomputed byte buffer. Build the
        // 63-bit stream:
        var bits = new System.Collections.Generic.List<int>();
        for (var i = 0; i < 41; i++) bits.Add(0);
        bits.Add(1);
        // 21 bits of 0x123456 (LSBs of 32-bit value, MSB-first)
        for (var i = 20; i >= 0; i--) bits.Add((0x123456 >> i) & 1);
        // Pad to byte boundary with zeros
        while (bits.Count % 8 != 0) bits.Add(0);

        var bytes = new byte[bits.Count / 8];
        for (var i = 0; i < bits.Count; i++)
            if (bits[i] != 0) bytes[i / 8] |= (byte)(1 << (7 - i % 8));

        var stream = new CrxBitstream(bytes);
        var rice = new CrxGolombRice(maxK: 15);
        var value = rice.DecodeSymbol(stream);
        value.ShouldBe(0x123456u);
    }

    [Theory]
    [InlineData(0u, 0)]
    [InlineData(1u, -1)]
    [InlineData(2u, 1)]
    [InlineData(3u, -2)]
    [InlineData(4u, 2)]
    [InlineData(5u, -3)]
    [InlineData(10u, 5)]
    [InlineData(11u, -6)]
    public void FoldSign_ZigzagMapping(uint bitCode, int expectedSigned)
    {
        CrxGolombRice.FoldSign(bitCode).ShouldBe(expectedSigned);
    }

    [Fact]
    public void DecodeSymbol_MaxK_ClampsAtCeiling()
    {
        // Force a long run of high values that would push K above MaxK
        // if unclamped. With maxK=7, KParam must never exceed 7.
        // We'll decode 20 large symbols in a row.
        var rice = new CrxGolombRice(maxK: 7) { KParam = 7 };
        // Build 20 symbols of value 100 at K=7: each is q=0 (one bit "1")
        // plus 7 bits of remainder = 8 bits total. So 20 × 8 = 160 bits = 20 bytes.
        // Value 100 at K=7: bitCode = (q << 7) | rem; q=0, rem=100 → bits "1 1100100".
        var bits = new System.Collections.Generic.List<int>();
        for (var sym = 0; sym < 20; sym++)
        {
            bits.Add(1); // q=0 (just the terminator)
            for (var i = 6; i >= 0; i--) bits.Add((100 >> i) & 1);
        }
        var bytes = new byte[bits.Count / 8];
        for (var i = 0; i < bits.Count; i++)
            if (bits[i] != 0) bytes[i / 8] |= (byte)(1 << (7 - i % 8));

        var stream = new CrxBitstream(bytes);
        for (var i = 0; i < 20; i++)
        {
            rice.DecodeSymbol(stream).ShouldBe(100u);
            rice.KParam.ShouldBeLessThanOrEqualTo(7);
        }
    }
}
