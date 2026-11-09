using FC.SDK.Protocol;
using FC.SDK.Transport;
using Shouldly;
using Xunit;

namespace FC.SDK.Tests;

/// <summary>
/// PTP container ↔ PTP/IP packet translation, pinned to the CIPA DC-005 byte layout. Before this the
/// transport sent every request six bytes too long, sent a data phase as a second Operation_Request, and
/// handed PtpSession response bodies with no PTP header: OpenSession's 6-byte OK body is shorter than a
/// PTP header, so PtpSession read it as GeneralError.
/// </summary>
public class PtpIpFramingTests
{
    [Fact]
    public void OpenSessionIsTwentyTwoBytes()
    {
        var command = new byte[16];
        int len = PtpPacket.WriteCommand(command, PtpOperationCode.OpenSession, 0, [1]);

        PtpIpFraming.OperationRequest(command.AsSpan(0, len), dataOut: false).ShouldBe(new byte[]
        {
            22, 0, 0, 0,  6, 0, 0, 0,  // length, Operation_Request
            1, 0, 0, 0,                // DataPhaseInfo: no data
            0x02, 0x10,                // OpenSession
            0, 0, 0, 0,                // txid
            1, 0, 0, 0,                // session id
        });
    }

    [Fact]
    public void DataOutIsAnnouncedInTheRequestAndSentAsStartAndEndData()
    {
        var command = new byte[12];
        PtpPacket.WriteCommand(command, PtpOperationCode.CanonSetPropValue, 7, []);
        PtpIpFraming.OperationRequest(command, dataOut: true)[8].ShouldBe((byte)2);

        var data = new byte[12 + 3];
        PtpPacket.WriteDataHeader(data, PtpOperationCode.CanonSetPropValue, 7, 3);
        data[12] = 0xAA; data[13] = 0xBB; data[14] = 0xCC;

        PtpIpFraming.DataOutPackets(data).ShouldBe(new byte[]
        {
            20, 0, 0, 0,  9, 0, 0, 0,  7, 0, 0, 0,  3, 0, 0, 0, 0, 0, 0, 0,  // Start_Data: txid, total
            15, 0, 0, 0,  12, 0, 0, 0,  7, 0, 0, 0,  0xAA, 0xBB, 0xCC,      // End_Data: txid, payload
        });
    }

    [Fact]
    public async Task OperationResponseBecomesAResponseContainer()
    {
        var wire = new MemoryStream(new byte[]
        {
            18, 0, 0, 0,  7, 0, 0, 0,  0x01, 0x20,  5, 0, 0, 0,  0x34, 0x12, 0, 0,  // OK, txid 5, p1
        });
        var buffer = new byte[64];

        int len = await PtpIpFraming.ReadResponseAsync(wire, buffer, CancellationToken.None);

        buffer.AsSpan(0, len).ToArray().ShouldBe(new byte[]
        {
            16, 0, 0, 0,  3, 0,  0x01, 0x20,  5, 0, 0, 0,  0x34, 0x12, 0, 0,
        });
    }

    [Fact]
    public async Task ADataPhaseIsReassembledAndItsResponseFollows()
    {
        var wire = new MemoryStream(new byte[]
        {
            20, 0, 0, 0,  9, 0, 0, 0,  4, 0, 0, 0,  5, 0, 0, 0, 0, 0, 0, 0,  // Start_Data, 5 bytes
            14, 0, 0, 0,  10, 0, 0, 0,  4, 0, 0, 0,  1, 2,                  // Data
            15, 0, 0, 0,  12, 0, 0, 0,  4, 0, 0, 0,  3, 4, 5,               // End_Data
            14, 0, 0, 0,  7, 0, 0, 0,  0x01, 0x20,  4, 0, 0, 0,             // the response that follows
        });
        var response = new byte[32];

        var (data, len) = await PtpIpFraming.ReadDataAndResponseAsync(wire, response, CancellationToken.None);

        data.ShouldBe(new byte[] { 1, 2, 3, 4, 5 });
        response.AsSpan(0, len).ToArray().ShouldBe(new byte[] { 12, 0, 0, 0, 3, 0, 0x01, 0x20, 4, 0, 0, 0 });
    }

    [Fact]
    public async Task ACommandRefusedBeforeItsDataPhaseAnswersWithTheResponseAlone()
    {
        var wire = new MemoryStream(new byte[]
        {
            14, 0, 0, 0,  7, 0, 0, 0,  0x09, 0x20,  6, 0, 0, 0,  // InvalidObjectHandle, txid 6
        });
        var response = new byte[32];

        var (data, len) = await PtpIpFraming.ReadDataAndResponseAsync(wire, response, CancellationToken.None);

        data.ShouldBeEmpty();
        response.AsSpan(0, len).ToArray().ShouldBe(new byte[] { 12, 0, 0, 0, 3, 0, 0x09, 0x20, 6, 0, 0, 0 });
    }

    /// <summary>A full-size image: the old path stopped at PtpSession's 512 KB receive buffer.</summary>
    [Fact]
    public async Task ADataPhaseOfSeveralMegabytesArrivesWhole()
    {
        var payload = new byte[3 * 1024 * 1024 + 17];
        new Random(42).NextBytes(payload);
        var wire = new MemoryStream();
        Write(wire, 9, 7, BitConverter.GetBytes((ulong)payload.Length));
        Write(wire, 10, 7, payload.AsSpan(0, 2_000_000).ToArray());
        Write(wire, 12, 7, payload.AsSpan(2_000_000).ToArray());
        wire.Write([14, 0, 0, 0, 7, 0, 0, 0, 0x01, 0x20, 7, 0, 0, 0]);
        wire.Position = 0;

        var (data, _) = await PtpIpFraming.ReadDataAndResponseAsync(wire, new byte[32], CancellationToken.None);

        data.ShouldBe(payload);
    }

    /// <summary>
    /// A Start_Data body is 12 bytes, but its size came from the packet's own length word, so anything
    /// answering on port 15740 could make the initiator allocate up to 2 GB before sending a byte more.
    /// </summary>
    [Fact]
    public async Task AStartDataThatDeclaresAHugeBodyIsRefused()
    {
        var wire = new MemoryStream(new byte[] { 0xF0, 0xFF, 0xFF, 0x7F, 9, 0, 0, 0 });

        var ex = await Should.ThrowAsync<IOException>(
            () => PtpIpFraming.ReadDataAndResponseAsync(wire, new byte[32], CancellationToken.None));

        ex.Message.ShouldContain("Start_Data");
    }

    /// <summary>
    /// The announced total sized the buffer up to 256 MB before any data arrived. Above a capped amount
    /// it is not trusted, and the buffer grows with the data that really comes.
    /// </summary>
    [Fact]
    public async Task ALargeAnnouncedTotalReservesNothingUntilTheDataArrives()
    {
        var wire = new MemoryStream();
        Write(wire, 9, 7, BitConverter.GetBytes(200UL * 1024 * 1024));
        Write(wire, 12, 7, [1, 2, 3]);
        wire.Write([14, 0, 0, 0, 7, 0, 0, 0, 0x01, 0x20, 7, 0, 0, 0]);
        wire.Position = 0;

        // A MemoryStream completes every read synchronously, so all of it is allocated on this thread.
        long before = GC.GetAllocatedBytesForCurrentThread();
        var (data, _) = await PtpIpFraming.ReadDataAndResponseAsync(wire, new byte[32], CancellationToken.None);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        data.ShouldBe(new byte[] { 1, 2, 3 });
        allocated.ShouldBeLessThan(1024 * 1024);
    }

    private static void Write(Stream wire, uint type, uint txId, byte[] body)
    {
        var header = new byte[12];
        BitConverter.TryWriteBytes(header.AsSpan(0), (uint)(12 + body.Length));
        BitConverter.TryWriteBytes(header.AsSpan(4), type);
        BitConverter.TryWriteBytes(header.AsSpan(8), txId);
        wire.Write(header);
        wire.Write(body);
    }
}
