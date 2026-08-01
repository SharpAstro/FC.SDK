using System.Runtime.Versioning;
using FC.SDK.Transport;
using Shouldly;
using Xunit;

namespace FC.SDK.Tests;

/// <summary>
/// The ioctl property-bag encoding used by <see cref="WpdIoctlPtpTransport"/>.
/// </summary>
/// <remarks>
/// <para>
/// This format is not documented anywhere: it is Microsoft's own serialization of
/// <c>IPortableDeviceValues</c>, decoded by diffing our COM traffic against EDSDK's raw ioctls for
/// identical calls (<c>docs/wpd-ioctl-wire-format.md</c>). Nothing validates it at compile time and
/// the driver's only feedback is a failed command, so the encoder is pinned here against bytes that
/// are known to drive a real body.
/// </para>
/// <para>
/// The reference strings come from the proof-of-concept encoder in <c>rev/RawIoctlPoc</c>, which was
/// verified byte-exact against captured EDSDK traffic before this transport was written, and which
/// has since streamed live view from an EOS 450D end to end. A diff here means the shipped transport
/// has drifted from the thing that was actually proven to work.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public class WpdPropertyBagTests
{
    /// <summary>The PoC's client name, so the reference bytes below are directly comparable.</summary>
    private const string ReferenceClientName = "FC.SDK-rawioctl-poc";

    /// <summary>A fixed stand-in for the per-session client-information context.</summary>
    private const string Context = "{5A7B944D-6383-49CE-B8D0-2E1B7CD98DDE}";

    private static string Hex(WpdBagWriter writer) => Convert.ToHexStringLower(writer.Written);

    [Fact]
    public void Client_information_handshake_matches_the_verified_reference()
    {
        const string expected =
            "0d00000003d5150c17d0ce4790167b3f978721cc03000000" +
            "9c2a42f0c85d4044b5bd5df28835658ae9030000" +
            "480000009c2a42f0c85d4044b5bd5df28835658a" +
            "9c2a42f0c85d4044b5bd5df28835658aea030000" +
            "1300000004000000" +
            "9c2a42f0c85d4044b5bd5df28835658af1030000" +
            "0d00000003d5150c17d0ce4790167b3f978721cc04000000" +
            "0c9f4d20922280409f4240664e70f85902000000" +
            "1f00000028000000460043002e00530044004b002d0072006100770069006f00630074006c002d0070006f00630000" +
            "000c9f4d20922280409f4240664e70f8590300000013000000010000000c9f4d20922280409f4240664e70f8590400" +
            "000013000000000000000c9f4d20922280409f4240664e70f859050000001300000000000000";

        Hex(WpdCommands.SaveClientInformation(new byte[64], ReferenceClientName)).ShouldBe(expected);
    }

    [Fact]
    public void Execute_without_data_phase_matches_the_verified_reference()
    {
        // SetRemoteMode (0x9114) with param 1, as command 12 — the first vendor command of a session.
        const string expected =
            "0d00000003d5150c17d0ce4790167b3f978721cc05000000" +
            "9c2a42f0c85d4044b5bd5df28835658aea0300001300000" + "00c000000" +
            "9c2a42f0c85d4044b5bd5df28835658ae9030000" +
            "480000005850544d2e1a0641a357771e0819fc56" +
            "5850544d2e1a0641a357771e0819fc56e9030000" +
            "1300000014910000" +
            "5850544d2e1a0641a357771e0819fc56ea030000" +
            "0d0000002f9ea9086d6d804baf5abaf2bcbe4cb9010000001300000001000000" +
            "9c2a42f0c85d4044b5bd5df28835658af2030000" +
            "1f0000004e0000007b00350041003700420039003400340044002d0036003300380033002d003400390043004500" +
            "2d0042003800440030002d003200450031004200370043004400390038004400440045007d000000";

        Hex(WpdCommands.Execute(new byte[64], Context, commandId: 12, opCode: 0x9114, parameters: [1]))
            .ShouldBe(expected);
    }

    [Fact]
    public void Execute_writes_an_empty_parameter_collection_when_there_are_none()
    {
        // Not an optimisation to skip: without the collection the driver fails vendor data-reads
        // with ELEMENT_NOT_FOUND. Same requirement as the COM path's SetOperationParams.
        var writer = WpdCommands.Execute(new byte[64], Context, commandId: 12, opCode: 0x911D, parameters: []);
        var reader = new WpdBagReader(writer.Written);

        reader.GetUInt32Collection(WpdInterop.WPD_COMMAND_MTP_EXT, WpdInterop.PID_OPERATION_PARAMS)
            .ShouldBeEmpty();
        reader.TryGetUInt32(WpdInterop.WPD_COMMAND_MTP_EXT, WpdInterop.PID_OPERATION_CODE, out uint opCode)
            .ShouldBeTrue();
        opCode.ShouldBe(0x911Du);
    }

    [Fact]
    public void Reader_finds_a_key_that_sits_after_a_nested_collection()
    {
        // The one thing most likely to be silently wrong: seeking past a value means parsing its
        // length, and a collection's length is only knowable by walking its items. Get that wrong
        // and every key after the operation parameters becomes invisible — including the transfer
        // context, which is how a whole data phase would go missing.
        var writer = WpdCommands.Execute(
            new byte[64], Context, commandId: 13, opCode: 0x9153, parameters: [0x00200000, 0, 0]);
        var reader = new WpdBagReader(writer.Written);

        reader.TryGetString(WpdInterop.WPD_COMMAND_COMMON, WpdInterop.PID_CLIENT_INFORMATION_CONTEXT, out string ctx)
            .ShouldBeTrue();
        ctx.ShouldBe(Context);
        reader.GetUInt32Collection(WpdInterop.WPD_COMMAND_MTP_EXT, WpdInterop.PID_OPERATION_PARAMS)
            .ShouldBe([0x00200000u, 0u, 0u]);
    }

    [Fact]
    public void Read_data_reserves_the_landing_buffer_the_driver_fills()
    {
        // TRANSFER_DATA is documented as an output but is really in/out — the caller allocates.
        // Sending only the context and byte count fails even a 341-byte GetDeviceInfo read.
        var writer = WpdCommands.ReadData(new byte[64], Context, "ctx-1", wantBytes: 4096);
        var reader = new WpdBagReader(writer.Written);

        reader.TryGetBytes(WpdInterop.WPD_COMMAND_MTP_EXT, WpdInterop.PID_TRANSFER_DATA, out var landing)
            .ShouldBeTrue();
        landing.Length.ShouldBe(4096);
        reader.TryGetUInt32(WpdInterop.WPD_COMMAND_MTP_EXT, WpdInterop.PID_TRANSFER_NUM_BYTES_TO_READ, out uint want)
            .ShouldBeTrue();
        want.ShouldBe(4096u);
    }

    [Fact]
    public void Write_data_round_trips_its_payload()
    {
        // The 12-byte SetDevicePropValueEx record: [size][propCode][value], size covering the record.
        byte[] payload = [12, 0, 0, 0, 0xB0, 0xD1, 0, 0, 2, 0, 0, 0];

        var writer = WpdCommands.WriteData(new byte[64], Context, "ctx-2", payload);
        var reader = new WpdBagReader(writer.Written);

        reader.TryGetBytes(WpdInterop.WPD_COMMAND_MTP_EXT, WpdInterop.PID_TRANSFER_DATA, out var data).ShouldBeTrue();
        data.ToArray().ShouldBe(payload);
        reader.TryGetString(WpdInterop.WPD_COMMAND_MTP_EXT, WpdInterop.PID_TRANSFER_CONTEXT, out string ctx).ShouldBeTrue();
        ctx.ShouldBe("ctx-2");
    }

    [Fact]
    public void Execute_with_data_to_write_declares_the_total_size()
    {
        var writer = WpdCommands.Execute(
            new byte[64], Context, commandId: 14, opCode: 0x9110, parameters: [], writeSize: 12);
        var reader = new WpdBagReader(writer.Written);

        // Written as VT_UI8, and readable as an integer without the caller caring about the width.
        reader.TryGetUInt32(WpdInterop.WPD_COMMAND_MTP_EXT, WpdInterop.PID_TRANSFER_TOTAL_SIZE, out uint size)
            .ShouldBeTrue();
        size.ShouldBe(12u);
    }

    [Fact]
    public void Writer_grows_past_the_buffer_it_was_given()
    {
        // The transport hands the same buffer back every command and reads Buffer afterwards; a
        // grow that did not survive the struct copy would silently truncate a live-view request.
        var writer = WpdCommands.ReadData(new byte[16], Context, "ctx-3", wantBytes: 2 << 20);

        writer.Buffer.Length.ShouldBeGreaterThan(2 << 20);
        writer.Length.ShouldBeGreaterThan(2 << 20);

        var reader = new WpdBagReader(writer.Written);
        reader.TryGetBytes(WpdInterop.WPD_COMMAND_MTP_EXT, WpdInterop.PID_TRANSFER_DATA, out var landing).ShouldBeTrue();
        landing.Length.ShouldBe(2 << 20);
    }

    [Fact]
    public void Missing_keys_report_absence_rather_than_throwing()
    {
        var writer = WpdCommands.EndDataTransfer(new byte[64], Context, "ctx-4");
        var reader = new WpdBagReader(writer.Written);

        // How the read path decides a command was answered outright with no data phase.
        reader.TryGetUInt32(WpdInterop.WPD_COMMAND_MTP_EXT, WpdInterop.PID_TRANSFER_TOTAL_SIZE, out _).ShouldBeFalse();
        reader.TryGetHResult(WpdInterop.WPD_COMMAND_COMMON, WpdInterop.PID_HRESULT, out _).ShouldBeFalse();
    }
}
