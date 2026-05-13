using SharpAstro.Png;
using Shouldly;
using StbImageSharp;
using System;
using System.IO;
using Xunit;

namespace FC.SDK.Raw.Tests;

/// <summary>
/// End-to-end Canon CR3 decode tests against the committed EOS M50 fixture
/// (<c>Fixtures/Canon_EOS_M50_CRAW.CR3</c>, ~14 MB, CC0 licensed via
/// <c>raw.pixls.us</c>, LFS-tracked). Phase A scope: validate that the
/// BMFF container walk extracts the right EXIF + dimensions + thumbnail
/// without decoding the CRX-compressed sensor frame.
///
/// <list type="bullet">
/// <item><c>DecodesRealCr3_MetadataAndDimensionsMatch</c>: structural —
///   dimensions / bit depth / CFA pattern / EXIF model from the BMFF
///   walk, with <c>BayerMosaic</c> deliberately empty in Phase A.</item>
/// <item><c>ThumbnailRendersToPng</c>: extract PRVW JPEG, decode via
///   StbImageSharp, write PNG. Verifies the BMFF preview-container
///   walk + JPEG byte slice are correct.</item>
/// <item><c>RawDecode_ThrowsNotImplementedUntilPhaseB</c>: confirms
///   that the production entry point (with <c>decodeMosaic=true</c>)
///   throws cleanly after the BMFF parse succeeds — the parse runs
///   first so any structural errors surface before the Phase B message.</item>
/// </list>
///
/// Phase B will delete the throw test and replace it with a full-mosaic
/// render test mirroring <see cref="Cr2EndToEndTests"/>.
/// </summary>
public class Cr3EndToEndTests(ITestOutputHelper output)
{
    private static string FixturePath
        => System.Environment.GetEnvironmentVariable("FC_SDK_RAW_TEST_CR3")
           ?? Path.Combine(AppContext.BaseDirectory, "Fixtures", "Canon_EOS_M50_CRAW.CR3");

    [Fact]
    public void DecodesRealCr3_MetadataAndDimensionsMatch()
    {
        var path = FixturePath;
        if (!File.Exists(path))
        {
            Assert.Skip($"CR3 fixture not present at {path}. " +
                "If running locally outside CI, run `git lfs pull` or set FC_SDK_RAW_TEST_CR3.");
            return;
        }

        // Drive Cr3Decoder directly with decodeMosaic=false so we exercise the
        // Phase A path without hitting the deliberate NotImplementedException
        // that CanonRaw.FromBytes raises for the still-pending CRX decode.
        var bytes = File.ReadAllBytes(path);
        var file = Cr3Decoder.Decode(bytes, decodeMosaic: false);

        // EOS M50 native sensor frame: 6288x4056 14-bit RGGB. These are the
        // dimensions of the *largest* CMP1 box in the file — the sensor-native
        // raw, not the smaller sRAW/preview tracks that also carry CMP1.
        file.Width.ShouldBe(6288);
        file.Height.ShouldBe(4056);
        file.BitDepth.ShouldBe(14);
        file.CfaPattern.ShouldBe(CanonCfaPattern.Rggb);

        // Phase A returns an empty BayerMosaic — the CRX decoder is Phase B.
        file.BayerMosaic.ShouldNotBeNull();
        file.BayerMosaic.Length.ShouldBe(0);

        // EXIF from CMT1 box should carry the model.
        file.Exif.ShouldNotBeNull();
        file.Exif!.Model.ShouldNotBeNullOrEmpty();
        output.WriteLine($"CR3 model: {file.Exif.Model}");

        // MakerNote from CMT3 box should populate the strongly-typed Canon
        // sub-tag fields when the model is in the well-known table.
        file.MakerNote.ShouldNotBeNull();
        if (file.MakerNote!.AsShotWhiteBalance is { } wb)
        {
            output.WriteLine($"As-shot WB: R={wb.R:F3} G1={wb.G1:F3} G2={wb.G2:F3} B={wb.B:F3}");
            // G1 is the normalisation reference and must equal 1.0 by construction.
            wb.G1.ShouldBe(1.0f, 1e-6f);
        }
    }

    [Fact]
    public void ThumbnailRendersToPng()
    {
        var path = FixturePath;
        if (!File.Exists(path))
        {
            Assert.Skip($"CR3 fixture not present at {path}. " +
                "If running locally outside CI, run `git lfs pull` or set FC_SDK_RAW_TEST_CR3.");
            return;
        }

        // CanonRaw.ExtractThumbnailJpeg routes CR3 through Cr3Decoder.ExtractThumbnail
        // which walks to the larger PRVW box (preferred over THMB).
        var jpegBytes = CanonRaw.ExtractThumbnailJpeg(path);
        jpegBytes.ShouldNotBeNull("CR3 should carry a PRVW or THMB preview JPEG");
        jpegBytes!.Length.ShouldBeGreaterThan(10_000,
            $"Preview JPEG is implausibly small ({jpegBytes.Length} bytes) — likely THMB (160x120) instead of PRVW");

        // SOI marker — confirms we sliced the JPEG cleanly past the BMFF box header.
        jpegBytes[0].ShouldBe((byte)0xFF);
        jpegBytes[1].ShouldBe((byte)0xD8);

        // Decode the JPEG to confirm it's actually valid sRGB content (not just
        // a JPEG-shaped byte sequence that the box walker happened to slice out).
        var img = ImageResult.FromMemory(jpegBytes, ColorComponents.RedGreenBlueAlpha);
        img.ShouldNotBeNull();
        img.Width.ShouldBeGreaterThan(0);
        img.Height.ShouldBeGreaterThan(0);
        img.Data.Length.ShouldBe(img.Width * img.Height * 4);

        var outDir = CreateTestOutputDir(nameof(ThumbnailRendersToPng));
        var pngPath = Path.Combine(outDir, "cr3_thumbnail.png");
        File.WriteAllBytes(pngPath, PngWriter.Encode(img.Data, img.Width, img.Height));
        output.WriteLine($"Thumbnail PNG: {pngPath} ({img.Width}x{img.Height})");
    }

    [Fact]
    public void RawDecode_ThrowsNotImplementedUntilPhaseB()
    {
        var path = FixturePath;
        if (!File.Exists(path))
        {
            Assert.Skip($"CR3 fixture not present at {path}. " +
                "If running locally outside CI, run `git lfs pull` or set FC_SDK_RAW_TEST_CR3.");
            return;
        }

        // The production entry point (CanonRaw.Open / FromBytes → Cr3Decoder.Decode
        // with decodeMosaic=true) deliberately throws after the BMFF parse runs.
        // That way structural errors surface with a clear container-level
        // message before users hit the Phase B "CRX wavelet pending" one.
        var ex = Should.Throw<NotImplementedException>(() => CanonRaw.Open(path));
        ex.Message.ShouldContain("CRX");
    }

    private static string CreateTestOutputDir(string testName)
    {
        var dir = Path.Combine(Path.GetTempPath(), "FC.SDK.Raw.Tests",
            DateTime.Now.ToString("yyyyMMdd"), testName);
        Directory.CreateDirectory(dir);
        return dir;
    }
}
