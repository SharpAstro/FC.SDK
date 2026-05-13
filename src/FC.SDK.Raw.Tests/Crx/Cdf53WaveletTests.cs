using FC.SDK.Raw.Crx;
using Shouldly;
using System;
using Xunit;

namespace FC.SDK.Raw.Tests.Crx;

/// <summary>
/// Round-trip tests for the CDF 5/3 integer-lifting wavelet
/// (<see cref="Cdf53Wavelet"/>). The integer lifting is reversible by
/// construction — the floor-rounding biases in the predict and update
/// steps exactly cancel under the inverse — so the test bar is bit-exact
/// equality after forward+inverse.
///
/// <para>Wavelet correctness can't be verified by inspection of the
/// transformed values (they're context-dependent integer combinations);
/// we instead generate random integer signals across a range of sizes
/// and boundary conditions, transform them, invert them, and check that
/// the result matches the original signal byte-for-byte.</para>
/// </summary>
public class Cdf53WaveletTests
{
    [Theory]
    [InlineData(2)]       // smallest valid
    [InlineData(3)]       // smallest odd
    [InlineData(8)]
    [InlineData(33)]      // odd, mid-size
    [InlineData(256)]
    [InlineData(1025)]    // odd, large
    public void Forward1D_Inverse1D_RoundTripsBitExact(int length)
    {
        var rng = new Random(Seed:length);
        var original = new int[length];
        for (var i = 0; i < length; i++)
            original[i] = rng.Next(-16384, 16384); // 15-bit signed range (typical wavelet coefficient magnitudes)
        var work = (int[])original.Clone();

        Cdf53Wavelet.Forward1D(work);
        Cdf53Wavelet.Inverse1D(work);

        for (var i = 0; i < length; i++)
            work[i].ShouldBe(original[i], $"index {i} diverged after round-trip");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]   // a single sample; the wavelet should no-op
    public void Forward1D_DegenerateLengths_AreNoOp(int length)
    {
        var data = new int[length];
        for (var i = 0; i < length; i++) data[i] = 42 + i;
        var snapshot = (int[])data.Clone();
        Cdf53Wavelet.Forward1D(data);
        data.ShouldBe(snapshot);
    }

    [Fact]
    public void Forward1D_AllZeros_StaysZero()
    {
        // Trivial check: a zero signal must remain zero under both passes.
        var data = new int[128];
        Cdf53Wavelet.Forward1D(data);
        foreach (var v in data) v.ShouldBe(0);
    }

    [Fact]
    public void Forward1D_ConstantSignal_LowpassEqualsValueHighpassZero()
    {
        // A constant signal has zero variation at every scale — the wavelet
        // should preserve the constant in the lowpass and write zeros to the
        // highpass. Concretely, the 5/3 update bias means a constant K maps
        // to lowpass=K (for an even-length signal with reflective boundary).
        const int n = 16;
        const int k = 1000;
        var data = new int[n];
        Array.Fill(data, k);
        Cdf53Wavelet.Forward1D(data);

        // Even indices = lowpass; odd = highpass.
        for (var i = 0; i < n; i += 2) data[i].ShouldBe(k, $"lowpass[{i / 2}] should equal constant");
        for (var i = 1; i < n; i += 2) data[i].ShouldBe(0, $"highpass[{i / 2}] should be zero for constant signal");
    }

    [Theory]
    [InlineData(4, 4)]
    [InlineData(16, 16)]
    [InlineData(33, 17)]  // both odd
    [InlineData(64, 48)]
    public void Forward2D_Inverse2D_RoundTripsBitExact(int width, int height)
    {
        var rng = new Random(Seed:width * 1000 + height);
        var stride = width;
        var original = new int[stride * height];
        for (var i = 0; i < original.Length; i++)
            original[i] = rng.Next(-8192, 8192);
        var work = (int[])original.Clone();

        Cdf53Wavelet.Forward2D(work, width, height, stride);
        Cdf53Wavelet.Inverse2D(work, width, height, stride);

        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var idx = y * stride + x;
            work[idx].ShouldBe(original[idx], $"pixel ({x}, {y}) diverged after 2D round-trip");
        }
    }

    [Fact]
    public void Forward2D_WithStrideLargerThanWidth_OnlyTransformsActiveRegion()
    {
        // Use a 4-pixel-wider buffer than the active region. The wavelet must
        // touch only the active columns; the padding columns must remain at
        // their sentinel value to confirm we didn't read or write past width.
        const int width = 8;
        const int height = 8;
        const int stride = 12;
        var data = new int[stride * height];
        const int sentinel = 0x7FFF7FFF;
        Array.Fill(data, sentinel);

        // Populate active region with non-zero values
        var rng = new Random(42);
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
            data[y * stride + x] = rng.Next(-1000, 1000);

        var original = (int[])data.Clone();
        Cdf53Wavelet.Forward2D(data, width, height, stride);
        Cdf53Wavelet.Inverse2D(data, width, height, stride);

        // Active region must round-trip.
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
            data[y * stride + x].ShouldBe(original[y * stride + x]);

        // Padding columns must be untouched.
        for (var y = 0; y < height; y++)
        for (var x = width; x < stride; x++)
            data[y * stride + x].ShouldBe(sentinel);
    }
}
