using System.Buffers.Binary;

namespace FC.SDK.Canon;

/// <summary>
/// The two Canon battery properties: <c>BatteryPower</c> (0xD111), a coarse level, and <c>BatteryInfoEx</c> (0xD21C),
/// which carries the remaining capacity in percent (libgphoto2 names; libgphoto2 decodes neither structure field).
/// </summary>
/// <remarks>
/// The standard PTP battery property 0x5001 is no substitute: an EOS R6 refuses it, and an EOS 6D answers it with a
/// coarse level (100 while its "Battery info" menu showed 88 %, <c>BatteryPower</c> 2). The 6D has no 0xD21C (nor
/// <c>BatteryInfo</c> 0xD1A6), so its menu percentage is not readable as a property at all. Measured on an EOS R6 over
/// WPD (raw ioctl), against the body's own "Battery info" menu:
/// <list type="table">
/// <item><term>LP-E6NH, 96 %</term><description>0xD21C percent 96, 0xD111 = 2</description></item>
/// <item><term>LP-E6N, 94 %</term><description>0xD21C percent 94, 0xD111 = 2</description></item>
/// <item><term>LP-E6NH, 8 %</term><description>0xD21C percent 8, 0xD111 = 0</description></item>
/// </list>
/// and 0xD111 stepping 2 → 4 → 1 → 5 → 0 as the screen went from four bars to one and then to low, with no value in
/// between. Those are exactly libgphoto2's labels for <c>BatteryPower</c> (<c>_get_Canon_EOS_BatteryLevel</c>):
/// 0 Low, 1 50%, 2 100%, 4 75%, 5 25%. The 6D reads 2 at four bars too; the 450D's scale has not been checked.
/// </remarks>
internal static class CanonBattery
{
    /// <summary><c>BatteryPower</c>, libgphoto2 <c>PTP_DPC_CANON_EOS_BatteryPower</c>; mapped as <see cref="EdsPropertyId.BatteryLevel"/>.</summary>
    internal const ushort PowerCode = 0xD111;

    /// <summary><c>BatteryInfoEx</c>, libgphoto2 <c>PTP_DPC_CANON_EOS_BatteryInfoEx</c> (not <c>BatteryInfo</c>, which is 0xD1A6).</summary>
    internal const ushort InfoExCode = 0xD21C;

    /// <summary>The level <see cref="PowerCode"/> reports for a battery about to run out.</summary>
    internal const uint PowerLow = 0;

    /// <summary>
    /// Remaining capacity from <c>BatteryInfoEx</c>: <c>[battery count:u32][percent:u32]</c>, then the battery model in
    /// ASCII ("LP-E6NH") and a per-battery id. The bytes after that change on every read and are not decoded. Null when
    /// the value is too short, reports no battery, or a percentage above 100.
    /// </summary>
    internal static byte? PercentFromInfoEx(ReadOnlySpan<byte> value)
    {
        if (value.Length < 8) return null;
        uint count = BinaryPrimitives.ReadUInt32LittleEndian(value);
        uint percent = BinaryPrimitives.ReadUInt32LittleEndian(value[4..]);
        return count >= 1 && percent <= 100 ? (byte)percent : null;
    }

    /// <summary>libgphoto2's label for a <c>BatteryPower</c> level; the raw number for one it does not know (3 included).</summary>
    internal static string DescribePower(uint level) => level switch
    {
        0 => "low",
        1 => "50%",
        2 => "100%",
        4 => "75%",
        5 => "25%",
        _ => $"unknown level {level}",
    };
}
