using FC.SDK.Transport;
using Shouldly;
using Xunit;

namespace FC.SDK.Tests;

/// <summary>
/// The reject half of <see cref="WpdIoctlPtpTransport.TryConnectAsync"/> — the path a caller relies
/// on to hand back a diagnosis instead of an exception, having already cleaned up after itself.
/// </summary>
/// <remarks>
/// Only the reject path is testable without hardware, and it is the half worth pinning: the accept
/// path announces its own success by working, whereas a reject that throws, or that leaks the handle
/// it opened, fails somewhere else entirely — in a fallback that was supposed to be free.
/// </remarks>
public class WpdTransportSelectionTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// A device-interface path that is syntactically plausible and certain not to exist, so
    /// CreateFileW fails at the first step rather than anything further in.
    /// </summary>
    private const string MissingDevicePath =
        @"\\?\usb#vid_04a9&pid_ffff#fc-sdk-test-no-such-device#{6ac27878-a6fa-4155-ba85-f98f491d4f33}";

    [Fact]
    public async Task TryConnect_on_a_device_that_is_not_there_reports_a_reason_rather_than_throwing()
    {
        if (!OperatingSystem.IsWindows()) return;

        var (transport, failure) = await WpdIoctlPtpTransport.TryConnectAsync(MissingDevicePath, ct: Ct);

        transport.ShouldBeNull();
        failure.ShouldNotBeNullOrWhiteSpace();
    }

    /// <summary>
    /// The reason travels as far as the caller, because it is the only account of why a session ended
    /// up on COM. A report that says "WPD (COM)" and nothing else cannot be acted on.
    /// </summary>
    [Fact]
    public async Task A_rejected_probe_explains_itself()
    {
        if (!OperatingSystem.IsWindows()) return;

        var (_, failure) = await WpdIoctlPtpTransport.TryConnectAsync(MissingDevicePath, ct: Ct);

        // CreateFileW's own failure text names the path and the Win32 error; anything less specific
        // means the message was flattened somewhere and stops being a diagnosis.
        failure.ShouldNotBeNull().ShouldContain("Win32 error");
    }

    /// <summary>
    /// Repeated rejection must stay cheap. A probe that leaked its handle on the failure path would
    /// still pass the assertions above and only surface later, as a device nobody can open.
    /// </summary>
    [Fact]
    public async Task Rejection_is_repeatable()
    {
        if (!OperatingSystem.IsWindows()) return;

        for (int i = 0; i < 10; i++)
        {
            var (transport, _) = await WpdIoctlPtpTransport.TryConnectAsync(MissingDevicePath, ct: Ct);
            transport.ShouldBeNull();
        }
    }
}
