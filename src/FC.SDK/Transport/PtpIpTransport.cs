using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using FC.SDK.Protocol;

namespace FC.SDK.Transport;

internal sealed class PtpIpTransport : IPtpTransport, IPtpDataInTransport
{
    public const int PtpIpPort = 15740;
    private const int PtpIpHeaderSize = 8;
    private const int HandshakeTimeoutMs = 5000;

    private readonly string _host;
    private readonly int _port;
    private readonly Guid _clientGuid;
    private readonly string _clientName;

    private TcpClient? _commandClient;
    private TcpClient? _eventClient;
    private NetworkStream? _commandStream;
    private NetworkStream? _eventStream;
    private bool _connected;

    /// <summary>
    /// How long a reply may go silent before the link counts as lost. Longer than the R6's longest
    /// timed exposure (30 s) plus its processing, since a release is answered only after it.
    /// </summary>
    internal TimeSpan ReplyIdleTimeout { get; init; } = TimeSpan.FromSeconds(60);

    // Replies are read through this so a camera that stops answering fails the call instead of
    // hanging it; it happened on an R6 mid live view. After that the stream is out of step with the
    // camera (a late reply would answer the wrong command), so the link stays refused.
    private IdleTimeoutStream? _replies;
    private IOException? _lost;

    // A PTP command is held until the next call says whether a data-out phase follows: PTP/IP
    // announces that in the request itself, while PtpSession sends the data container separately.
    private byte[]? _pendingCommand;

    public bool IsConnected => _commandClient?.Connected is true;

    /// <summary>
    /// The responder GUID from the PTP/IP InitCommandAck handshake.
    /// Typically derived from the camera's MAC address — stable across reboots.
    /// Available after <see cref="ConnectAsync"/>.
    /// </summary>
    public Guid ResponderGuid { get; private set; }

    public string DeviceId => ResponderGuid != Guid.Empty ? ResponderGuid.ToString("N") : _host;

    /// <param name="clientGuid">
    /// The identity a Canon body pairs with. It answers InitFail to any other GUID afterwards, so a
    /// caller that reconnects must pass the same one every time; null draws a fresh one.
    /// </param>
    internal PtpIpTransport(string host, int port = PtpIpPort, string? clientName = null, Guid? clientGuid = null)
    {
        _host = host;
        _port = port;
        _clientGuid = clientGuid ?? Guid.NewGuid();
        _clientName = clientName ?? "FC.SDK";
    }

    public async Task ConnectAsync(CancellationToken ct = default)
    {
        // Idempotent, like both WPD transports: the viewer connects the transport, then
        // CanonCamera.OpenSessionAsync connects it again. A second handshake opened a second command
        // connection the body never answered, and it dropped both a minute later.
        if (_connected) return;

        try
        {
            await HandshakeAsync(ct);
        }
        catch
        {
            // Close whatever the handshake opened before it failed, or the next attempt opens new
            // connections over it and the old ones live until finalized.
            try
            {
                await CloseAsync();
            }
            catch
            {
                // The handshake's own failure is the one to report, not a failure to tidy up after it.
            }

            throw;
        }

        _connected = true;
    }

    private async Task HandshakeAsync(CancellationToken ct)
    {
        // Step 1: Command connection
        _commandClient = new TcpClient();
        _commandClient.ReceiveTimeout = HandshakeTimeoutMs;
        _commandClient.SendTimeout = HandshakeTimeoutMs;
        await _commandClient.ConnectAsync(_host, _port, ct);
        _commandStream = _commandClient.GetStream();

        // Step 2: Send Init_Command_Request, receive Init_Command_ACK
        var initRequest = BuildInitCommandRequest();
        await _commandStream.WriteAsync(initRequest, ct);

        var ackBuf = new byte[512];
        int ackLen = await ReadPtpIpPacketAsync(_commandStream, ackBuf, ct);
        var ackType = (PtpIpPacketType)BinaryPrimitives.ReadUInt32LittleEndian(ackBuf.AsSpan(4));
        if (ackType is PtpIpPacketType.InitFail)
            throw InitFailed(ackBuf, ackLen);
        if (ackType is not PtpIpPacketType.InitCommandAck)
            throw new IOException($"Expected InitCommandAck, got {ackType}");

        uint connectionNumber = BinaryPrimitives.ReadUInt32LittleEndian(ackBuf.AsSpan(8));

        // Parse responder GUID (bytes 12–27) — typically MAC-derived, stable across reboots
        if (ackLen >= 28)
        {
            ResponderGuid = new Guid(ackBuf.AsSpan(12, 16));
        }

        // Step 3: Event connection
        _eventClient = new TcpClient();
        _eventClient.ReceiveTimeout = HandshakeTimeoutMs;
        await _eventClient.ConnectAsync(_host, _port, ct);
        _eventStream = _eventClient.GetStream();

        // Step 4: Send Init_Event_Request with ConnectionNumber
        var eventRequest = new byte[12];
        BinaryPrimitives.WriteUInt32LittleEndian(eventRequest, 12);
        BinaryPrimitives.WriteUInt32LittleEndian(eventRequest.AsSpan(4), (uint)PtpIpPacketType.InitEventRequest);
        BinaryPrimitives.WriteUInt32LittleEndian(eventRequest.AsSpan(8), connectionNumber);
        await _eventStream.WriteAsync(eventRequest, ct);

        // Step 5: Receive Init_Event_ACK
        var eventAckBuf = new byte[64];
        int eventAckLen = await ReadPtpIpPacketAsync(_eventStream, eventAckBuf, ct);
        var eventAckType = (PtpIpPacketType)BinaryPrimitives.ReadUInt32LittleEndian(eventAckBuf.AsSpan(4));
        if (eventAckType is PtpIpPacketType.InitFail)
            throw InitFailed(eventAckBuf, eventAckLen);
        if (eventAckType is not PtpIpPacketType.InitEventAck)
            throw new IOException($"Expected InitEventAck, got {eventAckType}");

        // Reset timeouts for normal operation
        _commandClient.ReceiveTimeout = 10000;
        _commandClient.SendTimeout = 10000;
    }

    /// <summary>
    /// A Canon body pairs with the client GUID of its first connection and answers Init_Fail to any other
    /// afterwards, so say that rather than only naming the packet type.
    /// </summary>
    private IOException InitFailed(byte[] packet, int length)
    {
        var reason = length >= PtpIpHeaderSize + 4
            ? $" (reason 0x{BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(PtpIpHeaderSize)):X})"
            : "";
        return new IOException(
            $"PTP/IP {_host} refused the connection with Init_Fail{reason}. A Canon body pairs with one client GUID " +
            "and refuses any other: reconnect with the GUID it was paired under, or clear the pairing on the camera.");
    }

    public async ValueTask SendAsync(ReadOnlyMemory<byte> packet, CancellationToken ct = default)
    {
        if (_commandStream is null) throw new InvalidOperationException("Transport not connected.");
        if (_lost is not null) throw _lost;

        var containerType = (PtpContainerType)BinaryPrimitives.ReadUInt16LittleEndian(packet.Span[4..]);
        switch (containerType)
        {
            case PtpContainerType.Command:
                await FlushPendingCommandAsync(dataOut: false, ct);
                _pendingCommand = packet.ToArray();
                break;

            case PtpContainerType.Data:
                await FlushPendingCommandAsync(dataOut: true, ct);
                await _commandStream.WriteAsync(PtpIpFraming.DataOutPackets(packet.Span), ct);
                break;

            default:
                throw new ArgumentException($"Cannot send a PTP {containerType} container.", nameof(packet));
        }
    }

    public async ValueTask<int> ReceiveAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        if (_commandStream is null) throw new InvalidOperationException("Transport not connected.");

        if (_lost is not null) throw _lost;

        await FlushPendingCommandAsync(dataOut: false, ct);
        try
        {
            return await PtpIpFraming.ReadResponseAsync(Replies, buffer, ct);
        }
        catch (TimeoutException ex)
        {
            throw Lose(ex);
        }
    }

    public async ValueTask<(byte[] Data, int ResponseLength)> ReceiveDataAsync(Memory<byte> responseBuffer, CancellationToken ct = default)
    {
        if (_commandStream is null) throw new InvalidOperationException("Transport not connected.");

        if (_lost is not null) throw _lost;

        await FlushPendingCommandAsync(dataOut: false, ct);
        try
        {
            return await PtpIpFraming.ReadDataAndResponseAsync(Replies, responseBuffer, ct);
        }
        catch (TimeoutException ex)
        {
            throw Lose(ex);
        }
    }

    private IdleTimeoutStream Replies => _replies ??= new IdleTimeoutStream(_commandStream!, ReplyIdleTimeout);

    private IOException Lose(TimeoutException cause) =>
        _lost = new IOException($"PTP/IP {_host}: the camera stopped answering ({cause.Message}); reconnect.", cause);

    private async ValueTask FlushPendingCommandAsync(bool dataOut, CancellationToken ct)
    {
        if (_pendingCommand is not { } command) return;
        _pendingCommand = null;
        await _commandStream!.WriteAsync(PtpIpFraming.OperationRequest(command, dataOut), ct);
    }

    public async ValueTask<int> ReceiveEventAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        if (_eventStream is null) throw new InvalidOperationException("Transport not connected.");

        if (!_eventStream.DataAvailable)
            return 0;

        var buf = new byte[buffer.Length + PtpIpHeaderSize];
        int read = await ReadPtpIpPacketAsync(_eventStream, buf, ct);
        if (read <= PtpIpHeaderSize) return 0;

        int payloadLen = read - PtpIpHeaderSize;
        buf.AsSpan(PtpIpHeaderSize, payloadLen).CopyTo(buffer.Span);
        return payloadLen;
    }

    private static async Task<int> ReadPtpIpPacketAsync(NetworkStream stream, byte[] buffer, CancellationToken ct)
    {
        // Read 4-byte length header first
        int headerRead = 0;
        while (headerRead < 4)
        {
            int n = await stream.ReadAsync(buffer.AsMemory(headerRead, 4 - headerRead), ct);
            if (n == 0) return 0;
            headerRead += n;
        }

        uint packetLength = BinaryPrimitives.ReadUInt32LittleEndian(buffer);
        if (packetLength > (uint)buffer.Length)
            throw new IOException($"PTP/IP packet too large: {packetLength} bytes");

        // Read remaining bytes
        int totalRead = 4;
        while (totalRead < (int)packetLength)
        {
            int n = await stream.ReadAsync(buffer.AsMemory(totalRead, (int)packetLength - totalRead), ct);
            if (n == 0) break;
            totalRead += n;
        }

        return totalRead;
    }

    private byte[] BuildInitCommandRequest()
    {
        var nameBytes = Encoding.Unicode.GetBytes(_clientName + '\0');
        int length = PtpIpHeaderSize + 16 + nameBytes.Length + 4; // header + GUID + name + version
        var buf = new byte[length];

        BinaryPrimitives.WriteUInt32LittleEndian(buf, (uint)length);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(4), (uint)PtpIpPacketType.InitCommandRequest);
        _clientGuid.ToByteArray().CopyTo(buf.AsSpan(8));
        nameBytes.CopyTo(buf.AsSpan(24));
        // PTP/IP version 1.0 (minor=0, major=1 as uint32 LE → 0x00010000)
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(24 + nameBytes.Length), 0x00010000);

        return buf;
    }

    public ValueTask DisposeAsync() => CloseAsync();

    private async ValueTask CloseAsync()
    {
        if (_commandStream is not null) await _commandStream.DisposeAsync();
        if (_eventStream is not null) await _eventStream.DisposeAsync();
        _commandClient?.Dispose();
        _eventClient?.Dispose();
        _commandStream = null;
        _eventStream = null;
        _commandClient = null;
        _eventClient = null;
    }
}
