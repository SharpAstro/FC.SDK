using SdlVulkan.Renderer;
using SharpAstro.Codecs.Abstractions;
using SharpAstro.Heif;

namespace FC.SDK.Viewer;

/// <summary>
/// Turns a HEIF capture (a Canon <c>.HIF</c>, HDR PQ) into a preview for the capture pane: the file's own largest
/// preview item, which decodes in a fraction of the time and is all a pane with no zoom needs, or the full image when
/// the file carries no preview this decoder can read.
/// </summary>
/// <remarks>
/// <para>
/// The processor decodes the HEVC and stops at the Y'CbCr planes (<see cref="HdrPlanes"/>); the GPU does the rest
/// (<c>VkTexture.CreateDeferredFromYCbCr</c>): chroma upsampling, the matrix, the transfer through the decoder's own
/// code table, the reduction for the pane and the peak. What the planes cannot carry stays on the processor, as
/// before: an HLG image (its transfer mixes the channels), one with a crop, rotation or mirror (applied to R'G'B'),
/// and any picture on a device that cannot run the conversion.
/// </para>
/// <para>
/// Either way the result is float, linear light, 1.0 = 203 cd/m2 (BT.2408), which is the scale both
/// <c>HeifImageDecoder.TryDecodeRgbaFloat</c> produces and <c>VkRenderer.DrawHdrImage</c> takes. No 8-bit stage
/// exists on this path: <c>TryDecodeIntoRgba8</c> refuses PQ and HLG on purpose, and on an SDR display the
/// renderer tone-maps the same float texture itself.
/// </para>
/// <para>
/// The primaries are passed through, not converted: a Canon HDR PQ file is BT.2020, and the renderer converts
/// to the display (on an HDR panel a BT.2020 primary comes out with a negative scRGB component, which is the point).
/// </para>
/// </remarks>
internal static class HeifPreview
{
    /// <summary>The extensions decoded through this path.</summary>
    public static bool Handles(string extension) => extension is ".hif" or ".heic" or ".heif";

    /// <summary>
    /// The largest thumbnail item of the primary image (on an R7, the 1620x1080 one rather than the 320x214),
    /// or null when the file carries none this decoder can read.
    /// </summary>
    public static PreviewImage? DecodeLargestThumbnail(byte[] file, int maxDimension)
    {
        var heif = HeifReader.Read(file);
        HeifItem? best = null;
        long bestArea = 0;
        foreach (var item in heif.ThumbnailsOf(heif.PrimaryItem))
        {
            long area;
            try
            {
                var image = heif.Describe(file, item);
                area = (long)image.Width * image.Height;
            }
            catch (Exception e) when (e is InvalidDataException or NotSupportedException)
            {
                continue;
            }
            if (area > bestArea) (best, bestArea) = (item, area);
        }
        return best is null ? null : Decode(file, best, maxDimension);
    }

    /// <summary>The primary image, reduced for the pane, or null when the decoder refuses the file.</summary>
    /// <remarks>
    /// On the processor path the full float raster of an R7 is 6960x4640x16 bytes, about 517 MB, dropped as soon as
    /// it has been reduced; on the GPU path only the planes (about 129 MB of 16-bit samples) are made at all.
    /// </remarks>
    public static PreviewImage? DecodeFull(byte[] file, int maxDimension)
    {
        try
        {
            return Decode(file, null, maxDimension);
        }
        catch (Exception e) when (e is InvalidDataException or NotSupportedException)
        {
            return HeifImageDecoder.TryDecodeRgbaFloat(file, out var rgba, out var width, out var height, out var encoding)
                ? ToHdrRaster(rgba, width, height, encoding, maxDimension)
                : null;
        }
    }

    /// <summary>
    /// <paramref name="item"/> (the primary when null) as planes for the GPU, or converted here when the planes cannot
    /// carry it (HLG, a transformative property).
    /// </summary>
    private static PreviewImage Decode(byte[] file, HeifItem? item, int maxDimension)
    {
        HeifYCbCrImage planes;
        float[] table;
        try
        {
            planes = HeifDecoder.DecodeYCbCr(file, item);
            table = planes.CodeToLinearTable();
        }
        catch (NotSupportedException)
        {
            var rgb = HeifDecoder.Decode(file, item);
            return ToHdrRaster(rgb.ToLinearRgbaFloat(out var encoding), rgb.Width, rgb.Height, encoding, maxDimension);
        }

        var gpuPlanes = new YCbCrPlanes
        {
            Width = planes.Width,
            Height = planes.Height,
            BitDepth = planes.BitDepth,
            Luma = planes.Luma,
            Cb = planes.Cb,
            Cr = planes.Cr,
            ChromaWidth = planes.ChromaWidth,
            ChromaShiftX = planes.ChromaShiftX,
            ChromaShiftY = planes.ChromaShiftY,
            Kr = planes.Kr,
            Kb = planes.Kb,
            IdentityMatrix = planes.IsIdentityMatrix,
            FullRange = planes.IsFullRange,
            CodeToLinear = table,
        };
        return new HdrPlanes(gpuPlanes, ReductionFor(planes.Width, planes.Height, maxDimension), PrimariesOf(planes.ColorEncoding),
            () =>
            {
                var rgb = planes.ToRgb();
                return ToHdrRaster(rgb.ToLinearRgbaFloat(out var encoding), rgb.Width, rgb.Height, encoding, maxDimension);
            });
    }

    /// <summary>
    /// The whole factor that brings the long edge to at least <paramref name="maxDimension"/>, rounded DOWN, so a full
    /// R7 frame lands at 1740x1160, not below the 1620x1080 preview it replaces.
    /// </summary>
    private static int ReductionFor(int width, int height, int maxDimension) => Math.Max(1, Math.Max(width, height) / maxDimension);

    /// <summary>
    /// Peak nits for the SDR tone curve, from the brightest channel in linear light (1.0 = 203 cd/m2): floored at
    /// reference white (below it the curve is the identity anyway) and capped at PQ's 10000.
    /// </summary>
    public static float PeakNits(float peak) => Math.Clamp(peak * HdrColor.ReferenceWhiteNits, HdrColor.ReferenceWhiteNits, 10000f);

    private static HdrPrimaries PrimariesOf(ColorEncoding encoding) => encoding.Primaries switch
    {
        ColorPrimaries.BT2020 => HdrPrimaries.Bt2020,
        // Everything else this decoder emits is drawn as BT.709. A Display P3 HEIF would be slightly
        // desaturated; no camera file seen so far is one.
        _ => HdrPrimaries.Bt709,
    };

    /// <summary>
    /// Reduces by a whole factor so the long edge is at least <paramref name="maxDimension"/> (the factor is
    /// rounded DOWN, so a full R7 frame lands at 1740x1160, not below the 1620x1080 preview it replaces), averaging
    /// each block in linear light, which is where an average of light means anything. Then measures the peak the
    /// SDR tone curve needs.
    /// </summary>
    private static HdrRaster ToHdrRaster(float[] rgba, int width, int height, ColorEncoding encoding, int maxDimension)
    {
        var step = ReductionFor(width, height, maxDimension);
        var (outWidth, outHeight) = (width / step, height / step);
        var output = step == 1 ? rgba : new float[outWidth * outHeight * 4];
        if (step > 1)
        {
            var scale = 1f / (step * step);
            Parallel.For(0, outHeight, y =>
            {
                for (var x = 0; x < outWidth; x++)
                {
                    float r = 0, g = 0, b = 0, a = 0;
                    for (var dy = 0; dy < step; dy++)
                    {
                        var row = ((y * step + dy) * width + x * step) * 4;
                        for (var dx = 0; dx < step * 4; dx += 4)
                        {
                            r += rgba[row + dx];
                            g += rgba[row + dx + 1];
                            b += rgba[row + dx + 2];
                            a += rgba[row + dx + 3];
                        }
                    }
                    var o = (y * outWidth + x) * 4;
                    output[o] = r * scale;
                    output[o + 1] = g * scale;
                    output[o + 2] = b * scale;
                    output[o + 3] = a * scale;
                }
            });
        }

        // The brightest channel actually shown: what the SDR tone curve maps to white.
        var peak = 0f;
        for (var i = 0; i < output.Length; i += 4)
            peak = MathF.Max(peak, MathF.Max(output[i], MathF.Max(output[i + 1], output[i + 2])));
        return new HdrRaster(outWidth, outHeight, output, PrimariesOf(encoding), PeakNits(peak));
    }
}
