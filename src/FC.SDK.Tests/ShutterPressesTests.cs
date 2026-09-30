using FC.SDK.Canon;
using Shouldly;
using Xunit;

namespace FC.SDK.Tests;

/// <summary>
/// <see cref="ShutterPresses"/>: every press that was made is let go of, whatever answered after it. A bulb start refused with
/// the mode dial off B left its half press held, and a 6D answered DeviceBusy to every write for longer than 30 s after it.
/// </summary>
public class ShutterPressesTests
{
    /// <summary>A shutter button that records what was done to it and answers each press as told.</summary>
    private sealed class Button(EdsError half = EdsError.OK, EdsError full = EdsError.OK, EdsError bulb = EdsError.OK)
    {
        public List<string> Done { get; } = [];

        public Task<EdsError> Press(uint mode)
        {
            Done.Add(mode == ShutterPresses.Half ? "half" : "full");
            return Task.FromResult(mode == ShutterPresses.Half ? half : full);
        }

        public Task<EdsError> LetGo(uint mode)
        {
            Done.Add(mode == ShutterPresses.Half ? "let go half" : "let go full");
            return Task.FromResult(EdsError.OK);
        }

        public Task<EdsError> StartBulb()
        {
            Done.Add("bulb");
            return Task.FromResult(bulb);
        }
    }

    [Fact]
    public async Task A_release_presses_half_then_full_and_lets_go_of_both()
    {
        var button = new Button();

        (await ShutterPresses.ReleaseAsync(button.Press, button.LetGo)).ShouldBe(EdsError.OK);

        button.Done.ShouldBe(["half", "full", "let go full", "let go half"]);
    }

    [Fact]
    public async Task A_refused_full_press_still_lets_go_of_the_half_press()
    {
        var button = new Button(full: EdsError.DeviceBusy);

        (await ShutterPresses.ReleaseAsync(button.Press, button.LetGo)).ShouldBe(EdsError.DeviceBusy);

        button.Done.ShouldBe(["half", "full", "let go half"], "the half press was made, so it is let go of");
    }

    [Fact]
    public async Task A_refused_half_press_holds_nothing_to_let_go_of()
    {
        var button = new Button(half: EdsError.DeviceBusy);

        (await ShutterPresses.ReleaseAsync(button.Press, button.LetGo)).ShouldBe(EdsError.DeviceBusy);

        button.Done.ShouldBe(["half"]);
    }

    [Fact]
    public async Task A_bulb_start_the_body_refuses_lets_go_of_its_half_press()
    {
        // The mode dial not on B: a 6D answers NotSupported to 0x9125.
        var button = new Button(bulb: EdsError.NotSupported);

        (await ShutterPresses.StartBulbAsync(true, button.Press, button.LetGo, button.StartBulb)).ShouldBe(EdsError.NotSupported);

        button.Done.ShouldBe(["half", "bulb", "let go half"]);
    }

    [Fact]
    public async Task A_bulb_that_starts_keeps_its_half_press_for_the_bulb_end()
    {
        var button = new Button();

        (await ShutterPresses.StartBulbAsync(true, button.Press, button.LetGo, button.StartBulb)).ShouldBe(EdsError.OK);

        button.Done.ShouldBe(["half", "bulb"]);
    }

    [Fact]
    public async Task A_body_with_no_press_pair_starts_its_bulb_with_no_press_to_let_go_of()
    {
        // A 450D has no 0x9128/0x9129.
        var button = new Button(bulb: EdsError.NotSupported);

        (await ShutterPresses.StartBulbAsync(false, button.Press, button.LetGo, button.StartBulb)).ShouldBe(EdsError.NotSupported);

        button.Done.ShouldBe(["bulb"]);
    }
}
