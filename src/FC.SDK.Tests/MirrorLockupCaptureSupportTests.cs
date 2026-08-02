using FC.SDK.Canon;
using Shouldly;
using Xunit;

namespace FC.SDK.Tests;

/// <summary>
/// The inference behind <see cref="CanonCamera.SupportsMirrorLockupCapture"/>: where a body keeps its
/// mirror-lockup setting predicts whether a remote release survives it.
/// </summary>
/// <remarks>
/// Bodies that keep it as a Custom Function are the older ones, and those discard remote releases
/// outright while it is armed — measured on a 450D across nine sequences, all silent against controls
/// that exposed. Bodies with the real <see cref="EdsPropertyId.MirrorUpSetting"/> property expose
/// normally. NINA refuses on the same distinction, which is independent corroboration from an
/// EDSDK-based client.
/// <para>
/// The default direction is what these pin. Before this existed, a release on an armed 450D returned
/// <see cref="EdsError.OK"/> for a picture the camera had thrown away, and the caller waited forever
/// for an image event that was never coming.
/// </para>
/// </remarks>
public class MirrorLockupCaptureSupportTests
{
    /// <summary>
    /// Unknown provenance must not refuse. Until something has read or written the setting there is
    /// no evidence either way, and a camera that refuses every capture until an unrelated call
    /// happens to run first would be worse than the bug this replaces.
    /// </summary>
    [Fact]
    public void An_unprobed_camera_is_assumed_to_support_capture()
    {
        var camera = Uninitialised();

        camera.MirrorLockupIsCustomFunction.ShouldBeNull();
        camera.SupportsMirrorLockupCapture.ShouldBeTrue();
    }

    [Fact]
    public void A_body_with_the_MirrorUpSetting_property_supports_capture()
    {
        var camera = Uninitialised();
        SetSource(camera, customFunction: false);

        camera.SupportsMirrorLockupCapture.ShouldBeTrue();
    }

    [Fact]
    public void A_body_that_keeps_mirror_lockup_as_a_custom_function_does_not()
    {
        var camera = Uninitialised();
        SetSource(camera, customFunction: true);

        camera.SupportsMirrorLockupCapture.ShouldBeFalse();
    }

    /// <summary>
    /// The inference is over two measured bodies, so it has to be possible to overrule — otherwise a
    /// C.Fn body where lockup does work is locked out of its own feature with no recourse.
    /// </summary>
    [Fact]
    public void An_explicit_setting_overrules_the_inference_in_both_directions()
    {
        var camera = Uninitialised();
        SetSource(camera, customFunction: true);

        camera.SupportsMirrorLockupCapture = true;
        camera.SupportsMirrorLockupCapture.ShouldBeTrue();

        SetSource(camera, customFunction: false);
        camera.SupportsMirrorLockupCapture = false;
        camera.SupportsMirrorLockupCapture.ShouldBeFalse();
    }

    /// <summary>
    /// Never connected and never disposed — every member under test is local state, and constructing
    /// a transport would make this a hardware test.
    /// </summary>
    private static CanonCamera Uninitialised() =>
        (CanonCamera)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(CanonCamera));

    private static void SetSource(CanonCamera camera, bool customFunction) =>
        typeof(CanonCamera)
            .GetProperty(nameof(CanonCamera.MirrorLockupIsCustomFunction))!
            .SetValue(camera, customFunction);
}
