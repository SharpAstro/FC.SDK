using Shouldly;
using Xunit;

namespace FC.SDK.Raw.Tests;

/// <summary>
/// Unit tests for the Canon CR2 slice unscramble. Uses synthetic sample streams
/// — no fixture required — so these run on every CI box. End-to-end CR2 decode
/// (with the real EOS 6D file) lives in <see cref="Cr2EndToEndTests"/>.
///
/// The unscramble mirrors dcraw's <c>lossless_jpeg_load_raw</c>: the flat
/// sample stream is partitioned into slices, each slice owning
/// <c>sliceWidth * sensorHeight</c> samples laid out row-major.
/// </summary>
public class CanonSliceUnscramblerTests
{
    [Fact]
    public void SingleSlice_IsIdentity()
    {
        // 1 slice = no reordering. The samples are already in output raster order;
        // the unscrambler should just copy them through verbatim (fast-path).
        var samples = new ushort[12];
        for (var i = 0; i < 12; i++) samples[i] = (ushort)i;

        var result = CanonSliceUnscrambler.Unscramble(samples,
            outputWidth: 4, outputHeight: 3, sliceCount: 1, sliceWidth: 4, lastSliceWidth: 4);

        result.ShouldBe(samples);
    }

    [Fact]
    public void TwoEqualSlices_InterleavesByRow()
    {
        // Output: 4 cols × 2 rows, 2 slices of width 2.
        //   row 0: [s0_r0c0, s0_r0c1, s1_r0c0, s1_r0c1]
        //   row 1: [s0_r1c0, s0_r1c1, s1_r1c0, s1_r1c1]
        // Sample stream: all of slice 0 first (row-major within slice), then all of slice 1.
        //   slice 0 = [A0, A1, A2, A3]
        //   slice 1 = [B0, B1, B2, B3]
        ushort[] samples = [0xA0, 0xA1, 0xA2, 0xA3, 0xB0, 0xB1, 0xB2, 0xB3];

        var result = CanonSliceUnscrambler.Unscramble(samples,
            outputWidth: 4, outputHeight: 2, sliceCount: 2, sliceWidth: 2, lastSliceWidth: 2);

        result.ShouldBe(new ushort[] { 0xA0, 0xA1, 0xB0, 0xB1, 0xA2, 0xA3, 0xB2, 0xB3 });
    }

    [Fact]
    public void NarrowerLastSlice_HandlesAsymmetricLayout()
    {
        // Output: 5 cols × 2 rows. 2 slices: slice 0 is 3 wide, last slice (idx 1) is 2 wide.
        //   slice 0 = [A0..A5]  (3 cols × 2 rows)
        //   slice 1 = [B0..B3]  (2 cols × 2 rows)
        // Expected:
        //   row 0: [A0, A1, A2, B0, B1]
        //   row 1: [A3, A4, A5, B2, B3]
        ushort[] samples = [0xA0, 0xA1, 0xA2, 0xA3, 0xA4, 0xA5, 0xB0, 0xB1, 0xB2, 0xB3];

        var result = CanonSliceUnscrambler.Unscramble(samples,
            outputWidth: 5, outputHeight: 2, sliceCount: 2, sliceWidth: 3, lastSliceWidth: 2);

        result.ShouldBe(new ushort[] { 0xA0, 0xA1, 0xA2, 0xB0, 0xB1, 0xA3, 0xA4, 0xA5, 0xB2, 0xB3 });
    }

    [Fact]
    public void SampleCountMismatch_Throws()
    {
        // 8 samples ≠ 10 output pixels — should error early with a clear message rather
        // than silently truncate or read past the end of the input.
        ushort[] samples = new ushort[8];
        Should.Throw<System.IO.InvalidDataException>(() =>
            CanonSliceUnscrambler.Unscramble(samples,
                outputWidth: 5, outputHeight: 2, sliceCount: 1, sliceWidth: 5, lastSliceWidth: 5));
    }

    [Fact]
    public void EosLike_TwoSlicesOf2784_FillsCorrectColumns()
    {
        // Matches the EOS 6D _MG_7578.CR2 layout: 5568×3708 sensor, 2 slices of 2784.
        // Synthetic samples: ((slice_idx * 0x4000) ^ (y * 31) ^ x_in_slice)
        // — uniquely identifies (slice, y, x_in_slice) so we can verify layout.
        const int W = 5568, H = 3708, SLICE_W = 2784;
        var samples = new ushort[W * H];
        var idx = 0;
        for (var slice = 0; slice < 2; slice++)
        {
            for (var y = 0; y < H; y++)
            {
                for (var x = 0; x < SLICE_W; x++)
                {
                    samples[idx++] = (ushort)((slice * 0x4000) ^ (y * 31) ^ x);
                }
            }
        }

        var result = CanonSliceUnscrambler.Unscramble(samples,
            W, H, sliceCount: 2, sliceWidth: SLICE_W, lastSliceWidth: SLICE_W);

        ushort Expected(int slice, int y, int xInSlice)
            => (ushort)((slice * 0x4000) ^ (y * 31) ^ xInSlice);

        result[0].ShouldBe(Expected(0, 0, 0));
        result[SLICE_W - 1].ShouldBe(Expected(0, 0, SLICE_W - 1));
        result[SLICE_W].ShouldBe(Expected(1, 0, 0));
        result[W - 1].ShouldBe(Expected(1, 0, SLICE_W - 1));
        result[W * (H - 1)].ShouldBe(Expected(0, H - 1, 0));
        result[W * (H - 1) + SLICE_W].ShouldBe(Expected(1, H - 1, 0));
    }
}
