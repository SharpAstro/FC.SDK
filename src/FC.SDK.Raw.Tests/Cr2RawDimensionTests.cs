using System.IO;
using SharpAstro.Tiff;
using Shouldly;
using Xunit;

namespace FC.SDK.Raw.Tests;

/// <summary>
/// Dimension resolution for the CR2 raw IFD, in particular the older files that do not state it.
/// </summary>
/// <remarks>
/// The regression these exist for: a real EOS 450D CR2 threw
/// <c>KeyNotFoundException: The given key '256' was not present in the dictionary</c>. DIGIC III
/// bodies write a raw IFD holding only <c>[259, 273, 279, 50648, 50656, 50752, 50885]</c> — no
/// ImageWidth, no ImageLength — and the decoder indexed both unconditionally. Every fixture in this
/// repo is from a newer body that does state them, so nothing here caught it.
///
/// Synthetic throughout: the arithmetic is the whole subject, and pinning it needs no 11 MB file.
/// The values are real ones read off both bodies, so a wrong formula fails against hardware truth
/// rather than against a number invented to match the implementation.
/// </remarks>
public class Cr2RawDimensionTests
{
    private static Dictionary<ushort, Cr2IfdReader.Entry> NoDimensions() => [];

    private static Dictionary<ushort, Cr2IfdReader.Entry> WithDimensions(ushort width, ushort height) => new()
    {
        [0x0100] = new Cr2IfdReader.Entry(TiffFieldType.Short, 1, [(byte)(width & 0xFF), (byte)(width >> 8)]),
        [0x0101] = new Cr2IfdReader.Entry(TiffFieldType.Short, 1, [(byte)(height & 0xFF), (byte)(height >> 8)]),
    };

    /// <summary>
    /// The 5D-era fixture states <c>(2, 2784, 2784)</c> and ImageWidth 5568, so it is ground truth
    /// for the slice sum: a formula that cannot reproduce a number the file also spells out is
    /// wrong, whatever it does on the body that omits it.
    /// </summary>
    [Fact]
    public void Slice_sum_reproduces_the_width_a_newer_file_states_outright()
    {
        var (width, height) = Cr2Decoder.ResolveRawDimensions(
            NoDimensions(), fileIsLE: true,
            jpegSamples: 5568L * 3708, jpegWidth: 2784, jpegComponents: 2,
            slice: (Count: 2, SliceWidth: 2784, LastSliceWidth: 2784));

        width.ShouldBe(5568);
        height.ShouldBe(3708);
    }

    /// <summary>The 450D itself: three slices, and the case that was crashing.</summary>
    [Fact]
    public void A_450D_raw_ifd_without_dimensions_derives_them_from_the_slice_layout()
    {
        var (width, height) = Cr2Decoder.ResolveRawDimensions(
            NoDimensions(), fileIsLE: true,
            jpegSamples: 4312L * 2876, jpegWidth: 1438, jpegComponents: 4,
            slice: (Count: 3, SliceWidth: 1440, LastSliceWidth: 1432));

        // 2 * 1440 + 1432. The active area a 450D reports is 4272 x 2848; the raw frame is larger
        // because the masked border is still attached at this stage.
        width.ShouldBe(4312);
        height.ShouldBe(2876);
    }

    [Fact]
    public void Stated_dimensions_win_over_anything_derivable()
    {
        var (width, height) = Cr2Decoder.ResolveRawDimensions(
            WithDimensions(4312, 2876), fileIsLE: true,
            // Deliberately inconsistent with the tags: if the IFD is present it is the answer, and
            // the caller's own sample-count check is what catches a genuine disagreement.
            jpegSamples: 1, jpegWidth: 1, jpegComponents: 1,
            slice: (Count: 9, SliceWidth: 99, LastSliceWidth: 99));

        width.ShouldBe(4312);
        height.ShouldBe(2876);
    }

    /// <summary>No slice tag at all: one output row per JPEG row, components interleaved.</summary>
    [Fact]
    public void An_unsliced_frame_takes_its_row_from_the_jpeg_frame_header()
    {
        var (width, height) = Cr2Decoder.ResolveRawDimensions(
            NoDimensions(), fileIsLE: true,
            jpegSamples: 4000L * 3000, jpegWidth: 2000, jpegComponents: 2, slice: null);

        width.ShouldBe(4000);
        height.ShouldBe(3000);
    }

    /// <summary>
    /// A layout that does not divide is a misread, not a small rounding problem — better to say so
    /// than to hand back a plausible height and let the unscrambler produce a sheared image.
    /// </summary>
    [Fact]
    public void A_sample_count_that_does_not_divide_into_rows_is_refused()
    {
        var refusal = Should.Throw<InvalidDataException>(() => Cr2Decoder.ResolveRawDimensions(
            NoDimensions(), fileIsLE: true,
            jpegSamples: 1001, jpegWidth: 10, jpegComponents: 1,
            slice: (Count: 2, SliceWidth: 5, LastSliceWidth: 5)));

        refusal.Message.ShouldContain("could not be derived");
    }
}
