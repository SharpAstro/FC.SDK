using System.Buffers.Binary;

namespace FC.SDK.Canon;

/// <summary>File type of one image a release produces, from <c>ImageFormat</c> (0xD120).</summary>
public enum CanonImageType : uint
{
    Jpeg = 1,
    Raw = 6,

    /// <summary>HEIF (10-bit). Seen on an EOS R6 with HDR PQ on; absent from libgphoto2's list.</summary>
    Heif = 8,
}

/// <summary>Pixel size of one image. Values per libgphoto2's <c>ptp_unpack_EOS_ImageFormat</c> notes.</summary>
public enum CanonImageSize : uint
{
    Large = 0,
    Medium = 1,
    Small = 2,
    Medium1 = 5,
    Medium2 = 6,
    Small1 = 0xE,
    Small2 = 0xF,
    Small3 = 0x10,
}

/// <summary>
/// Compression of one image. Read it together with <see cref="CanonImageFormatEntry.Type"/>: on a RAW entry
/// <see cref="Fine"/> is C-RAW and <see cref="Lossless"/> plain RAW; on JPEG and HEIF entries it is the Normal/Fine
/// quality. Names per libgphoto2 (<i>coarse</i> is Canon's <i>Normal</i>).
/// </summary>
public enum CanonImageCompression : uint
{
    Normal = 2,
    Fine = 3,
    Lossless = 4,
}

/// <summary>One file the body writes per release.</summary>
public readonly record struct CanonImageFormatEntry(CanonImageType Type, CanonImageSize Size, CanonImageCompression Compression);

/// <summary>
/// Decodes the image-quality properties <c>ImageFormat</c> / <c>ImageFormatCF</c> / <c>ImageFormatSD</c>
/// (0xD120-0xD122, libgphoto2 names).
/// </summary>
/// <remarks>
/// <para>
/// The value is a structure, not a scalar: <c>[count:u32]</c> then, per image, <c>[length:u32 = 0x10][type:u32]
/// [size:u32][compression:u32]</c>. Read as a uint32 (which is what <see cref="CanonCamera.GetPropertyAsync"/> does for
/// <see cref="EdsPropertyId.ImageQuality"/>) it only says how many files a release writes: 1 for JPEG-only and for
/// RAW-only alike. Same layout and validation as libgphoto2's <c>ptp_unpack_EOS_ImageFormat</c> (count 1 or 2, every
/// entry 0x10 bytes long).
/// </para>
/// <para>
/// Verified over WPD (raw ioctl), changing the quality on the body and reading the bytes back. EOS R6: S2, S1 Normal,
/// M Fine, RAW + L Fine (RAW entry first), C-RAW, HEIF M Fine; it reports the same bytes on all three codes whatever
/// its two card slots are set to, and some readings carry trailing bytes after the last entry, which are not part of
/// it. EOS 6D: fifteen settings, among them mRAW and sRAW (a RAW entry of size Medium / Small) and JPEG S3.
/// </para>
/// </remarks>
public static class CanonImageFormat
{
    /// <summary><c>ImageFormat</c>, libgphoto2 <c>PTP_DPC_CANON_EOS_ImageFormat</c>. Mapped as <see cref="EdsPropertyId.ImageQuality"/>.</summary>
    public const ushort PtpCode = 0xD120;

    private const int EntryLength = 0x10;

    /// <summary>
    /// The images described by the property bytes, RAW first when there are two; false if the bytes do not follow the
    /// layout (truncated, a count other than 1 or 2, an entry length other than 0x10).
    /// </summary>
    public static bool TryDecode(ReadOnlySpan<byte> value, out IReadOnlyList<CanonImageFormatEntry> images)
    {
        images = [];
        if (value.Length < 4) return false;
        uint count = BinaryPrimitives.ReadUInt32LittleEndian(value);
        if (count is not (1 or 2) || value.Length < 4 + (int)count * EntryLength) return false;

        var entries = new CanonImageFormatEntry[count];
        for (int i = 0; i < count; i++)
        {
            var entry = value.Slice(4 + i * EntryLength, EntryLength);
            if (BinaryPrimitives.ReadUInt32LittleEndian(entry) != EntryLength) return false;
            entries[i] = new CanonImageFormatEntry(
                (CanonImageType)BinaryPrimitives.ReadUInt32LittleEndian(entry[4..]),
                (CanonImageSize)BinaryPrimitives.ReadUInt32LittleEndian(entry[8..]),
                (CanonImageCompression)BinaryPrimitives.ReadUInt32LittleEndian(entry[12..]));
        }
        images = entries;
        return true;
    }
}
