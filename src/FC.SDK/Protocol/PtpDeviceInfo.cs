using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.Text;

namespace FC.SDK.Protocol;

/// <summary>
/// The PTP <c>DeviceInfo</c> dataset a camera answers <c>GetDeviceInfo</c> (0x1001) with: what it is, who made it, which
/// operations it offers and its serial number. One parser for every reader of it, because the same bytes serve an open
/// session (<see cref="Canon.CanonPtpSession"/>) and a read with no session at all
/// (<see cref="CanonCamera.ReadWpdIdentityAsync"/>).
/// </summary>
/// <remarks>
/// Layout, in order: StandardVersion (u16), VendorExtensionID (u32), VendorExtensionVersion (u16), VendorExtensionDesc
/// (PTP string), FunctionalMode (u16), then five u16 arrays (OperationsSupported, EventsSupported,
/// DevicePropertiesSupported, CaptureFormats, ImageFormats), then four PTP strings (Manufacturer, Model, DeviceVersion,
/// SerialNumber). A PTP string is a u8 character count that includes the terminating NUL, then that many UTF-16LE
/// characters; a count of zero is the empty string. An array is a u32 element count, then the elements.
/// </remarks>
internal sealed record PtpDeviceInfo(
    string Manufacturer,
    string Model,
    string DeviceVersion,
    string SerialNumber,
    IReadOnlySet<ushort> OperationsSupported)
{
    private const int FixedHeaderLength = 8;
    private const int FunctionalModeLength = 2;

    /// <summary>
    /// Parses a DeviceInfo dataset. False for one that is cut short or whose strings run past its end, which a camera in
    /// the middle of resetting can answer; never throws on bad bytes.
    /// </summary>
    internal static bool TryParse(ReadOnlySpan<byte> data, [NotNullWhen(true)] out PtpDeviceInfo? info)
    {
        info = null;

        var offset = FixedHeaderLength;
        if (!TrySkipString(data, ref offset) // VendorExtensionDesc
            || !TrySkip(data, ref offset, FunctionalModeLength)
            || !TryReadUInt16Array(data, ref offset, out var operations)
            || !TrySkipUInt16Array(data, ref offset) // EventsSupported
            || !TrySkipUInt16Array(data, ref offset) // DevicePropertiesSupported
            || !TrySkipUInt16Array(data, ref offset) // CaptureFormats
            || !TrySkipUInt16Array(data, ref offset) // ImageFormats
            || !TryReadString(data, ref offset, out var manufacturer)
            || !TryReadString(data, ref offset, out var model)
            || !TryReadString(data, ref offset, out var deviceVersion)
            || !TryReadString(data, ref offset, out var serialNumber))
        {
            return false;
        }

        info = new PtpDeviceInfo(manufacturer, model, deviceVersion, serialNumber, operations);
        return true;
    }

    private static bool TrySkip(ReadOnlySpan<byte> data, ref int offset, int length)
    {
        if (length < 0 || offset > data.Length - length)
        {
            return false;
        }

        offset += length;
        return true;
    }

    private static bool TrySkipString(ReadOnlySpan<byte> data, ref int offset) => TryReadString(data, ref offset, out _);

    private static bool TryReadString(ReadOnlySpan<byte> data, ref int offset, out string value)
    {
        value = "";
        if (offset >= data.Length)
        {
            return false;
        }

        int charCount = data[offset];
        var start = offset + 1;
        if (charCount == 0)
        {
            offset = start;
            return true;
        }

        if (start > data.Length - charCount * 2)
        {
            return false;
        }

        // The last character is the terminating NUL.
        value = Encoding.Unicode.GetString(data.Slice(start, (charCount - 1) * 2));
        offset = start + charCount * 2;
        return true;
    }

    private static bool TrySkipUInt16Array(ReadOnlySpan<byte> data, ref int offset)
    {
        if (offset > data.Length - 4)
        {
            return false;
        }

        var count = BinaryPrimitives.ReadUInt32LittleEndian(data[offset..]);
        offset += 4;
        return count <= (uint)(data.Length - offset) / 2 && TrySkip(data, ref offset, (int)count * 2);
    }

    private static bool TryReadUInt16Array(ReadOnlySpan<byte> data, ref int offset, out IReadOnlySet<ushort> values)
    {
        var set = new HashSet<ushort>();
        values = set;
        if (offset > data.Length - 4)
        {
            return false;
        }

        var count = BinaryPrimitives.ReadUInt32LittleEndian(data[offset..]);
        offset += 4;
        if (count > (uint)(data.Length - offset) / 2)
        {
            return false;
        }

        for (var i = 0; i < count; i++, offset += 2)
        {
            set.Add(BinaryPrimitives.ReadUInt16LittleEndian(data[offset..]));
        }

        return true;
    }
}
