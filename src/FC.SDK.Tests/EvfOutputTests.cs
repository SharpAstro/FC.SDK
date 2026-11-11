using FC.SDK.Canon;
using Shouldly;
using Xunit;

namespace FC.SDK.Tests;

/// <summary>
/// <see cref="CanonEvfOutput"/>: what live view writes to <c>Evf_OutputDevice</c> (0xD1B0) when it
/// starts and stops.
/// </summary>
/// <remarks>
/// The readings are the ones issue #21 reports for an EOS 6D (0 at rest) and an EOS R6 (1 at rest),
/// both over WPD raw ioctl. These tests pin the arithmetic only. That a body acts on the values as
/// the issue describes is a hardware claim, and stays with the bodies.
/// </remarks>
public class EvfOutputTests
{
    private const uint None = (uint)EdsEvfOutputDevice.None;
    private const uint Tft = (uint)EdsEvfOutputDevice.TFT;
    private const uint Pc = (uint)EdsEvfOutputDevice.PC;

    /// <summary>A DSLR at rest: PC alone, mirror up and its own screen dark, as before 3.3.</summary>
    [Fact]
    public void A_6D_at_rest_starts_on_PC_alone() =>
        CanonEvfOutput.StartValue(None).ShouldBe(Pc);

    /// <summary>
    /// The R6 finding: PC alone silences the wired remote and the body's controls, so its display
    /// stays in the mask.
    /// </summary>
    [Fact]
    public void An_R6_at_rest_keeps_its_display_when_live_view_starts() =>
        CanonEvfOutput.StartValue(Tft).ShouldBe(Tft | Pc);

    /// <summary>A property that would not read gets what every body got before 3.3.</summary>
    [Fact]
    public void An_unread_output_starts_on_PC_alone() =>
        CanonEvfOutput.StartValue(null).ShouldBe(Pc);

    /// <summary>
    /// A 6D announces a change twice, <c>0x80000002</c> and then <c>2</c>. The top bit is not part
    /// of the value and must not be written back.
    /// </summary>
    [Theory]
    [InlineData(0x8000_0000u, 2u)]
    [InlineData(0x8000_0001u, 3u)]
    [InlineData(0x8000_0002u, 2u)]
    [InlineData(0x8000_0003u, 3u)]
    public void The_transition_flag_is_not_written_back(uint reading, uint expected) =>
        CanonEvfOutput.StartValue(reading).ShouldBe(expected);

    /// <summary>The 6D finding: a stop used to write TFT and leave live view on the body's screen.</summary>
    [Fact]
    public void A_6D_goes_back_to_off_when_live_view_stops()
    {
        var remembered = CanonEvfOutput.Remember(current: None, remembered: null);

        CanonEvfOutput.StopValue(remembered, current: Pc).ShouldBe(None);
    }

    [Fact]
    public void An_R6_goes_back_to_its_display_when_live_view_stops()
    {
        var remembered = CanonEvfOutput.Remember(current: Tft, remembered: null);

        CanonEvfOutput.StopValue(remembered, current: Tft | Pc).ShouldBe(Tft);
    }

    /// <summary>
    /// A second start reads the value the first one wrote. That says nothing about the body at
    /// rest, so it must not replace what the first start kept.
    /// </summary>
    [Theory]
    [InlineData(0u)]
    [InlineData(1u)]
    public void A_second_start_keeps_what_the_first_one_remembered(uint atRest)
    {
        var first = CanonEvfOutput.Remember(current: atRest, remembered: null);
        var second = CanonEvfOutput.Remember(current: CanonEvfOutput.StartValue(atRest), remembered: first);

        second.ShouldBe(atRest);
    }

    /// <summary>
    /// A body an earlier session left sending to the host has no known state at rest. TFT hands
    /// the display back, where PC alone can leave a body that ignores its buttons.
    /// </summary>
    [Theory]
    [InlineData(2u)]
    [InlineData(3u)]
    [InlineData(8u)]
    [InlineData(0x8000_0002u)]
    public void A_body_found_already_sending_to_the_host_stops_on_TFT(uint found)
    {
        var remembered = CanonEvfOutput.Remember(current: found, remembered: null);

        remembered.ShouldBeNull();
        CanonEvfOutput.StopValue(remembered, current: found).ShouldBe(Tft);
    }

    /// <summary>
    /// A stop with no start behind it, on a body that is not in live view. Writing TFT here would
    /// switch a DSLR's live view on.
    /// </summary>
    [Theory]
    [InlineData(0u)]
    [InlineData(1u)]
    public void A_stop_with_nothing_to_undo_writes_nothing(uint current) =>
        CanonEvfOutput.StopValue(remembered: null, current).ShouldBeNull();

    /// <summary>Nothing remembered and nothing readable: the pre-3.3 answer.</summary>
    [Fact]
    public void A_stop_that_knows_nothing_writes_TFT() =>
        CanonEvfOutput.StopValue(remembered: null, current: null).ShouldBe(Tft);
}
