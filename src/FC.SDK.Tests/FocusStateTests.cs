using FC.SDK.Canon;
using Shouldly;
using Xunit;

namespace FC.SDK.Tests;

/// <summary>
/// <see cref="CanonFocusState"/> — can this camera autofocus right now, and if not, why not.
/// </summary>
/// <remarks>
/// The three rows below are transcribed from an EOS 6D driven through all three configurations with
/// <c>FC.SDK.Diagnostics lens</c>. They matter because nothing in the protocol reports "I cannot
/// autofocus": a bare mount and a lens switched to MF both answer an AF command with
/// <see cref="EdsError.OK"/> and move nothing.
/// </remarks>
public class FocusStateTests
{
    /// <summary>A telescope, or any bare mount. No name, so nothing to focus.</summary>
    [Fact]
    public void No_lens_means_no_autofocus()
    {
        var state = new CanonFocusState(LensName: null, FocusMode: EdsAFMode.OneShot);

        state.LensAttached.ShouldBeFalse();
        state.AutoFocusAvailable.ShouldBeFalse();
        state.ToString().ShouldBe("no lens, autofocus unavailable");
    }

    [Fact]
    public void A_lens_with_its_switch_at_AF_can_autofocus()
    {
        var state = new CanonFocusState("EF50mm f/1.8 STM", EdsAFMode.OneShot);

        state.LensAttached.ShouldBeTrue();
        state.AutoFocusAvailable.ShouldBeTrue();
    }

    /// <summary>
    /// The reading that took a hardware A/B to establish: 0xD108 follows the lens's own AF/MF
    /// switch. Notably <see cref="EdsAFMode.ManualFocus"/> is absent from the property's
    /// allowed-value list — allowed values are what a client may write, not what the property can
    /// report — so the list cannot be used to rule this state out.
    /// </summary>
    [Fact]
    public void A_lens_switched_to_MF_reports_manual_and_cannot_autofocus()
    {
        var state = new CanonFocusState("EF50mm f/1.8 STM", EdsAFMode.ManualFocus);

        state.LensAttached.ShouldBeTrue();
        state.AutoFocusAvailable.ShouldBeFalse();
    }

    /// <summary>
    /// Lens presence and focus mode are independent questions, and the summary has to keep them
    /// apart — "no lens" and "switch at MF" want different advice from a caller.
    /// </summary>
    [Theory]
    [InlineData(EdsAFMode.OneShot, true)]
    [InlineData(EdsAFMode.AIServo, true)]
    [InlineData(EdsAFMode.AIFocus, true)]
    [InlineData(EdsAFMode.ManualFocus, false)]
    public void Every_focus_mode_but_manual_permits_autofocus_when_a_lens_is_present(
        EdsAFMode mode, bool expected) =>
        new CanonFocusState("EF50mm f/1.8 STM", mode).AutoFocusAvailable.ShouldBe(expected);
}
