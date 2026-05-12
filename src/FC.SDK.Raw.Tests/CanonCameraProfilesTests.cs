using Shouldly;
using Xunit;

namespace FC.SDK.Raw.Tests;

public class CanonCameraProfilesTests
{
    [Theory]
    [InlineData("Canon EOS 6D", 0x3C82)]
    [InlineData("Canon EOS 5D Mark III", 0x3C80)]
    [InlineData("Canon EOS 7D Mark II", 0x3510)]
    [InlineData("Canon EOS-1D X", 0x3C4E)]
    public void ResolveProfile_KnownModel_ReturnsExpectedSaturation(string model, int expectedMax)
    {
        var p = CanonCameraProfiles.ResolveProfile(model);
        p.ShouldNotBeNull();
        p.Model.ShouldBe(model);
        p.MaxRaw.ShouldBe(expectedMax);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Sony A7R V")]
    [InlineData("canon eos 6d")]      // case-sensitive — EXIF strings are exact
    [InlineData("Canon EOS 6D mark fake")]
    public void ResolveProfile_Unknown_ReturnsNull(string? model)
    {
        CanonCameraProfiles.ResolveProfile(model).ShouldBeNull();
    }

    [Fact]
    public void ComputeRgbCam_NeutralCameraInput_RoundTripsToNeutralSrgb()
    {
        // The whole point of the cam_xyz_coeff row-normalisation is that a
        // neutral camera RGB (1, 1, 1) — what you get after applying as-shot
        // WB to a neutral scene — maps to neutral sRGB (1, 1, 1). Verify on
        // every camera in the table.
        foreach (var p in CanonCameraProfiles.All)
        {
            var rgbCam = p.ComputeRgbCam();

            // r' = sum(row 0); g' = sum(row 1); b' = sum(row 2)
            var r = rgbCam[0] + rgbCam[1] + rgbCam[2];
            var g = rgbCam[3] + rgbCam[4] + rgbCam[5];
            var b = rgbCam[6] + rgbCam[7] + rgbCam[8];

            r.ShouldBe(1.0f, tolerance: 1e-4f, $"{p.Model}: row 0 sum");
            g.ShouldBe(1.0f, tolerance: 1e-4f, $"{p.Model}: row 1 sum");
            b.ShouldBe(1.0f, tolerance: 1e-4f, $"{p.Model}: row 2 sum");
        }
    }

    [Fact]
    public void ComputeRgbCam_Eos6D_MatchesHandComputedValues()
    {
        // Reference values hand-computed via the same pipeline (cam_xyz * xyz_rgb,
        // row-normalise, 3x3 inverse) for EOS 6D's adobe_coeff entry. Allow ~0.01
        // tolerance to absorb hand-arithmetic rounding.
        var expected = new[]
        {
             1.913f, -1.060f,  0.147f,
            -0.225f,  1.648f, -0.422f,
             0.010f, -0.510f,  1.500f,
        };
        var actual = CanonCameraProfiles.ResolveProfile("Canon EOS 6D")!.ComputeRgbCam();
        for (var i = 0; i < 9; i++)
            actual[i].ShouldBe(expected[i], tolerance: 0.01f, $"rgb_cam[{i}]");
    }
}
