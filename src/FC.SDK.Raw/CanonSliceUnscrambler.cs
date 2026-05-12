using System;

namespace FC.SDK.Raw;

/// <summary>
/// Reorders the flat sample stream from a Canon CR2 lossless JPEG into the
/// sensor's row-major Bayer layout. Mirrors dcraw's <c>lossless_jpeg_load_raw</c>
/// (CR2 branch): treats the JPEG as a flat 1D stream of samples and indexes
/// each into <c>(row, col)</c> via the CR2Slice descriptor. The fact that the
/// JPEG carries 2 (or 4) components per pixel is just an encoding detail for
/// predictive coding — adjacent samples in the flat stream represent adjacent
/// sensor columns regardless of how the encoder split them into components.
///
/// Tag 0xC640 (CR2Slice) is 3 SHORT values: <c>[slice_count - 1, sliceWidth,
/// lastSliceWidth]</c>. <c>[0, w, w]</c> means a single slice (the entire
/// image, no unscramble required) and is handled as a fast-path memcpy.
///
/// Per dcraw: each slice owns <c>sliceWidth * sensorHeight</c> samples in the
/// flat stream, laid out row-major (full slice rows in order). The first slice
/// fills sensor columns <c>[0, sliceWidth)</c>, the second
/// <c>[sliceWidth, 2*sliceWidth)</c>, and so on. The last slice may be
/// narrower (<c>lastSliceWidth</c>).
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
