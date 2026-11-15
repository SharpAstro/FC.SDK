using FC.SDK.Canon;
using Shouldly;
using Xunit;

namespace FC.SDK.Tests;

/// <summary>
/// <see cref="CanonFocusState"/> — can this camera autofocus right now, and if not, why not.
/// </summary>
/// <remarks>
/// The rows below are transcribed from an EOS 6D and an EOS R6 driven through each configuration
/// with <c>FC.SDK.Diagnostics lens</c> (issue #28, 2026-11-14, and the 6D alone on 2026-08-03). They
/// matter because nothing in the protocol reports "I cannot autofocus": a bare mount and a lens
/// switched to MF both answer an AF command with <see cref="EdsError.OK"/> and move nothing.
/// </remarks>
public class FocusStateTests
{
    /// <summary>
    /// A telescope, or any bare mount, on a body that reports its lens status: status 0, no name, and
    /// the focus mode an R6 and a 6D read on 2026-11-14.
    /// </summary>
    [Fact]
    public void No_lens_means_no_autofocus()
    {
        var state = new CanonFocusState(LensName: null, FocusMode: EdsAFMode.ManualFocus, LensPresent: false);

        state.LensAttached.ShouldBeFalse();
        state.AutoFocusAvailable.ShouldBeFalse();
        state.ToString().ShouldBe("no lens, autofocus unavailable");
    }

    /// <summary>
    /// The 2026-08-03 reading of a bare 6D: <see cref="EdsAFMode.OneShot"/>, the mode a lens at AF
    /// reads. With no lens status either, only the missing name says there is nothing to focus.
    /// </summary>
    [Fact]
    public void Without_a_lens_status_a_missing_name_means_no_lens()
    {
        var state = new CanonFocusState(LensName: null, FocusMode: EdsAFMode.OneShot);

        state.LensPresent.ShouldBeNull();
        state.LensAttached.ShouldBeFalse();
        state.AutoFocusAvailable.ShouldBeFalse();
    }

    /// <summary>Without a lens status a name is a lens, as before 0xD1A8 was read.</summary>
    [Fact]
    public void Without_a_lens_status_a_name_means_a_lens()
    {
        var state = new CanonFocusState("EF50mm f/1.8 STM", EdsAFMode.OneShot);

        state.LensAttached.ShouldBeTrue();
        state.AutoFocusAvailable.ShouldBeTrue();
    }

    /// <summary>
    /// A lens the body gives no name: status 1 decides, whatever the name. An R6 focused a Sigma
    /// 35mm F1.4 DG HSM Art while calling it nothing (issue #28, section 4).
    /// </summary>
    [Fact]
    public void A_lens_with_no_name_is_still_a_lens()
    {
        var state = new CanonFocusState(LensName: null, FocusMode: EdsAFMode.OneShot, LensPresent: true);

        state.LensAttached.ShouldBeTrue();
        state.AutoFocusAvailable.ShouldBeTrue();
        state.ToString().ShouldBe("unnamed lens (OneShot), autofocus available");
    }

    /// <summary>
    /// The status wins over a name too: a name left in the cache by a lens that has come off does
    /// not make a bare mount a lens.
    /// </summary>
    [Fact]
    public void A_status_of_no_lens_wins_over_a_name()
    {
        var state = new CanonFocusState("EF50mm f/1.8 STM", EdsAFMode.OneShot, LensPresent: false);

        state.LensAttached.ShouldBeFalse();
        state.AutoFocusAvailable.ShouldBeFalse();
    }

    [Fact]
    public void A_lens_with_its_switch_at_AF_can_autofocus()
    {
        var state = new CanonFocusState("EF50mm f/1.8 STM", EdsAFMode.OneShot, LensPresent: true);

        state.LensAttached.ShouldBeTrue();
        state.AutoFocusAvailable.ShouldBeTrue();
        state.ToString().ShouldBe("EF50mm f/1.8 STM (OneShot), autofocus available");
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

    /// <summary>Code written against the two members the type had before 3.4 still reads them.</summary>
    [Fact]
    public void It_still_deconstructs_into_name_and_mode()
    {
        var (name, mode) = new CanonFocusState("EF50mm f/1.8 STM", EdsAFMode.AIServo, LensPresent: true);

        name.ShouldBe("EF50mm f/1.8 STM");
        mode.ShouldBe(EdsAFMode.AIServo);
    }
}
