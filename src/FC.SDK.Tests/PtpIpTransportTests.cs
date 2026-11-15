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

    /// <summary>
    /// Stopping the event pump cancels a GetEvent in flight. Its reply used to stay in the stream, and
    /// the next command read it as its own: CloseSession threw "Expected PTP/IP Operation_Response, got
    /// StartDataPacket" in 2 runs of 10 on an EOS 6D (issue #29). The reply is now read to its end
    /// before the next command is written.
    /// </summary>
    [Fact]
    public async Task ACancelledCommandsReplyIsReadBeforeTheNextCommandIsSent()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var asked = new TaskCompletionSource();
        var cancelled = new TaskCompletionSource();
        var camera = AnswerACancelledGetEventLateAsync(listener, asked, cancelled.Task, timeout.Token);

        await using (var transport = new PtpIpTransport("127.0.0.1", port, "test"))
        {
            await transport.ConnectAsync(timeout.Token);
            var session = new PtpSession(transport);

            using var pump = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
            var events = session.SendCommandReceiveDataAsync(PtpOperationCode.CanonGetEvent, pump.Token);
            await asked.Task.WaitAsync(timeout.Token);
            await pump.CancelAsync();
            await Should.ThrowAsync<OperationCanceledException>(() => events);

            // The camera answers the GetEvent only now, when nobody waits for it any more.
            cancelled.SetResult();

            var response = await session.SendCommandAsync(PtpOperationCode.CanonSetRemoteMode, timeout.Token, 0);
            response.Code.ShouldBe(PtpResponseCode.OK);
        }

        (await camera).ShouldBeTrue("the next command was on the wire before the cancelled one's reply");
        listener.Stop();
    }

    /// <summary>
    /// Plays a camera whose GetEvent reply arrives after the caller gave up, then answers the command
    /// that follows. Answers whether that command waited for the late reply.
    /// </summary>
    private static async Task<bool> AnswerACancelledGetEventLateAsync(
        TcpListener listener, TaskCompletionSource asked, Task cancelled, CancellationToken ct)
    {
        var (command, events) = await AcceptHandshakeAsync(listener, ct);
        using var _ = command;
        using var __ = events;
        var commandStream = command.GetStream();

        var getEvent = await ReadPacketAsync(commandStream, ct);
        BinaryPrimitives.ReadUInt16LittleEndian(getEvent.Body.AsSpan(4)).ShouldBe((ushort)PtpOperationCode.CanonGetEvent);
        var txId = BinaryPrimitives.ReadUInt32LittleEndian(getEvent.Body.AsSpan(6));
        asked.SetResult();
        await cancelled.WaitAsync(ct);

        // An empty event list, as a 6D sends it: 8 bytes, the {length 8, type 0} terminator.
        var start = new byte[12];
        BinaryPrimitives.WriteUInt32LittleEndian(start, txId);
        BinaryPrimitives.WriteUInt64LittleEndian(start.AsSpan(4), 8);
        await commandStream.WriteAsync(Packet(PtpIpPacketType.StartDataPacket, start), ct);
        var end = new byte[4 + 8];
        BinaryPrimitives.WriteUInt32LittleEndian(end, txId);
        BinaryPrimitives.WriteUInt32LittleEndian(end.AsSpan(4), 8);
        var nextWasEarly = commandStream.DataAvailable;
        await commandStream.WriteAsync(Packet(PtpIpPacketType.EndDataPacket, end), ct);
        await commandStream.WriteAsync(OkResponse(txId), ct);

        var next = await ReadPacketAsync(commandStream, ct);
        next.Type.ShouldBe(PtpIpPacketType.OperationRequest);
        BinaryPrimitives.ReadUInt16LittleEndian(next.Body.AsSpan(4)).ShouldBe((ushort)PtpOperationCode.CanonSetRemoteMode);
        await commandStream.WriteAsync(OkResponse(BinaryPrimitives.ReadUInt32LittleEndian(next.Body.AsSpan(6))), ct);
        return !nextWasEarly;
    }

    /// <summary>
    /// The other end of the same rule: a cancelled command whose reply never comes must not hold the
    /// next command for the full reply timeout (60 s by default). It gets a short silence limit of its
    /// own, after which the link is refused, so the caller can reconnect.
    /// </summary>
    [Fact]
    public async Task ACancelledCommandsReplyThatNeverComesLosesTheLinkQuickly()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var silent = new TaskCompletionSource();
        var camera = HandshakeThenStaySilentAsync(listener, silent.Task, timeout.Token);

        await using (var transport = new PtpIpTransport("127.0.0.1", port, "test")
        {
            ReplyIdleTimeout = TimeSpan.FromSeconds(60),
            AbandonedReplyIdleTimeout = TimeSpan.FromMilliseconds(300),
        })
        {
            await transport.ConnectAsync(timeout.Token);
            var session = new PtpSession(transport);

            using var pump = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
            var events = session.SendCommandReceiveDataAsync(PtpOperationCode.CanonGetEvent, pump.Token);
            await pump.CancelAsync();
            await Should.ThrowAsync<OperationCanceledException>(() => events);

            // Bounded by the test's own 10 s token: on the 60 s reply timeout this would be cancelled, not refused.
            var lost = await Should.ThrowAsync<IOException>(() => session.SendCommandAsync(PtpOperationCode.CanonSetRemoteMode, timeout.Token, 0));
            lost.Message.ShouldContain("reconnect");
            await Should.ThrowAsync<IOException>(() => session.SendCommandAsync(PtpOperationCode.CloseSession, timeout.Token));
        }

        silent.SetResult();
        await camera;
        listener.Stop();
    }

    /// <summary>
    /// A host that accepts the connection and never answers kept OpenSession waiting for good: the
    /// handshake's socket timeouts did not apply to its async reads (issue #29). It is the pairing
    /// prompt's wait, so the error says what to press.
    /// </summary>
    [Fact]
    public async Task AHostThatNeverAnswersThePairingFailsTheConnect()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var release = new TaskCompletionSource();
        var camera = AcceptAndStaySilentAsync(listener, answerTheCommandConnection: false, release.Task, timeout.Token);

        await using (var transport = new PtpIpTransport("127.0.0.1", port, "test") { PairingTimeout = TimeSpan.FromMilliseconds(300) })
        {
            var failed = await Should.ThrowAsync<IOException>(() => transport.ConnectAsync(timeout.Token));
            failed.Message.ShouldContain("pairing prompt");
        }

        release.SetResult();
        await camera;
        listener.Stop();
    }

    /// <summary>The event connection's acknowledgement has nobody in its way, so it gets the short limit.</summary>
    [Fact]
    public async Task AHostThatNeverAcknowledgesTheEventConnectionFailsTheConnect()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var release = new TaskCompletionSource();
        var camera = AcceptAndStaySilentAsync(listener, answerTheCommandConnection: true, release.Task, timeout.Token);

        await using (var transport = new PtpIpTransport("127.0.0.1", port, "test") { ConnectTimeout = TimeSpan.FromMilliseconds(300) })
        {
            var failed = await Should.ThrowAsync<IOException>(() => transport.ConnectAsync(timeout.Token));
            failed.Message.ShouldContain("event connection");
        }

        release.SetResult();
        await camera;
        listener.Stop();
    }

    /// <summary>The caller's own token still ends a pairing wait, as a cancellation and not as a lost link.</summary>
    [Fact]
    public async Task TheCallersTokenStillCancelsAPairingWait()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var release = new TaskCompletionSource();
        var camera = AcceptAndStaySilentAsync(listener, answerTheCommandConnection: false, release.Task, timeout.Token);

        await using (var transport = new PtpIpTransport("127.0.0.1", port, "test"))
        {
            using var operatorGaveUp = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
            operatorGaveUp.CancelAfter(TimeSpan.FromMilliseconds(300));
            await Should.ThrowAsync<OperationCanceledException>(() => transport.ConnectAsync(operatorGaveUp.Token));
        }

        release.SetResult();
        await camera;
        listener.Stop();
    }

    /// <summary>
    /// Plays a host that takes the command connection and then says nothing: either at once (a pairing
    /// prompt nobody presses), or after acknowledging it, on the event connection.
    /// </summary>
    private static async Task AcceptAndStaySilentAsync(
        TcpListener listener, bool answerTheCommandConnection, Task release, CancellationToken ct)
    {
        using var command = await listener.AcceptTcpClientAsync(ct);
        var commandStream = command.GetStream();
        (await ReadPacketAsync(commandStream, ct)).Type.ShouldBe(PtpIpPacketType.InitCommandRequest);
        if (!answerTheCommandConnection)
        {
            await release;
            return;
        }

        await commandStream.WriteAsync(Packet(PtpIpPacketType.InitCommandAck, new byte[4 + 16 + 2 + 4]), ct);
        using var events = await listener.AcceptTcpClientAsync(ct);
        (await ReadPacketAsync(events.GetStream(), ct)).Type.ShouldBe(PtpIpPacketType.InitEventRequest);
        await release;
    }

    /// <summary>
    /// A Canon body leaves the network for 10 to 20 s after every session closes (issue #29), so a
    /// reconnect started then finds nobody. One connect bounded to 5 s, as 4.0 made it, failed 9 of 9
    /// on an EOS 6D and an EOS R6. The connect is retried until its budget is spent; here the port
    /// refuses at first and starts listening a second later.
    /// </summary>
    [Fact]
    public async Task AConnectIsRetriedUntilTheCameraIsBackOnTheNetwork()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var port = FreePort();

        await using var transport = new PtpIpTransport("127.0.0.1", port, "test") { ConnectBudget = TimeSpan.FromSeconds(20) };
        var connecting = transport.ConnectAsync(timeout.Token);

        // The body comes back: real time, since the refusals before it are the socket's own.
        await Task.Delay(TimeSpan.FromSeconds(1), timeout.Token);
        connecting.IsCompleted.ShouldBeFalse("the connect gave up, or went through, with nothing listening");
        var listener = new TcpListener(IPAddress.Loopback, port);
        listener.Start();
        var camera = AcceptHandshakeAsync(listener, timeout.Token);

        await connecting;
        transport.IsConnected.ShouldBeTrue();

        var (command, events) = await camera;
        command.Dispose();
        events.Dispose();
        listener.Stop();
    }

    /// <summary>
    /// With nobody there for the whole budget the connect fails as an <see cref="IOException"/> that
    /// says how often it tried and what to do, never as the socket's own exception: a refused connect
    /// used to leave as a <see cref="SocketException"/>, which the ConnectWifi remarks did not name.
    /// </summary>
    [Fact]
    public async Task AConnectNobodyAnswersFailsAtItsBudgetAsAnIOException()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var port = FreePort();

        await using var transport = new PtpIpTransport("127.0.0.1", port, "test") { ConnectBudget = TimeSpan.FromSeconds(3) };
        var failed = await Should.ThrowAsync<IOException>(() => transport.ConnectAsync(timeout.Token));

        failed.Message.ShouldContain("attempts");
        failed.Message.ShouldContain("off the network");
        transport.IsConnected.ShouldBeFalse();
    }

    /// <summary>The caller's token ends the retrying at once, as a cancellation.</summary>
    [Fact]
    public async Task TheCallersTokenEndsTheConnectRetries()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var port = FreePort();

        await using var transport = new PtpIpTransport("127.0.0.1", port, "test");
        using var gaveUp = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
        gaveUp.CancelAfter(TimeSpan.FromMilliseconds(300));

        await Should.ThrowAsync<OperationCanceledException>(() => transport.ConnectAsync(gaveUp.Token));
    }

    /// <summary>A loopback port nothing listens on: bound once to learn a free number, then let go.</summary>
    private static int FreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
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
