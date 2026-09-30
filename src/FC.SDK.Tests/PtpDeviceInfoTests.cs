using System.Text;
using FC.SDK.Protocol;
using Shouldly;
using Xunit;

namespace FC.SDK.Tests;

/// <summary>
/// The PTP DeviceInfo parser. Datasets are built here to the ISO 15740 layout rather than captured from a camera: a real
/// one carries a real serial number, and what needs pinning is the layout and what happens to bytes that are wrong.
/// </summary>
public class PtpDeviceInfoTests
{
    private const string Serial = "00112233445566778899aabbccddeeff";

    [Fact]
    public void ReadsTheStringsAndTheOperationsOfACanonShapedDataset()
    {
        var data = Dataset(operations: [0x1001, 0x1002, 0x9115], manufacturer: "Canon Inc.", model: "Canon EOS 6D", version: "3-1.1.3", serial: Serial);

        PtpDeviceInfo.TryParse(data, out var info).ShouldBeTrue();

        info!.Manufacturer.ShouldBe("Canon Inc.");
        info.Model.ShouldBe("Canon EOS 6D");
        info.DeviceVersion.ShouldBe("3-1.1.3");
        info.SerialNumber.ShouldBe(Serial);
        info.OperationsSupported.ShouldBe([(ushort)0x1001, (ushort)0x1002, (ushort)0x9115], ignoreOrder: true);
    }

    [Fact]
    public void AVendorExtensionDescriptionDoesNotShiftTheFieldsAfterIt()
    {
        var data = Dataset(operations: [0x1001], manufacturer: "Canon Inc.", model: "Canon EOS R5", version: "1.8.2", serial: Serial,
            vendorDescription: "canon.com: 1.0;");

        PtpDeviceInfo.TryParse(data, out var info).ShouldBeTrue();

        info!.Model.ShouldBe("Canon EOS R5");
        info.SerialNumber.ShouldBe(Serial);
    }

    [Fact]
    public void AnEmptySerialIsEmptyRatherThanAFailure()
    {
        // An absent string is a zero count with no characters: a body that keeps no serial, which is an answer.
        var data = Dataset(operations: [0x1001], manufacturer: "Canon Inc.", model: "Canon EOS 450D", version: "1.1.0", serial: "");

        PtpDeviceInfo.TryParse(data, out var info).ShouldBeTrue();

        info!.Model.ShouldBe("Canon EOS 450D");
        info.SerialNumber.ShouldBe("");
    }

    [Fact]
    public void ADatasetCutShortAnywhereIsRefusedAndNeverThrows()
    {
        var data = Dataset(operations: [0x1001, 0x1002], manufacturer: "Canon Inc.", model: "Canon EOS 6D", version: "3-1.1.3", serial: Serial);

        for (var length = 0; length < data.Length; length++)
        {
            var cut = data.AsSpan(0, length);
            var parsed = PtpDeviceInfo.TryParse(cut, out var info);

            // Only a cut that still holds all four strings can succeed, and the serial is the last of them, so a cut
            // anywhere short of its last character is refused. (A cut inside the serial's own NUL is the one byte that
            // leaves the text intact, and is not asserted either way.)
            if (length < data.Length - 2)
            {
                parsed.ShouldBeFalse($"a dataset cut to {length} of {data.Length} bytes");
                info.ShouldBeNull();
            }
        }
    }

    [Fact]
    public void AnArrayThatClaimsMoreElementsThanTheDatasetHoldsIsRefused()
    {
        var data = Dataset(operations: [0x1001], manufacturer: "Canon Inc.", model: "Canon EOS 6D", version: "3-1.1.3", serial: Serial);
        // OperationsSupported' element count sits right after the 8 fixed bytes, the (here empty) description and
        // the 2-byte FunctionalMode: 8 + 1 + 2.
        BitConverter.GetBytes(0xFFFFFFFFu).CopyTo(data, 11);

        PtpDeviceInfo.TryParse(data, out var info).ShouldBeFalse();
        info.ShouldBeNull();
    }

    [Fact]
    public void AStringThatRunsPastTheEndIsRefused()
    {
        var data = Dataset(operations: [0x1001], manufacturer: "Canon Inc.", model: "Canon EOS 6D", version: "3-1.1.3", serial: Serial);
        // Claim the last string is 255 characters long; there are not that many bytes left.
        var serialStart = data.Length - ((Serial.Length + 1) * 2 + 1);
        data[serialStart] = 255;

        PtpDeviceInfo.TryParse(data, out _).ShouldBeFalse();
    }

    [Fact]
    public void NothingAtAllIsRefused()
    {
        PtpDeviceInfo.TryParse([], out var info).ShouldBeFalse();
        info.ShouldBeNull();
    }

    private static byte[] Dataset(ushort[] operations, string manufacturer, string model, string version, string serial, string vendorDescription = "")
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);

        writer.Write((ushort)100);   // StandardVersion
        writer.Write(0x0000000Bu);   // VendorExtensionID
        writer.Write((ushort)100);   // VendorExtensionVersion
        PtpString(writer, vendorDescription);
        writer.Write((ushort)0);     // FunctionalMode
        UInt16Array(writer, operations);
        UInt16Array(writer, [0xC101]);            // EventsSupported
        UInt16Array(writer, [0x5001, 0xD101]);    // DevicePropertiesSupported
        UInt16Array(writer, [0x3801]);            // CaptureFormats
        UInt16Array(writer, [0x3801, 0xB101]);    // ImageFormats
        PtpString(writer, manufacturer);
        PtpString(writer, model);
        PtpString(writer, version);
        PtpString(writer, serial);

        return stream.ToArray();
    }

    private static void UInt16Array(BinaryWriter writer, ushort[] values)
    {
        writer.Write((uint)values.Length);
        foreach (var value in values)
        {
            writer.Write(value);
        }
    }

    private static void PtpString(BinaryWriter writer, string value)
    {
        if (value.Length == 0)
        {
            writer.Write((byte)0);
            return;
        }

        writer.Write((byte)(value.Length + 1)); // the count includes the terminating NUL
        writer.Write(Encoding.Unicode.GetBytes(value));
        writer.Write((ushort)0);
    }
}
