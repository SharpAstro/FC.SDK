using FC.SDK.Canon;
using Shouldly;
using Xunit;

namespace FC.SDK.Tests;

/// <summary>
/// <see cref="CanonImageFormat.TryDecode"/> against the bytes an EOS R6 and an EOS 6D reported for 0xD120 over WPD (raw
/// ioctl), each captured after setting that quality on the body.
/// </summary>
/// <remarks>
/// Byte-exact on purpose: read as a uint32 the property only gives the file count, so a decoder that drifts would look
/// fine on every single-file setting. Some captures carry trailing bytes after the last entry; they are kept as captured.
/// </remarks>
public class ImageFormatTests
{
    public static TheoryData<string, CanonImageFormatEntry[]> Captured => new()
    {
        { "0100000010000000010000000F0000000300000001000000100000000100000054BD7941", // S2 (trailing bytes)
            [new(CanonImageType.Jpeg, CanonImageSize.Small2, CanonImageCompression.Fine)] },
        { "0100000010000000010000000E0000000200000000000000",                          // S1 Normal
            [new(CanonImageType.Jpeg, CanonImageSize.Small1, CanonImageCompression.Normal)] },
        { "01000000100000000100000001000000030000000000000000",                         // M Fine
            [new(CanonImageType.Jpeg, CanonImageSize.Medium, CanonImageCompression.Fine)] },
        { "020000001000000006000000000000000400000010000000010000000000000003000000",   // RAW + L Fine
            [new(CanonImageType.Raw, CanonImageSize.Large, CanonImageCompression.Lossless),
             new(CanonImageType.Jpeg, CanonImageSize.Large, CanonImageCompression.Fine)] },
        { "010000001000000006000000000000000300000068967941",                           // C-RAW (trailing bytes)
            [new(CanonImageType.Raw, CanonImageSize.Large, CanonImageCompression.Fine)] },
        { "0100000010000000080000000100000003000000000000000A000000",                   // HEIF M Fine (HDR PQ on)
            [new(CanonImageType.Heif, CanonImageSize.Medium, CanonImageCompression.Fine)] },
        // EOS 6D
        { "020000001000000006000000010000000400000010000000010000000F00000003000000",   // mRAW + S2 Fine
            [new(CanonImageType.Raw, CanonImageSize.Medium, CanonImageCompression.Lossless),
             new(CanonImageType.Jpeg, CanonImageSize.Small2, CanonImageCompression.Fine)] },
        { "020000001000000006000000020000000400000010000000010000000000000003000000",   // sRAW + L Fine
            [new(CanonImageType.Raw, CanonImageSize.Small, CanonImageCompression.Lossless),
             new(CanonImageType.Jpeg, CanonImageSize.Large, CanonImageCompression.Fine)] },
        { "020000001000000006000000000000000400000010000000010000001000000003000000",   // RAW + S3 Fine
            [new(CanonImageType.Raw, CanonImageSize.Large, CanonImageCompression.Lossless),
             new(CanonImageType.Jpeg, CanonImageSize.Small3, CanonImageCompression.Fine)] },
        { "0100000010000000060000000000000004000000",                                   // RAW alone
            [new(CanonImageType.Raw, CanonImageSize.Large, CanonImageCompression.Lossless)] },
    };

    [Theory]
    [MemberData(nameof(Captured))]
    public void Every_quality_captured_on_a_body_decodes(string hex, CanonImageFormatEntry[] expected)
    {
        CanonImageFormat.TryDecode(Convert.FromHexString(hex), out var images).ShouldBeTrue();
        images.ShouldBe(expected);
    }

    [Theory]
    [InlineData("")]
    [InlineData("01000000")]                                   // count with no entry
    [InlineData("00000000")]                                   // no image
    [InlineData("0300000010000000")]                           // libgphoto2 accepts 1 or 2 only
    [InlineData("01000000080000000100000000000000")]           // entry length other than 0x10
    [InlineData("0200000010000000060000000000000004000000")]   // second entry missing
    public void Bytes_off_the_layout_are_refused(string hex)
    {
        CanonImageFormat.TryDecode(Convert.FromHexString(hex), out var images).ShouldBeFalse();
        images.ShouldBeEmpty();
    }
}
