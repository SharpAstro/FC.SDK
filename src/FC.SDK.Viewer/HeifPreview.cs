using SdlVulkan.Renderer;
using SharpAstro.Codecs.Abstractions;
using SharpAstro.Heif;

namespace FC.SDK.Viewer;

/// <summary>
/// Turns a HEIF capture (a Canon <c>.HIF</c>, HDR PQ) into an <see cref="HdrRaster"/> for the capture pane:
/// the file's own largest preview item, which decodes in a fraction of the time and is all a pane with no zoom
/// needs, or the full image when the file carries no preview this decoder can read.
/// </summary>
/// <remarks>
/// <para>
/// The rasters stay float, linear light, 1.0 = 203 cd/m² (BT.2408), which is the scale both
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
    public static HdrRaster? DecodeLargestThumbnail(byte[] file, int maxDimension)
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
        if (best is null) return null;

        var rgb = HeifDecoder.Decode(file, best);
        var rgba = rgb.ToLinearRgbaFloat(out var encoding);
        return ToHdrRaster(rgba, rgb.Width, rgb.Height, encoding, maxDimension);
    }

    /// <summary>The primary image, box-filtered down for the pane, or null when the decoder refuses the file.</summary>
    /// <remarks>
    /// The full float raster of an R7 is 6960x4640x16 bytes, about 517 MB, and is dropped as soon as it has been
    /// reduced; only the reduced copy (about 32 MB) is kept for the GPU.
    /// </remarks>
    public static HdrRaster? DecodeFull(byte[] file, int maxDimension) =>
        HeifImageDecoder.TryDecodeRgbaFloat(file, out var rgba, out var width, out var height, out var encoding)
            ? ToHdrRaster(rgba, width, height, encoding, maxDimension)
            : null;

    /// <summary>
    /// Reduces by a whole factor so the long edge is at least <paramref name="maxDimension"/> (the factor is
    /// rounded DOWN, so a full R7 frame lands at 1740x1160, not below the 1620x1080 preview it replaces), averaging
    /// each block in linear light, which is where an average of light means anything. Then measures the peak the
    /// SDR tone curve needs.
    /// </summary>
    private static HdrRaster ToHdrRaster(float[] rgba, int width, int height, ColorEncoding encoding, int maxDimension)
    {
        var step = Math.Max(1, Math.Max(width, height) / maxDimension);
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

        // The brightest channel actually shown, in cd/m2: what the SDR tone curve maps to white. Floored at
        // reference white (below it the curve is the identity anyway) and capped at PQ's 10000.
        var peak = 0f;
        for (var i = 0; i < output.Length; i += 4)
            peak = MathF.Max(peak, MathF.Max(output[i], MathF.Max(output[i + 1], output[i + 2])));
        var peakNits = Math.Clamp(peak * HdrColor.ReferenceWhiteNits, HdrColor.ReferenceWhiteNits, 10000f);

        var primaries = encoding.Primaries switch
        {
            ColorPrimaries.BT2020 => HdrPrimaries.Bt2020,
            // Everything else this decoder emits is drawn as BT.709. A Display P3 HEIF would be slightly
            // desaturated; no camera file seen so far is one.
            _ => HdrPrimaries.Bt709,
        };

        return new HdrRaster(outWidth, outHeight, output, primaries, peakNits);
    }
}
