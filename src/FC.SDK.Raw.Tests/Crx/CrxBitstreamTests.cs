using FC.SDK.Raw.Crx;
using Shouldly;
using Xunit;

namespace FC.SDK.Raw.Tests.Crx;

/// <summary>
/// Unit tests for the MSB-first bit reader (<see cref="CrxBitstream"/>).
/// Hand-crafted byte streams exercise the boundary cases:
/// <list type="bullet">
/// <item>Reading bits across a byte boundary</item>
/// <item>Reading bits across a 32-bit word boundary (the refill path)</item>
/// <item>GetZeros across multiple zero bytes (the slow-path unary scan)</item>
/// <item>GetZeros at end-of-stream</item>
/// </list>
/// </summary>
public class CrxBitstreamTests
{
    [Fact]
    public void GetBits_SingleByte_ReadsMsbFirst()
    {
        // 0xA5 = 1010 0101. MSB-first reads should produce:
        //   GetBits(1) = 1
        //   GetBits(1) = 0
        //   GetBits(2) = 0b10 = 2
        //   GetBits(4) = 0b0101 = 5
        var stream = new CrxBitstream([0xA5]);
        stream.GetBits(1).ShouldBe(1u);
        stream.GetBits(1).ShouldBe(0u);
        stream.GetBits(2).ShouldBe(2u);
        stream.GetBits(4).ShouldBe(5u);
    }

    [Fact]
    public void GetBits_AcrossByteBoundary_AssemblesCorrectly()
    {
        // 0xF0 0x0F = 1111 0000 0000 1111
        //   GetBits(4) = 1111
        //   GetBits(4) = 0000   <- straddles byte boundary
        //   GetBits(4) = 0000   <- straddles byte boundary
        //   GetBits(4) = 1111
        var stream = new CrxBitstream([0xF0, 0x0F]);
        stream.GetBits(4).ShouldBe(0xFu);
        stream.GetBits(4).ShouldBe(0x0u);
        stream.GetBits(4).ShouldBe(0x0u);
        stream.GetBits(4).ShouldBe(0xFu);
    }

    [Fact]
    public void GetBits_AcrossWordBoundary_AssemblesCorrectly()
    {
        // 5 bytes = 40 bits. Read 31 then 9 to force a refill mid-second-read.
        //   bytes: 0x12 0x34 0x56 0x78 0x9A
        //   first 32 bits: 0x12345678
        //   GetBits(31) = top 31 bits of 0x12345678 = 0x091A2B3C
        //   then 1 bit remains: bit 0 of 0x12345678 = 0
        //   GetBits(9) = the remaining 1 bit (0) + next 8 bits (0x9A) = 0b0_10011010 = 0x9A
        var stream = new CrxBitstream([0x12, 0x34, 0x56, 0x78, 0x9A]);
        stream.GetBits(31).ShouldBe(0x091A2B3Cu);
        stream.GetBits(9).ShouldBe(0x9Au);
    }

    [Fact]
    public void GetZeros_SingleByteWithOne_CountsLeadingZeros()
    {
        // 0x40 = 0100 0000. One leading zero before the 1, then 6 trailing zeros.
        // GetZeros should return 1 (consuming "01"), leaving "000000" in the
        // accumulator.
        var stream = new CrxBitstream([0x40, 0x80]);
        stream.GetZeros().ShouldBe(1);
        // The next GetZeros scans through 6 zeros, then a refill brings in
        // 0x80 = 10000000 which starts with a 1 -> contributes 0 more zeros.
        // Total: 6.
        stream.GetZeros().ShouldBe(6);
    }

    [Fact]
    public void GetZeros_TerminatorAtBitZero_ReturnsZero()
    {
        // 0x80 = 1000 0000. The very first bit is 1, so GetZeros = 0.
        var stream = new CrxBitstream([0x80]);
        stream.GetZeros().ShouldBe(0);
    }

    [Fact]
    public void GetZeros_AcrossMultipleZeroBytes_AccumulatesCorrectly()
    {
        // Three zero bytes (24 zeros) followed by 0x10 = 0001 0000.
        // The 1 bit is at position 3 within the 4th byte, so:
        //   24 (3 zero bytes) + 3 (zeros within 4th byte) = 27 zeros total.
        var stream = new CrxBitstream([0x00, 0x00, 0x00, 0x10]);
        stream.GetZeros().ShouldBe(27);
    }

    [Fact]
    public void GetBits_AfterGetZeros_ContinuesFromCorrectPosition()
    {
        // 0x21 = 0010 0001 = "00 1 00001"
        //   GetZeros -> 2 (consumes "001")
        //   GetBits(5) -> 0b00001 = 1
        var stream = new CrxBitstream([0x21]);
        stream.GetZeros().ShouldBe(2);
        stream.GetBits(5).ShouldBe(1u);
    }

    [Fact]
    public void IsEndOfStream_AfterFullConsumption_ReportsTrue()
    {
        var stream = new CrxBitstream([0xFF]);
        stream.IsEndOfStream.ShouldBeFalse();
        stream.GetBits(8);
        stream.IsEndOfStream.ShouldBeTrue();
    }
}
