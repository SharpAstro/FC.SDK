using System;

namespace FC.SDK.Raw;

/// <summary>
/// Canon MakerNote <c>SensorInfo</c> (subtag <c>0x00E0</c>): the rectangle of the sensor raster that
/// actually carries a picture, and the machinery to apply it without breaking the colour filter.
/// </summary>
/// <remarks>
/// <para><b>The decoded frame is bigger than the photograph, on every Canon body.</b> A raw file
/// records the whole sensor raster, whose left and top edges carry photosites under an opaque
/// shield (used by the camera for its own black-level calibration) followed by a narrow partially
/// shielded transition, and whose right and bottom carry a few more spare columns and rows. None of
/// it is scene. Measured on four files:</para>
/// <code>
///   body            decoded      active rect                right/bottom spare
///   5D Mark IV      6888x4546    (156, 58)  6720x4480        12, 8
///   EOS M50         6288x4056    (276, 48)  6000x4000        12, 8
///   EOS R5          5248x3510    (144,108)  5088x3392        16, 10
///   CR2 (6D)       5568x3708    ( 84, 50)  5472x3648        12, 10
/// </code>
/// <para><b>The masked region and the discarded margin are not the same rectangle</b>, which is the
/// easy mistake. On the 5D Mark IV the optically black columns stop at 143 while the active area
/// starts at 156: columns 144 to 155 are lit but only partly shielded, so they are excluded from the
/// picture AND unusable as a black reference. Anyone mining the margin for a per-frame bias must
/// measure where the flat part ends rather than assume it runs to <c>Left</c>. Canon's own
/// <c>BlackMaskLeftBorder</c> fields would say, but they read 0 on all four files above, so they
/// cannot be relied on.</para>
/// <para><b>The mosaic itself is never cropped by this library.</b> The CR3 decoder is byte-exact
/// against LibRaw's <c>unprocessed_raw</c>, which is also uncropped, and that comparison is the only
/// reason to believe the decoder at all -- cropping inside it would silently retire the oracle. So
/// the crop is offered as metadata on <see cref="CanonRawFile.ActiveArea"/> and applied by whoever
/// consumes the pixels.</para>
/// </remarks>
internal static class CanonSensorInfo
{
    /// <summary>MakerNote subtag holding the SensorInfo SHORT array.</summary>
    private const ushort SensorInfoTag = 0x00E0;

    // ExifTool Canon.pm indexes SensorInfo as an array of int16 whose element 0 is the array length,
    // so value N sits at byte offset 2N. We need up to index 8, hence 18 bytes.
    private const int IndexSensorWidth = 1;
    private const int IndexSensorHeight = 2;
    private const int IndexLeftBorder = 5;
    private const int IndexTopBorder = 6;
    private const int IndexRightBorder = 7;
    private const int IndexBottomBorder = 8;
    private const int MinimumLength = (IndexBottomBorder + 1) * 2;

    /// <summary>
    /// The active-area rectangle for a decoded frame, or the whole frame when the file does not
    /// describe one that makes sense for it.
    /// </summary>
    /// <remarks>
    /// Deliberately computed from <see cref="CanonMakerNote.RawSubtags"/> rather than plumbed
    /// through each decoder: CR2 and CR3 parse their MakerNotes separately and have drifted before,
    /// and a crop that one format applies and the other does not is a colour-and-geometry bug that
    /// only shows on half the files. Reading the subtag here means neither decoder can forget.
    /// </remarks>
    internal static CanonActiveArea Resolve(int width, int height, CanonCfaPattern cfa, CanonMakerNote? makerNote)
    {
        var whole = new CanonActiveArea(0, 0, width, height, cfa);

        if (makerNote is null
            || !makerNote.RawSubtags.TryGetValue(SensorInfoTag, out var raw)
            || raw.Length < MinimumLength)
        {
            return whole;
        }

        // The borders index the SENSOR raster. If SensorInfo is describing a raster of a different
        // size from the one we decoded -- a crop mode, an sRAW, a body we have mis-parsed -- then
        // its coordinates do not belong to these pixels and applying them would cut the picture in
        // the wrong place. Fall back rather than guess.
        var le = makerNote.IsLittleEndian;
        if (ReadInt16(raw, IndexSensorWidth, le) != width || ReadInt16(raw, IndexSensorHeight, le) != height)
        {
            return whole;
        }

        int left = ReadInt16(raw, IndexLeftBorder, le);
        int top = ReadInt16(raw, IndexTopBorder, le);
        int right = ReadInt16(raw, IndexRightBorder, le);
        int bottom = ReadInt16(raw, IndexBottomBorder, le);

        // Borders are inclusive, so a 1x1 active area is legal arithmetic and obviously not a photo;
        // require the rect to be inside the frame and to have real extent.
        if (left < 0 || top < 0 || right <= left || bottom <= top || right >= width || bottom >= height)
        {
            return whole;
        }

        return new CanonActiveArea(left, top, right - left + 1, bottom - top + 1, ShiftCfa(cfa, left, top));
    }

    /// <summary>
    /// The Bayer pattern seen from an origin moved by (<paramref name="dx"/>, <paramref name="dy"/>).
    /// </summary>
    /// <remarks>
    /// A crop by an ODD offset re-phases the colour filter array: the pixel at the new (0, 0) is a
    /// different colour from the one at the old (0, 0), so a consumer that keeps the original
    /// pattern renders the frame with red and blue exchanged, or with the greens on the wrong
    /// diagonal. All four bodies measured have even borders and would never exercise this, which is
    /// exactly why it is worth computing rather than asserting -- a body that does not, decoded
    /// years from now, would otherwise produce a plausible image in the wrong colours.
    /// </remarks>
    internal static CanonCfaPattern ShiftCfa(CanonCfaPattern cfa, int dx, int dy)
    {
        // Identify a pattern by where RED sits in its 2x2 cell; moving the origin moves red the
        // other way.
        var (rx, ry) = cfa switch
        {
            CanonCfaPattern.Rggb => (0, 0),
            CanonCfaPattern.Grbg => (1, 0),
            CanonCfaPattern.Gbrg => (0, 1),
            CanonCfaPattern.Bggr => (1, 1),
            _ => (0, 0),
        };

        rx = ((rx - dx) % 2 + 2) % 2;
        ry = ((ry - dy) % 2 + 2) % 2;

        return (rx, ry) switch
        {
            (0, 0) => CanonCfaPattern.Rggb,
            (1, 0) => CanonCfaPattern.Grbg,
            (0, 1) => CanonCfaPattern.Gbrg,
            _ => CanonCfaPattern.Bggr,
        };
    }

    /// <summary>Reads back in the byte order the MakerNote was parsed in
    /// (<see cref="CanonMakerNote.IsLittleEndian"/>): these are file bytes, not machine bytes.</summary>
    private static short ReadInt16(byte[] raw, int index, bool littleEndian)
    {
        var lo = raw[index * 2];
        var hi = raw[index * 2 + 1];
        return littleEndian ? (short)(lo | (hi << 8)) : (short)(hi | (lo << 8));
    }
}
