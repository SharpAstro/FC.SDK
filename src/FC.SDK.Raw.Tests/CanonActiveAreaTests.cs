using System;
using System.Collections.Generic;
using System.IO;
using Shouldly;
using Xunit;

namespace FC.SDK.Raw.Tests;

/// <summary>
/// <see cref="CanonRawFile.ActiveArea"/>: which part of the decoded raster is a photograph.
/// </summary>
/// <remarks>
/// <para>The bug this closes was visible for as long as the decoder has existed and nobody saw it,
/// because it looks like a picture: every Canon raw came back with the sensor's shielded margin
/// still attached, an L of flat black down the left and across the top. It reached a stretched
/// display as a dark border, and it reached every statistic taken over the frame as ~3% of pixels
/// pinned at the black level.</para>
/// <para>The numbers below are read off real files rather than from ExifTool's tag table, because
/// the two disagree in a way that matters: on the 5D Mark IV the optically BLACK columns stop at
/// 143, while <c>SensorLeftBorder</c> is 156. The twelve columns between are lit but only partly
/// shielded -- excluded from the picture and useless as a black reference. Reading 144 off a
/// rendered frame and calling it the border would crop twelve columns of bad pixels back in.</para>
/// </remarks>
public class CanonActiveAreaTests(ITestOutputHelper output)
{
    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", name);

    /// <summary>Present AND materialised -- an LFS pointer is a text file that exists, which is how
    /// a <see cref="File.Exists(string)"/> guard hands a decoder 130 bytes of pointer text. Same
    /// check as <see cref="Cr3EndToEndTests"/>.</summary>
    private static bool IsUsableFixture(string path)
    {
        if (!File.Exists(path))
        {
            return false;
        }

        ReadOnlySpan<byte> pointerMagic = "version https://git-lfs"u8;
        Span<byte> head = stackalloc byte[23];
        using var stream = File.OpenRead(path);
        return stream.ReadAtLeast(head, head.Length, throwOnEndOfStream: false) == head.Length
            && !head.SequenceEqual(pointerMagic);
    }

    public static TheoryData<string, int, int, int, int, int, int> Fixtures => new()
    {
        // file,                      decoded W, H,   active L, T, W, H
        { "Canon_EOS_M50_RAW.CR3",    6288, 4056,  276,  48, 6000, 4000 },
        { "Canon_EOS_M50_CRAW.CR3",   6288, 4056,  276,  48, 6000, 4000 },
        { "Canon_EOS_R5_CRAW.CR3",    5248, 3510,  144, 108, 5088, 3392 },
        { "_MG_7578.CR2",             5568, 3708,   84,  50, 5472, 3648 },
    };

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void TheActiveAreaIsTheRectangleTheFileDeclares(
        string name, int frameWidth, int frameHeight, int left, int top, int width, int height)
    {
        var path = Fixture(name);
        if (!IsUsableFixture(path))
        {
            Assert.Skip($"fixture not present or not materialised: {path} (run `git lfs pull`)");
            return;
        }

        var bytes = File.ReadAllBytes(path);
        var file = Path.GetExtension(path).Equals(".CR2", StringComparison.OrdinalIgnoreCase)
            ? Cr2Decoder.Decode(bytes)
            : Cr3Decoder.Decode(bytes, decodeMosaic: false);

        file.Width.ShouldBe(frameWidth);
        file.Height.ShouldBe(frameHeight);

        var area = file.ActiveArea;
        output.WriteLine($"{name}: decoded {file.Width}x{file.Height} -> active ({area.Left},{area.Top}) {area.Width}x{area.Height}, {area.CfaPattern}");

        area.Left.ShouldBe(left);
        area.Top.ShouldBe(top);
        area.Width.ShouldBe(width);
        area.Height.ShouldBe(height);
        area.IsWholeFrame.ShouldBeFalse();

        // The crop must land inside the raster it indexes -- the one arithmetic slip here writes
        // out of bounds rather than showing a wrong picture.
        (area.Left + area.Width).ShouldBeLessThanOrEqualTo(file.Width);
        (area.Top + area.Height).ShouldBeLessThanOrEqualTo(file.Height);

        // Every body measured so far offsets by an even number, so the pattern is unchanged. This is
        // an observation, not a requirement: if a future fixture trips it, ShiftCfa has already
        // handled it and this line is what will say so.
        if (area.Left % 2 == 0 && area.Top % 2 == 0)
        {
            area.CfaPattern.ShouldBe(file.CfaPattern);
        }
    }

    /// <summary>
    /// An odd crop offset re-phases the CFA. No fixture exercises it (all four are even), so the
    /// only way this can be wrong-and-silent is if it is never tested: a consumer keeping the
    /// original pattern would render red and blue swapped, which reads as a plausible photograph of
    /// a differently-coloured sky.
    /// </summary>
    [Theory]
    [InlineData(CanonCfaPattern.Rggb, 0, 0, CanonCfaPattern.Rggb)]
    [InlineData(CanonCfaPattern.Rggb, 1, 0, CanonCfaPattern.Grbg)]
    [InlineData(CanonCfaPattern.Rggb, 0, 1, CanonCfaPattern.Gbrg)]
    [InlineData(CanonCfaPattern.Rggb, 1, 1, CanonCfaPattern.Bggr)]
    [InlineData(CanonCfaPattern.Bggr, 1, 1, CanonCfaPattern.Rggb)]
    [InlineData(CanonCfaPattern.Grbg, 1, 0, CanonCfaPattern.Rggb)]
    [InlineData(CanonCfaPattern.Gbrg, 0, 1, CanonCfaPattern.Rggb)]
    // Only the parity matters, so a large even offset is the identity and a large odd one is not.
    [InlineData(CanonCfaPattern.Rggb, 156, 58, CanonCfaPattern.Rggb)]
    [InlineData(CanonCfaPattern.Rggb, 157, 58, CanonCfaPattern.Grbg)]
    public void AnOddCropOffsetRephasesTheColourFilter(
        CanonCfaPattern original, int dx, int dy, CanonCfaPattern expected)
        => CanonSensorInfo.ShiftCfa(original, dx, dy).ShouldBe(expected);

    [Theory]
    [InlineData(CanonCfaPattern.Rggb)]
    [InlineData(CanonCfaPattern.Grbg)]
    [InlineData(CanonCfaPattern.Gbrg)]
    [InlineData(CanonCfaPattern.Bggr)]
    public void ShiftingByAnOffsetAndBackIsTheIdentity(CanonCfaPattern original)
    {
        for (var dx = 0; dx < 4; dx++)
        {
            for (var dy = 0; dy < 4; dy++)
            {
                var shifted = CanonSensorInfo.ShiftCfa(original, dx, dy);
                CanonSensorInfo.ShiftCfa(shifted, -dx, -dy).ShouldBe(original, $"dx={dx} dy={dy}");
            }
        }
    }

    // The fallback branches, which no real file takes -- so without these they are only ever
    // executed for the first time by whichever malformed file eventually turns up.
    public static TheoryData<string, CanonMakerNote?> Rejections => new()
    {
        { "no MakerNote at all", null },
        { "no SensorInfo subtag", MakerNote([]) },
        { "SensorInfo too short to hold the borders", MakerNote(SensorInfo(1000, 800, 10, 10, 989, 789)[..12]) },
        // Describes a raster of a different size, so its coordinates are not about these pixels.
        { "SensorInfo for a different raster", MakerNote(SensorInfo(4000, 3000, 10, 10, 989, 789)) },
        { "right border outside the frame", MakerNote(SensorInfo(1000, 800, 10, 10, 1000, 789)) },
        { "bottom border outside the frame", MakerNote(SensorInfo(1000, 800, 10, 10, 989, 800)) },
        { "inverted rect", MakerNote(SensorInfo(1000, 800, 500, 10, 100, 789)) },
        { "negative border", MakerNote(SensorInfo(1000, 800, -1, 10, 989, 789)) },
    };

    [Theory]
    [MemberData(nameof(Rejections))]
    public void AnUnusableSensorInfoLeavesTheWholeFrame(string why, CanonMakerNote? makerNote)
    {
        var file = RawFile(1000, 800, makerNote);

        var area = file.ActiveArea;

        area.IsWholeFrame.ShouldBeTrue(why);
        area.Left.ShouldBe(0, why);
        area.Top.ShouldBe(0, why);
        area.Width.ShouldBe(1000, why);
        area.Height.ShouldBe(800, why);
        area.CfaPattern.ShouldBe(file.CfaPattern, why);
    }

    [Fact]
    public void AUsableSensorInfoCropsAndIsNotTheWholeFrame()
    {
        var file = RawFile(1000, 800, MakerNote(SensorInfo(1000, 800, 10, 10, 989, 789)));

        var area = file.ActiveArea;

        area.ShouldBe(new CanonActiveArea(10, 10, 980, 780, CanonCfaPattern.Rggb));
        area.IsWholeFrame.ShouldBeFalse();
    }

    /// <summary>The bytes are the FILE's, not the machine's, so a big-endian MakerNote read as
    /// little-endian yields a raster size that does not match and the crop is declined rather than
    /// applied in the wrong place.</summary>
    [Fact]
    public void ByteOrderIsHonoured()
    {
        var le = SensorInfo(1000, 800, 10, 10, 989, 789);
        var be = new byte[le.Length];
        for (var i = 0; i < le.Length; i += 2)
        {
            be[i] = le[i + 1];
            be[i + 1] = le[i];
        }

        RawFile(1000, 800, MakerNote(be) with { IsLittleEndian = false }).ActiveArea
            .ShouldBe(new CanonActiveArea(10, 10, 980, 780, CanonCfaPattern.Rggb));

        // The same bytes read in the wrong order describe nothing that fits, so: whole frame.
        RawFile(1000, 800, MakerNote(be)).ActiveArea.IsWholeFrame.ShouldBeTrue();
    }

    private static CanonRawFile RawFile(int width, int height, CanonMakerNote? makerNote)
        => new(width, height, [], 14, CanonCfaPattern.Rggb, Exif: null, MakerNote: makerNote);

    private static CanonMakerNote MakerNote(ReadOnlySpan<byte> sensorInfo)
    {
        var subtags = new Dictionary<ushort, byte[]>();
        if (sensorInfo.Length > 0)
        {
            subtags[0x00E0] = sensorInfo.ToArray();
        }

        return new CanonMakerNote(null, null, null, null, null, subtags);
    }

    /// <summary>SensorInfo as the files carry it: little-endian int16s, value N at byte offset 2N,
    /// element 0 the array length.</summary>
    private static byte[] SensorInfo(int sensorWidth, int sensorHeight, int left, int top, int right, int bottom)
    {
        short[] values = [34, (short)sensorWidth, (short)sensorHeight, 1, 1,
                          (short)left, (short)top, (short)right, (short)bottom];
        var bytes = new byte[values.Length * 2];
        for (var i = 0; i < values.Length; i++)
        {
            bytes[i * 2] = (byte)(values[i] & 0xFF);
            bytes[i * 2 + 1] = (byte)((values[i] >> 8) & 0xFF);
        }

        return bytes;
    }
}
