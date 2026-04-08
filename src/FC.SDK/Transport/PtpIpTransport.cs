using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using FC.SDK.Protocol;

namespace FC.SDK.Transport;

internal sealed class PtpIpTransport : IPtpTransport
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

    public bool IsConnected => _commandClient?.Connected is true;

    /// <summary>
    /// The responder GUID from the PTP/IP InitCommandAck handshake.
    /// Typically derived from the camera's MAC address — stable across reboots.
    /// Available after <see cref="ConnectAsync"/>.
    /// </summary>
    public Guid ResponderGuid { get; private set; }

    public string DeviceId => ResponderGuid != Guid.Empty ? ResponderGuid.ToString("N") : _host;

    internal PtpIpTransport(string host, int port = PtpIpPort, string? clientName = null)
    {
        _host = host;
        _port = port;
        _clientGuid = Guid.NewGuid();
        _clientName = clientName ?? "FC.SDK";
    }

    public async Task ConnectAsync(CancellationToken ct = default)
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
        if (eventAckType is not PtpIpPacketType.InitEventAck)
            throw new IOException($"Expected InitEventAck, got {eventAckType}");

        // Reset timeouts for normal operation
        _commandClient.ReceiveTimeout = 10000;
        _commandClient.SendTimeout = 10000;
    }

    public async ValueTask SendAsync(ReadOnlyMemory<byte> packet, CancellationToken ct = default)
    {
        if (_commandStream is null) throw new InvalidOperationException("Transport not connected.");

        // Wrap PTP container in PTP/IP Operation_Request (type 0x06)
        // PTP/IP header (8) + DataPhaseInfo (4) + PTP container minus PTP header
        // The PTP/IP operation request embeds: DataPhaseInfo, OpCode, TxId, Params
        // For simplicity, wrap the raw PTP container inside PTP/IP framing
        int ptpIpLen = PtpIpHeaderSize + 4 + packet.Length; // +4 for DataPhaseInfo
        var wrapped = new byte[ptpIpLen];
        BinaryPrimitives.WriteUInt32LittleEndian(wrapped, (uint)ptpIpLen);
        BinaryPrimitives.WriteUInt32LittleEndian(wrapped.AsSpan(4), (uint)PtpIpPacketType.OperationRequest);

        // DataPhaseInfo: 1=no data, 2=sending data
        var ptpType = (PtpContainerType)BinaryPrimitives.ReadUInt16LittleEndian(packet.Span[4..]);
        uint dataPhaseInfo = ptpType is PtpContainerType.Data ? 2u : 1u;
        BinaryPrimitives.WriteUInt32LittleEndian(wrapped.AsSpan(8), dataPhaseInfo);

        // Copy PTP payload (skip PTP length+type fields, keep code+txid+params)
        packet.Span[6..].CopyTo(wrapped.AsSpan(12));

        await _commandStream.WriteAsync(wrapped, ct);
    }

    public async ValueTask<int> ReceiveAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        if (_commandStream is null) throw new InvalidOperationException("Transport not connected.");

        var ptpIpBuf = new byte[buffer.Length + PtpIpHeaderSize];
        int read = await ReadPtpIpPacketAsync(_commandStream, ptpIpBuf, ct);
        if (read < PtpIpHeaderSize) return 0;

        var packetType = (PtpIpPacketType)BinaryPrimitives.ReadUInt32LittleEndian(ptpIpBuf.AsSpan(4));

        // Unwrap PTP/IP to standard PTP container
        int payloadStart = packetType switch
        {
            PtpIpPacketType.OperationResponse => PtpIpHeaderSize,
            PtpIpPacketType.StartDataPacket => PtpIpHeaderSize,
            PtpIpPacketType.EndDataPacket => PtpIpHeaderSize,
            PtpIpPacketType.DataPacket => PtpIpHeaderSize,
            _ => PtpIpHeaderSize,
        };

        int payloadLen = read - payloadStart;
        if (payloadLen > 0)
            ptpIpBuf.AsSpan(payloadStart, payloadLen).CopyTo(buffer.Span);

        return payloadLen;
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

    public async ValueTask DisposeAsync()
    {
        if (_commandStream is not null) await _commandStream.DisposeAsync();
        if (_eventStream is not null) await _eventStream.DisposeAsync();
        _commandClient?.Dispose();
        _eventClient?.Dispose();
        _commandClient = null;
        _eventClient = null;
    }
}
