using System.Buffers.Binary;
using FC.SDK.Canon;
using Shouldly;
using Xunit;

namespace FC.SDK.Tests;

/// <summary>
/// The live-view exposure histogram — envelope record 17.
/// </summary>
/// <remarks>
/// <para>
/// This is the only live metering an EOS volunteers over PTP: there is no operation that reports a
/// metered exposure value, so on a body whose dial is at Manual the histogram is the only way to
/// learn whether a frame is exposed without spending a shutter actuation to find out.
/// </para>
/// <para>
/// The record was previously called a histogram on the strength of its size alone — and 4096 bytes
/// of anything satisfies "4 channels x 256 bins x uint32". The load-bearing check is therefore that
/// four histograms of one image count the same pixels; a payload whose groups disagree is not laid
/// out the way this reads it, and a wrong exposure readout is worse than none. These tests pin the
/// rejection as firmly as the happy path.
/// </para>
/// </remarks>
public class EvfHistogramTests
{
    private const int Bins = 256;

    /// <summary>An envelope carrying exactly one record 17 with the given four channels.</summary>
    private static byte[] Envelope(uint[] luma, uint[] red, uint[] green, uint[] blue)
    {
        var payload = new byte[4 * Bins * sizeof(uint)];
        var at = 0;
        foreach (var channel in new[] { luma, red, green, blue })
            foreach (var value in channel)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(at), value);
                at += sizeof(uint);
            }
        return Record(17, payload);
    }

    private static byte[] Record(uint type, byte[] payload)
    {
        var buffer = new byte[8 + payload.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, (uint)buffer.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(4), type);
        payload.CopyTo(buffer.AsSpan(8));
        return buffer;
    }

    /// <summary>A channel with <paramref name="count"/> pixels all sitting in one bin.</summary>
    private static uint[] AllAt(int bin, uint count)
    {
        var bins = new uint[Bins];
        bins[bin] = count;
        return bins;
    }

    private static CanonEvfHistogram? Decode(byte[] envelope) =>
        CanonViewfinderFrame.TryGetHistogram(CanonViewfinderFrame.ParseRecords(envelope));

    [Fact]
    public void Four_channels_that_agree_decode()
    {
        var h = Decode(Envelope(AllAt(128, 1000), AllAt(200, 1000), AllAt(128, 1000), AllAt(50, 1000)));

        h.ShouldNotBeNull();
        h.Value.PixelCount.ShouldBe(1000);
        h.Value.Red[200].ShouldBe(1000u);
        h.Value.Blue[50].ShouldBe(1000u);
    }

    /// <summary>
    /// The structural check, and the reason this decoder is trustworthy at all. Four histograms of
    /// one frame necessarily count the same pixels, so groups that disagree mean the 4x256 reading
    /// is wrong — a different record layout, or a different body's numbering.
    /// </summary>
    [Fact]
    public void Channels_that_disagree_on_the_pixel_count_are_rejected()
    {
        var h = Decode(Envelope(AllAt(128, 1000), AllAt(128, 999), AllAt(128, 1000), AllAt(128, 1000)));

        h.ShouldBeNull();
    }

    /// <summary>
    /// A body that sends the record but has not metered anything yet. Zero pixels is not a reading,
    /// and reporting it as "mean 0%" would look exactly like a pitch-black frame.
    /// </summary>
    [Fact]
    public void An_all_zero_histogram_is_not_a_reading()
    {
        Decode(Record(17, new byte[4096])).ShouldBeNull();
    }

    [Fact]
    public void A_truncated_payload_is_rejected()
    {
        Decode(Record(17, new byte[4095])).ShouldBeNull();
    }

    [Fact]
    public void An_envelope_with_no_histogram_record_yields_null()
    {
        Decode(Record(18, new byte[16])).ShouldBeNull();
    }

    /// <summary>
    /// Mean level is reported as a fraction of full scale, so a caller can compare it against the
    /// ~18% a well-exposed average scene sits at without knowing the bin count.
    /// </summary>
    [Theory]
    [InlineData(0, 0.0)]
    [InlineData(255, 1.0)]
    [InlineData(128, 128.0 / 255.0)]
    public void Mean_level_is_a_fraction_of_full_scale(int bin, double expected)
    {
        var h = Decode(Envelope(AllAt(bin, 500), AllAt(bin, 500), AllAt(bin, 500), AllAt(bin, 500)));

        h!.Value.MeanLevel.ShouldBe(expected, 0.001);
    }

    /// <summary>
    /// The measurement that would have saved a wasted focus run: those frames metered at 1% of full
    /// scale, and a sharpness comparison on them was reading noise rather than detail.
    /// </summary>
    [Fact]
    public void A_grossly_underexposed_frame_reads_far_below_a_mid_grey()
    {
        var h = Decode(Envelope(AllAt(3, 400), AllAt(3, 400), AllAt(3, 400), AllAt(3, 400)));

        h!.Value.MeanLevel.ShouldBeLessThan(0.02);
    }

    [Fact]
    public void Clipping_is_reported_at_both_ends()
    {
        var bins = new uint[Bins];
        bins[0] = 250;      // crushed
        bins[128] = 500;
        bins[255] = 250;    // blown
        var h = Decode(Envelope(bins, bins, bins, bins));

        h!.Value.ClippedShadows.ShouldBe(0.25, 0.001);
        h.Value.ClippedHighlights.ShouldBe(0.25, 0.001);
    }

    /// <summary>
    /// A public struct can always be default-constructed, so every member has to survive null bin
    /// arrays. Nothing in the SDK produces one, which is exactly why it would go unnoticed.
    /// </summary>
    [Fact]
    public void A_default_histogram_answers_zeroes_rather_than_throwing()
    {
        var h = default(CanonEvfHistogram);

        h.PixelCount.ShouldBe(0);
        h.MeanLevel.ShouldBe(0);
        h.ClippedHighlights.ShouldBe(0);
        h.ClippedShadows.ShouldBe(0);
        h.Percentile(0.5).ShouldBe(0);
        h.ToString().ShouldNotBeNull();
    }

    [Fact]
    public void Percentiles_walk_the_cumulative_distribution()
    {
        var bins = new uint[Bins];
        bins[10] = 500;
        bins[200] = 500;
        var h = Decode(Envelope(bins, bins, bins, bins));

        h!.Value.Percentile(0.25).ShouldBe(10 / 255.0, 0.001);
        h.Value.Percentile(0.99).ShouldBe(200 / 255.0, 0.001);
    }
}
