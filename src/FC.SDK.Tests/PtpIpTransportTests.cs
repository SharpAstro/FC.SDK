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
