using FC.SDK.Raw.Crx;
using SharpAstro.Jpeg;
using SharpAstro.Png;
using Shouldly;
using System;
using System.Collections.Generic;
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
///   SharpAstro.Jpeg, write PNG. Verifies the BMFF preview-container
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
    /// <summary>
    /// Whether the fixture is actually usable: present AND materialised.
    /// 
    /// <para>The second half is the one that bites. A checkout with <c>lfs: false</c>, or a failed
    /// <c>git lfs pull</c>, leaves a ~130-byte pointer TEXT file in place of the CR3. That file
    /// exists, so a <see cref="File.Exists(string)"/> guard passes and the decoder is then handed
    /// pointer text: seven tests failed with assertion noise where they should have skipped. An
    /// unmaterialised pointer never fails on its own, which is exactly what makes it worth
    /// detecting explicitly.</para>
    /// </summary>
    private static bool IsUsableFixture(string path)
    {
        if (!File.Exists(path))
        {
            return false;
        }

        // Every LFS pointer begins with this exact string; a CR3 begins with an ISO-BMFF ftyp box,
        // so the two cannot be confused.
        ReadOnlySpan<byte> pointerMagic = "version https://git-lfs"u8;
        Span<byte> head = stackalloc byte[23];
        using var stream = File.OpenRead(path);
        return stream.ReadAtLeast(head, head.Length, throwOnEndOfStream: false) == head.Length
            && !head.SequenceEqual(pointerMagic);
    }

    private static string FixturePath
        => System.Environment.GetEnvironmentVariable("FC_SDK_RAW_TEST_CR3")
           ?? Path.Combine(AppContext.BaseDirectory, "Fixtures", "Canon_EOS_M50_CRAW.CR3");

    [Fact]
    public void DecodesRealCr3_MetadataAndDimensionsMatch()
    {
        var path = FixturePath;
        if (!IsUsableFixture(path))
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
        if (!IsUsableFixture(path))
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
        var img = JpegDecoder.Decode(jpegBytes);
        img.ShouldNotBeNull();
        img.Width.ShouldBeGreaterThan(0);
        img.Height.ShouldBeGreaterThan(0);
        img.Pixels.Length.ShouldBe(img.Width * img.Height * 4);

        var outDir = CreateTestOutputDir(nameof(ThumbnailRendersToPng));
        var pngPath = Path.Combine(outDir, "cr3_thumbnail.png");
        File.WriteAllBytes(pngPath, PngWriter.Encode(img.Pixels, img.Width, img.Height));
        output.WriteLine($"Thumbnail PNG: {pngPath} ({img.Width}x{img.Height})");
    }

    [Fact]
    public void Cr3_DecodesM50CrawToMosaic_ProducesPlausibleSignal()
    {
        // Phase B.5 end-to-end gate. Uses the CRAW.CR3 fixture
        // (encType=0 levels=3 — full CDF 5/3 wavelet pyramid, the path
        // that exercises CrxWaveletPlaneDecoder + the H-band line decoder).
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Canon_EOS_M50_CRAW.CR3");
        if (!IsUsableFixture(path))
        {
            Assert.Skip($"CR3 CRAW fixture not present at {path}. Run `git lfs pull` to fetch.");
            return;
        }

        var bytes = File.ReadAllBytes(path);
        var file = Cr3Decoder.Decode(bytes, decodeMosaic: true);

        file.Width.ShouldBe(6288);
        file.Height.ShouldBe(4056);
        file.BitDepth.ShouldBe(14);
        file.CfaPattern.ShouldBe(CanonCfaPattern.Rggb);

        file.BayerMosaic.ShouldNotBeNull();
        file.BayerMosaic.Length.ShouldBe(file.Width * file.Height);

        var max14 = (1 << 14) - 1;
        ushort min = ushort.MaxValue, max = 0;
        var distinct = new HashSet<ushort>();
        foreach (var v in file.BayerMosaic)
        {
            if (v < min) min = v;
            if (v > max) max = v;
            if (distinct.Count < 4096) distinct.Add(v);
        }
        output.WriteLine($"M50 CRAW mosaic: min={min}, max={max}, distinct(cap 4096)={distinct.Count}");
        (max - min).ShouldBeGreaterThan(max14 / 10,
            $"M50 CRAW mosaic dynamic range only {max - min} < {max14 / 10} — wavelet decode looks broken.");
        distinct.Count.ShouldBeGreaterThan(256,
            $"M50 CRAW mosaic only has {distinct.Count} distinct values — wavelet decode is producing degenerate output.");
        max.ShouldBeLessThanOrEqualTo((ushort)max14, "mosaic value exceeds 14-bit range — clamp regression.");

        var outDir = CreateTestOutputDir(nameof(Cr3_DecodesM50CrawToMosaic_ProducesPlausibleSignal));
        var img = CanonDemosaic.Render(file, new CanonRenderOptions { Algorithm = CanonDemosaicAlgorithm.Bilinear });
        var rgba = new byte[file.Width * file.Height * 4];
        for (var p = 0; p < file.Width * file.Height; p++)
        {
            rgba[p * 4]     = (byte)(img.InterleavedRgb[p * 3]     >> 8);
            rgba[p * 4 + 1] = (byte)(img.InterleavedRgb[p * 3 + 1] >> 8);
            rgba[p * 4 + 2] = (byte)(img.InterleavedRgb[p * 3 + 2] >> 8);
            rgba[p * 4 + 3] = 0xFF;
        }
        var pngPath = Path.Combine(outDir, "cr3_m50_craw_bilinear.png");
        File.WriteAllBytes(pngPath, PngWriter.Encode(rgba, file.Width, file.Height));
        output.WriteLine($"M50 CRAW bilinear render: {pngPath} ({file.Width}x{file.Height})");

        // Dump the raw mosaic as ushort[] .bin so the LibRaw oracle compare
        // (unprocessed_raw + tifffile) can do byte-exact validation.
        var binPath = Path.Combine(outDir, "cr3_m50_craw_mosaic.bin");
        var rawBytes = new byte[file.BayerMosaic.Length * 2];
        Buffer.BlockCopy(file.BayerMosaic, 0, rawBytes, 0, rawBytes.Length);
        File.WriteAllBytes(binPath, rawBytes);
        output.WriteLine($"M50 CRAW raw-mosaic bin: {binPath} ({rawBytes.Length} bytes)");
    }

    [Theory]
    [InlineData("Canon_EOS_M50_CRAW.CR3", 6288, 4056, 3, 3144, 4056)]
    [InlineData("Canon_EOS_M50_RAW.CR3",  6288, 4056, 0, 3144, 4056)]
    public void HeaderParse_ResolvesPerTrackGeometry(
        string fixtureName, int expectedWidth, int expectedHeight,
        int expectedLevels, int expectedTileWidth, int expectedTileHeight)
    {
        // Phase B.3: the BMFF descent picks the largest CMP1 (= sensor-native
        // raw track) and produces a CrxImageHeader with full geometry +
        // codec params + per-track mdat byte range. Validate against both
        // M50 fixtures — they differ in wavelet levels (0 vs 3) so we
        // cover both decode-path classes.
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", fixtureName);
        if (!IsUsableFixture(path))
        {
            Assert.Skip($"CR3 fixture not present at {path}. Run `git lfs pull` to fetch.");
            return;
        }
        var bytes = File.ReadAllBytes(path);
        var header = Cr3Decoder.TryResolveHeader(bytes);
        header.ShouldNotBeNull();
        header!.Width.ShouldBe(expectedWidth);
        header.Height.ShouldBe(expectedHeight);
        header.BitDepth.ShouldBe(14);
        header.PlaneCount.ShouldBe(4);
        header.Cfa.ShouldBe(CanonCfaPattern.Rggb);
        // encType=0 (lossless HQ) for both M50 fixtures. cRAW lossy
        // (encType=3) would need a fixture from a body that emits that mode.
        header.EncType.ShouldBe(0);
        header.Levels.ShouldBe(expectedLevels);
        header.TileWidth.ShouldBe(expectedTileWidth);
        header.TileHeight.ShouldBe(expectedTileHeight);
        // mdat range: offset must be inside the file, size must fit.
        header.MdatOffset.ShouldBeGreaterThan(0);
        header.MdatSize.ShouldBeGreaterThan(0);
        (header.MdatOffset + header.MdatSize).ShouldBeLessThanOrEqualTo(bytes.LongLength);
        // MdatHdrSize is the structural-marker zone at the start of the
        // track's mdat payload; it must be > 0 (else the parser produces no
        // subbands) and strictly less than the total mdat size.
        header.MdatHdrSize.ShouldBeGreaterThan(0);
        header.MdatHdrSize.ShouldBeLessThan(header.MdatSize);
    }

    [Fact]
    public void Cr3_DecodesR5CrawToMosaic_ProducesPlausibleSignal()
    {
        // Phase B.6 end-to-end gate. The R5 cRAW fixture is lossy cRAW —
        // encType=0 levels=3 (same wavelet pyramid we ship for M50 CRAW)
        // BUT with every band using FF13 markers carrying per-position
        // quantization tables. The decoder reads the per-tile Golomb QP
        // stream, folds it through q_step_tbl[6] into per-level qStep
        // grids, and scales each decoded coefficient by qStepBase +
        // ((qStepTbl[row,col] * qStepMult) >> 3) before the inverse 5/3
        // lift. R5/R6 ship every consumer cRAW shot through this path.
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Canon_EOS_R5_CRAW.CR3");
        if (!IsUsableFixture(path))
        {
            Assert.Skip($"EOS R5 cRAW fixture not present at {path}.");
            return;
        }
        var bytes = File.ReadAllBytes(path);
        var header = Cr3Decoder.TryResolveHeader(bytes);
        header.ShouldNotBeNull();
        output.WriteLine(
            $"EOS R5 cRAW header: {header!.Width}x{header.Height} bit={header.BitDepth} " +
            $"cfa={header.Cfa} planes={header.PlaneCount} encType={header.EncType} " +
            $"levels={header.Levels} tile={header.TileWidth}x{header.TileHeight} " +
            $"mdat=[0x{header.MdatOffset:X}+{header.MdatSize}] hdr={header.MdatHdrSize}");
        header.Width.ShouldBe(5248);
        header.Height.ShouldBe(3510);
        header.BitDepth.ShouldBe(14);
        header.Cfa.ShouldBe(CanonCfaPattern.Rggb);
        header.PlaneCount.ShouldBe(4);
        header.EncType.ShouldBe(0);
        header.Levels.ShouldBe(3);

        var file = Cr3Decoder.Decode(bytes, decodeMosaic: true);
        file.Width.ShouldBe(5248);
        file.Height.ShouldBe(3510);
        file.BitDepth.ShouldBe(14);
        file.CfaPattern.ShouldBe(CanonCfaPattern.Rggb);
        file.BayerMosaic.ShouldNotBeNull();
        file.BayerMosaic.Length.ShouldBe(file.Width * file.Height);

        var max14 = (1 << 14) - 1;
        ushort min = ushort.MaxValue, max = 0;
        var distinct = new HashSet<ushort>();
        foreach (var v in file.BayerMosaic)
        {
            if (v < min) min = v;
            if (v > max) max = v;
            if (distinct.Count < 4096) distinct.Add(v);
        }
        output.WriteLine($"R5 cRAW mosaic: min={min}, max={max}, distinct(cap 4096)={distinct.Count}");
        (max - min).ShouldBeGreaterThan(max14 / 10,
            $"R5 cRAW mosaic dynamic range only {max - min} < {max14 / 10} — wavelet+iquant decode looks broken.");
        distinct.Count.ShouldBeGreaterThan(256,
            $"R5 cRAW mosaic only has {distinct.Count} distinct values — wavelet+iquant decode is producing degenerate output.");
        max.ShouldBeLessThanOrEqualTo((ushort)max14, "mosaic value exceeds 14-bit range — clamp regression.");

        var outDir = CreateTestOutputDir(nameof(Cr3_DecodesR5CrawToMosaic_ProducesPlausibleSignal));
        var img = CanonDemosaic.Render(file, new CanonRenderOptions { Algorithm = CanonDemosaicAlgorithm.Bilinear });
        var rgba = new byte[file.Width * file.Height * 4];
        for (var p = 0; p < file.Width * file.Height; p++)
        {
            rgba[p * 4]     = (byte)(img.InterleavedRgb[p * 3]     >> 8);
            rgba[p * 4 + 1] = (byte)(img.InterleavedRgb[p * 3 + 1] >> 8);
            rgba[p * 4 + 2] = (byte)(img.InterleavedRgb[p * 3 + 2] >> 8);
            rgba[p * 4 + 3] = 0xFF;
        }
        var pngPath = Path.Combine(outDir, "cr3_r5_craw_bilinear.png");
        File.WriteAllBytes(pngPath, PngWriter.Encode(rgba, file.Width, file.Height));
        output.WriteLine($"R5 cRAW bilinear render: {pngPath} ({file.Width}x{file.Height})");

        // Dump raw mosaic as ushort[] .bin so the LibRaw oracle compare
        // (unprocessed_raw + tifffile) can do byte-exact validation.
        var binPath = Path.Combine(outDir, "cr3_r5_craw_mosaic.bin");
        var rawBytes = new byte[file.BayerMosaic.Length * 2];
        Buffer.BlockCopy(file.BayerMosaic, 0, rawBytes, 0, rawBytes.Length);
        File.WriteAllBytes(binPath, rawBytes);
        output.WriteLine($"R5 cRAW raw-mosaic bin: {binPath} ({rawBytes.Length} bytes)");
    }

    [Fact]
    public void Cr3_DecodesM50RawToMosaic_ProducesPlausibleSignal()
    {
        // Phase B.4 end-to-end gate. Uses the simpler RAW.CR3 fixture
        // (encType=0 levels=0 — no wavelet, single LL band per plane) since
        // levels>0 wavelet recombination is the B.5 task. CRAW.CR3 still
        // throws at decode time with a "B.5 pending" message.
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Canon_EOS_M50_RAW.CR3");
        if (!IsUsableFixture(path))
        {
            Assert.Skip($"CR3 RAW fixture not present at {path}. Run `git lfs pull` to fetch.");
            return;
        }

        var bytes = File.ReadAllBytes(path);
        var file = Cr3Decoder.Decode(bytes, decodeMosaic: true);

        // Container-level invariants — must match what HeaderParse asserts.
        file.Width.ShouldBe(6288);
        file.Height.ShouldBe(4056);
        file.BitDepth.ShouldBe(14);
        file.CfaPattern.ShouldBe(CanonCfaPattern.Rggb);

        // Mosaic shape: one ushort per sensor pixel.
        file.BayerMosaic.ShouldNotBeNull();
        file.BayerMosaic.Length.ShouldBe(file.Width * file.Height);

        // Plausibility gates: a 14-bit sensor frame from a real scene must
        // (a) span a substantial fraction of the available dynamic range
        // and (b) carry many distinct values — a stuck/zeroed decode would
        // collapse to a few values and a tiny range. Same coherent-signal
        // checks Cr2EndToEndTests applies.
        var max14 = (1 << 14) - 1;
        ushort min = ushort.MaxValue, max = 0;
        var distinct = new HashSet<ushort>();
        foreach (var v in file.BayerMosaic)
        {
            if (v < min) min = v;
            if (v > max) max = v;
            // Bound the set size so we don't blow memory on a 25M-pixel scan.
            if (distinct.Count < 4096) distinct.Add(v);
        }
        output.WriteLine($"M50 RAW mosaic: min={min}, max={max}, distinct(cap 4096)={distinct.Count}");
        (max - min).ShouldBeGreaterThan(max14 / 10,
            $"M50 RAW mosaic dynamic range only {max - min} < {max14 / 10} — decoder looks broken or stream truncated.");
        distinct.Count.ShouldBeGreaterThan(256,
            $"M50 RAW mosaic only has {distinct.Count} distinct values — decoder is producing degenerate output.");
        max.ShouldBeLessThanOrEqualTo((ushort)max14, "mosaic value exceeds 14-bit range — clamp regression.");

        // Visual gate: run the decoded mosaic through the production demosaic
        // pipeline (black subtract + WB + bilinear interpolation + colour
        // matrix + gamma) and dump a PNG. A correct decode produces a
        // recognisable scene; a coordinate-system bug or per-plane swap
        // would visibly garble the image even when the per-channel stats pass.
        var outDir = CreateTestOutputDir(nameof(Cr3_DecodesM50RawToMosaic_ProducesPlausibleSignal));
        var img = CanonDemosaic.Render(file, new CanonRenderOptions { Algorithm = CanonDemosaicAlgorithm.Bilinear });
        var rgba = new byte[file.Width * file.Height * 4];
        for (var p = 0; p < file.Width * file.Height; p++)
        {
            rgba[p * 4]     = (byte)(img.InterleavedRgb[p * 3]     >> 8);
            rgba[p * 4 + 1] = (byte)(img.InterleavedRgb[p * 3 + 1] >> 8);
            rgba[p * 4 + 2] = (byte)(img.InterleavedRgb[p * 3 + 2] >> 8);
            rgba[p * 4 + 3] = 0xFF;
        }
        var pngPath = Path.Combine(outDir, "cr3_m50_raw_bilinear.png");
        File.WriteAllBytes(pngPath, PngWriter.Encode(rgba, file.Width, file.Height));
        output.WriteLine($"M50 RAW bilinear render: {pngPath} ({file.Width}x{file.Height})");

        // Dump the raw mosaic as a flat ushort[] binary so the LibRaw oracle
        // (simple_dcraw / unprocessed_raw + tifffile) can do byte-exact
        // comparison. The dump path is stable across test runs so the
        // companion Python diff script can pick it up.
        var binPath = Path.Combine(outDir, "cr3_m50_raw_mosaic.bin");
        var bytes2 = new byte[file.BayerMosaic.Length * 2];
        Buffer.BlockCopy(file.BayerMosaic, 0, bytes2, 0, bytes2.Length);
        File.WriteAllBytes(binPath, bytes2);
        output.WriteLine($"M50 RAW raw-mosaic bin: {binPath} ({bytes2.Length} bytes)");
    }

    private static string CreateTestOutputDir(string testName)
    {
        var dir = Path.Combine(Path.GetTempPath(), "FC.SDK.Raw.Tests",
            DateTime.Now.ToString("yyyyMMdd"), testName);
        Directory.CreateDirectory(dir);
        return dir;
    }
}
