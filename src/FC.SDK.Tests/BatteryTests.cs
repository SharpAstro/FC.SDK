using FC.SDK.Canon;
using Shouldly;
using Xunit;

namespace FC.SDK.Tests;

/// <summary>
/// Battery figures out of Canon's own properties, against bytes an EOS R6 reported over WPD (raw ioctl) next to what its
/// "Battery info" menu showed at the same moment.
/// </summary>
/// <remarks>
/// EOS bodies refuse the standard 0x5001, so these are the only battery reading an EOS gives. Only the first two words
/// of <c>BatteryInfoEx</c> mean anything: the tail changes on every read, which is why the captures below are cut short.
/// </remarks>
public class BatteryTests
{
    [Theory]
    [InlineData("01000000600000000000004C502D45364E4800000001008ED0AB13001C00000000DF00DF020008808C00A90104", 96)] // LP-E6NH
    [InlineData("010000005E0000000000004C502D45364E00000000010026531F08004005400500DF00DF000000000070000000", 94)] // LP-E6N
    [InlineData("01000000080000000000004C502D45364E480000000100A95822EE00A4C5A10100000000000000000400000001", 8)]  // LP-E6NH
    public void BatteryInfoEx_carries_the_menu_percentage(string hex, int percent) =>
        CanonBattery.PercentFromInfoEx(Convert.FromHexString(hex)).ShouldBe((byte)percent);

    [Theory]
    [InlineData("")]
    [InlineData("01000000")]           // no percentage word
    [InlineData("0000000050000000")]   // no battery
    [InlineData("01000000FF000000")]   // not a percentage
    public void Unreadable_BatteryInfoEx_gives_nothing(string hex) =>
        CanonBattery.PercentFromInfoEx(Convert.FromHexString(hex)).ShouldBeNull();

    [Theory]
    [InlineData(2u, "100%")]   // four bars on an R6
    [InlineData(4u, "75%")]    // three bars
    [InlineData(1u, "50%")]    // two bars
    [InlineData(5u, "25%")]    // one bar
    [InlineData(0u, "low")]    // one bar, about to run out
    [InlineData(3u, "unknown level 3")]
    public void BatteryPower_levels_follow_libgphoto2(uint level, string label) =>
        CanonBattery.DescribePower(level).ShouldBe(label);
}
