using System;

namespace FC.SDK.Raw;

/// <summary>
/// Reorders a flat lossless-JPEG sample stream into the Canon CR2 sensor layout.
///
/// Canon stores the raw image as N vertical slices, each <c>sliceWidth</c> output
/// columns wide (the last slice may be narrower — <c>lastSliceWidth</c>). The
/// slices are then concatenated into a single lossless-JPEG payload — but
/// <em>not</em> as side-by-side strips of one big image; rather as a flat
/// concatenation in scan order: slice 0's pixels (row-major, slice_w columns ×
/// image_h rows), then slice 1's pixels, and so on.
///
/// The total sample count after JPEG decode equals
/// <c>output_w * output_h</c> regardless of how the JPEG encoder chose to lay
/// out its (width × height × components) raster, so the unscramble is purely a
/// sample-index → (row, col) mapping that doesn't need the JPEG dimensions.
///
/// Tag 0xC640 (CR2Slice) is 3 SHORT values: <c>[slice_count - 1, sliceWidth,
/// lastSliceWidth]</c>. <c>[0, w, w]</c> means a single slice (the entire image,
/// no unscramble required) and is handled as a fast-path memcpy.
/// </summary>
internal static class CanonSliceUnscrambler
{
    internal static ushort[] Unscramble(
        ReadOnlySpan<ushort> samples,
        int outputWidth,
        int outputHeight,
        int sliceCount,
        int sliceWidth,
        int lastSliceWidth)
    {
        var output = new ushort[outputWidth * outputHeight];
        if (samples.Length != output.Length)
        {
            throw new InvalidDataException(
                $"Sample count {samples.Length} ≠ expected {output.Length} ({outputWidth}×{outputHeight}). " +
                "Either the JPEG dimensions or the CR2Slice tag are inconsistent with the IFD ImageWidth/ImageLength.");
        }

        // Single-slice fast path — samples are already in output raster order.
        if (sliceCount == 1)
        {
            samples.CopyTo(output);
            return output;
        }

        var srcIdx = 0;
        for (var slice = 0; slice < sliceCount; slice++)
        {
            var sliceW = (slice == sliceCount - 1) ? lastSliceWidth : sliceWidth;
            var xOffset = slice * sliceWidth;
            for (var y = 0; y < outputHeight; y++)
            {
                var dstRowBase = y * outputWidth + xOffset;
                samples.Slice(srcIdx, sliceW).CopyTo(output.AsSpan(dstRowBase, sliceW));
                srcIdx += sliceW;
            }
        }
        return output;
    }
}
