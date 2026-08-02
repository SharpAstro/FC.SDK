using FC.SDK.Canon;
using Shouldly;
using Xunit;

namespace FC.SDK.Tests;

/// <summary>
/// Which GetEvent records count as "there is a new image", per
/// <see cref="CanonCamera.AnnouncesNewImage"/>.
/// </summary>
/// <remarks>
/// Pinned because the cost of getting it wrong is invisible and delayed. A body announces a
/// card-destination frame and a host-destination frame with different event codes; missing either
/// family does not raise an error anywhere — the exposure happens, no callback fires, and the
/// picture is simply never collected. On a 450D that also wedges the camera, because a
/// host-destination frame occupies it until <c>TransferComplete</c> arrives, and it then answers
/// <c>DeviceBusy</c> to every subsequent release. That looked exactly like a camera refusing to
/// shoot, and was diagnosed as one.
/// </remarks>
public class ObjectEventClassificationTests
{
    public static TheoryData<CanonEventType> ImageAnnouncements =>
    [
        CanonEventType.ObjectAddedEx,        // card destination
        CanonEventType.ObjectAddedEx64,
        CanonEventType.RequestObjectTransfer,  // host destination — frame held in the body's RAM
        CanonEventType.RequestObjectTransfer64,
    ];

    [Theory]
    [MemberData(nameof(ImageAnnouncements))]
    public void Every_family_that_carries_an_object_handle_announces_an_image(CanonEventType type) =>
        CanonCamera.AnnouncesNewImage(type).ShouldBeTrue();

    public static TheoryData<CanonEventType> NotImageAnnouncements =>
    [
        CanonEventType.PropertyChanged,
        CanonEventType.AllowedValuesChanged,
        CanonEventType.CameraStatusChanged,
        CanonEventType.BulbExposureTime,
        CanonEventType.WillSoonShutdown,
    ];

    /// <summary>
    /// The negative half matters as much: a state record routed to <c>ObjectAdded</c> would send a
    /// caller off to download an object handle that is really a status word.
    /// </summary>
    [Theory]
    [MemberData(nameof(NotImageAnnouncements))]
    public void State_and_property_records_are_not_image_announcements(CanonEventType type) =>
        CanonCamera.AnnouncesNewImage(type).ShouldBeFalse();
}
