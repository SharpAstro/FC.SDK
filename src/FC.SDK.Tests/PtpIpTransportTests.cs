using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using FC.SDK.Protocol;
using FC.SDK.Transport;
using Shouldly;
using Xunit;

namespace FC.SDK.Tests;

/// <summary>
/// <see cref="PtpIpTransport"/> against a scripted PTP/IP responder on loopback, each test playing the
/// camera's side of one exchange an EOS R6 got wrong or exposed.
/// </summary>
// Real sockets, so not a unit test: the category lets a unit-only run leave them out
// (`dotnet test --filter "Category!=Integration"`). CI still runs everything.
[Trait("Category", "Integration")]
public class PtpIpTransportTests
{
    /// <summary>
    /// A full-size image through PtpSession: before IPtpDataInTransport the data phase went into a
    /// fixed 512 KB receive buffer and a 25 MB RAW over WiFi failed with IOException.
    /// </summary>
    [Fact]
    public async Task GetObjectDeliversAnImageFarLargerThanTheSessionReceiveBuffer()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var image = new byte[5 * 1024 * 1024 + 3];
        new Random(7).NextBytes(image);
        var camera = ServeObjectAsync(listener, image, timeout.Token);

        await using (var transport = new PtpIpTransport("127.0.0.1", port, "test"))
        {
            await transport.ConnectAsync(timeout.Token);
            var (response, data) = await new PtpSession(transport).SendCommandReceiveDataAsync(PtpOperationCode.CanonGetObject, timeout.Token, 0x1234);

            response.Code.ShouldBe(PtpResponseCode.OK);
            data.ShouldBe(image);
        }

        await camera;
        listener.Stop();
    }

    /// <summary>Plays a camera: the handshake, then one GetObject answered in several Data packets.</summary>
    private static async Task ServeObjectAsync(TcpListener listener, byte[] image, CancellationToken ct)
    {
        var (command, events) = await AcceptHandshakeAsync(listener, ct);
        using var _ = command;
        using var __ = events;
        var commandStream = command.GetStream();

        var request = await ReadPacketAsync(commandStream, ct);
        BinaryPrimitives.ReadUInt16LittleEndian(request.Body.AsSpan(4)).ShouldBe((ushort)PtpOperationCode.CanonGetObject);
        var txId = BinaryPrimitives.ReadUInt32LittleEndian(request.Body.AsSpan(6));

        var start = new byte[12];
        BinaryPrimitives.WriteUInt32LittleEndian(start, txId);
        BinaryPrimitives.WriteUInt64LittleEndian(start.AsSpan(4), (ulong)image.Length);
        await commandStream.WriteAsync(Packet(PtpIpPacketType.StartDataPacket, start), ct);

        const int PacketPayload = 1 << 20;
        for (int offset = 0; offset < image.Length; offset += PacketPayload)
        {
            int n = Math.Min(PacketPayload, image.Length - offset);
            var body = new byte[4 + n];
            BinaryPrimitives.WriteUInt32LittleEndian(body, txId);
            image.AsSpan(offset, n).CopyTo(body.AsSpan(4));
            var type = offset + n == image.Length ? PtpIpPacketType.EndDataPacket : PtpIpPacketType.DataPacket;
            await commandStream.WriteAsync(Packet(type, body), ct);
        }

        await commandStream.WriteAsync(OkResponse(txId), ct);
    }

    /// <summary>
    /// The viewer connected the transport, then <c>OpenSessionAsync</c> connected it again, and the
    /// second handshake opened a second command connection the camera never answered. A real R6
    /// dropped both a minute later.
    /// </summary>
    [Fact]
    public async Task ASecondConnectReusesTheLiveConnectionsAndOpenSessionGoesThrough()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var camera = AnswerOpenSessionAsync(listener, timeout.Token);

        await using (var transport = new PtpIpTransport("127.0.0.1", port, "test"))
        {
            await transport.ConnectAsync(timeout.Token);
            await transport.ConnectAsync(timeout.Token);

            var response = await new PtpSession(transport).SendCommandAsync(PtpOperationCode.OpenSession, timeout.Token, 1);
            response.Code.ShouldBe(PtpResponseCode.OK);
        }

        (await camera).ShouldBeFalse("a second command connection was opened");
        listener.Stop();
    }

    /// <summary>Plays a camera: the handshake, then one OpenSession. Answers whether anything connected again.</summary>
    private static async Task<bool> AnswerOpenSessionAsync(TcpListener listener, CancellationToken ct)
    {
        var (command, events) = await AcceptHandshakeAsync(listener, ct);
        using var _ = command;
        using var __ = events;
        var commandStream = command.GetStream();

        // Anything that connects now is the duplicate handshake this test exists to catch.
        var duplicate = listener.AcceptTcpClientAsync(ct).AsTask();

        var request = await ReadPacketAsync(commandStream, ct);
        request.Type.ShouldBe(PtpIpPacketType.OperationRequest);
        BinaryPrimitives.ReadUInt16LittleEndian(request.Body.AsSpan(4)).ShouldBe((ushort)PtpOperationCode.OpenSession);
        await commandStream.WriteAsync(OkResponse(BinaryPrimitives.ReadUInt32LittleEndian(request.Body.AsSpan(6))), ct);

        return duplicate.IsCompletedSuccessfully;
    }

    /// <summary>
    /// An R6 once stopped answering mid live view and the viewer waited forever. A silent reply now
    /// fails the call as a lost link, and the link stays refused, since a late reply would answer the
    /// wrong command.
    /// </summary>
    [Fact]
    public async Task ACameraThatStopsAnsweringFailsTheCallInsteadOfHangingIt()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var silent = new TaskCompletionSource();
        var camera = HandshakeThenStaySilentAsync(listener, silent.Task, timeout.Token);

        await using (var transport = new PtpIpTransport("127.0.0.1", port, "test") { ReplyIdleTimeout = TimeSpan.FromMilliseconds(300) })
        {
            await transport.ConnectAsync(timeout.Token);
            var session = new PtpSession(transport);

            await Should.ThrowAsync<IOException>(() => session.SendCommandAsync(PtpOperationCode.OpenSession, timeout.Token, 1));
            await Should.ThrowAsync<IOException>(() => session.SendCommandAsync(PtpOperationCode.OpenSession, timeout.Token, 1));
        }

        silent.SetResult();
        await camera;
        listener.Stop();
    }

    private static async Task HandshakeThenStaySilentAsync(TcpListener listener, Task release, CancellationToken ct)
    {
        var (command, events) = await AcceptHandshakeAsync(listener, ct);
        using var _ = command;
        using var __ = events;
        await release; // hold both connections open, answer nothing
    }

    /// <summary>
    /// A handshake that fails after a connection was opened (here the body answers InitFail, as it does
    /// to a GUID it did not pair with) left that connection open, and the next attempt opened new ones
    /// over it.
    /// </summary>
    [Fact]
    public async Task AFailedHandshakeClosesTheConnectionItOpened()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var camera = RefuseHandshakeAsync(listener, timeout.Token);

        await using (var transport = new PtpIpTransport("127.0.0.1", port, "test"))
        {
            var refused = await Should.ThrowAsync<IOException>(() => transport.ConnectAsync(timeout.Token));
            refused.Message.ShouldContain("GUID", Case.Sensitive, "the error should say the pairing was refused");

            // Asked while the transport is still alive: disposing it would close the socket anyway.
            (await camera).ShouldBeTrue("the refused command connection was left open");
        }

        listener.Stop();
    }

    /// <summary>Plays a camera that refuses the pairing. Answers whether the initiator then closed its end.</summary>
    private static async Task<bool> RefuseHandshakeAsync(TcpListener listener, CancellationToken ct)
    {
        using var command = await listener.AcceptTcpClientAsync(ct);
        var commandStream = command.GetStream();
        (await ReadPacketAsync(commandStream, ct)).Type.ShouldBe(PtpIpPacketType.InitCommandRequest);
        await commandStream.WriteAsync(Packet(PtpIpPacketType.InitFail, new byte[4]), ct);

        using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct);
        wait.CancelAfter(TimeSpan.FromSeconds(2));
        try
        {
            return await commandStream.ReadAsync(new byte[1], wait.Token) == 0;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    /// <summary>The camera's side of the four-way handshake. The caller owns both connections.</summary>
    private static async Task<(TcpClient Command, TcpClient Events)> AcceptHandshakeAsync(TcpListener listener, CancellationToken ct)
    {
        var command = await listener.AcceptTcpClientAsync(ct);
        var commandStream = command.GetStream();
        (await ReadPacketAsync(commandStream, ct)).Type.ShouldBe(PtpIpPacketType.InitCommandRequest);
        await commandStream.WriteAsync(Packet(PtpIpPacketType.InitCommandAck, new byte[4 + 16 + 2 + 4]), ct);

        var events = await listener.AcceptTcpClientAsync(ct);
        var eventStream = events.GetStream();
        (await ReadPacketAsync(eventStream, ct)).Type.ShouldBe(PtpIpPacketType.InitEventRequest);
        await eventStream.WriteAsync(Packet(PtpIpPacketType.InitEventAck, []), ct);
        return (command, events);
    }

    private static byte[] OkResponse(uint txId)
    {
        var body = new byte[6];
        BinaryPrimitives.WriteUInt16LittleEndian(body, (ushort)PtpResponseCode.OK);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(2), txId);
        return Packet(PtpIpPacketType.OperationResponse, body);
    }

    private static byte[] Packet(PtpIpPacketType type, byte[] body)
    {
        var packet = new byte[PtpIpFraming.HeaderSize + body.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(packet, (uint)packet.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(4), (uint)type);
        body.CopyTo(packet.AsSpan(PtpIpFraming.HeaderSize));
        return packet;
    }

    private static async Task<(PtpIpPacketType Type, byte[] Body)> ReadPacketAsync(Stream stream, CancellationToken ct)
    {
        var header = new byte[PtpIpFraming.HeaderSize];
        var (length, type) = await PtpIpFraming.ReadHeaderAsync(stream, header, ct);
        var body = new byte[length - PtpIpFraming.HeaderSize];
        await stream.ReadExactlyAsync(body, ct);
        return (type, body);
    }
}
