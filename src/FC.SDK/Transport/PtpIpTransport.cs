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

    private readonly string _host;
    private readonly int _port;
    private readonly Guid _clientGuid;
    private readonly string _clientName;

    // Every deadline here runs on this clock: the handshake's bounds, the reply's silence limit, the
    // wait for a reply nobody waits for. The socket I/O itself is real either way.
    private readonly TimeProvider _time;

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

    /// <summary>
    /// How long a TCP connect and the Init_Event_Ack may take. Nobody is in the way of either: the
    /// event ack came back within 17 ms on 29 connections to an EOS 6D and an EOS R6 (issue #29).
    /// </summary>
    internal TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// How long the Init_Command_Ack may take. On a first connection the body shows a pairing prompt
    /// and answers only once the operator presses OK, so this bounds a person: the slowest pairing
    /// measured took 20.3 s, and an already paired 6D took 4.26 s on its first connection of a day
    /// (issue #29). The caller's token still ends the wait sooner.
    /// </summary>
    internal TimeSpan PairingTimeout { get; init; } = TimeSpan.FromSeconds(120);

    /// <summary>
    /// How long a reply nobody waits for any more may go silent before the link counts as lost. Much
    /// shorter than <see cref="ReplyIdleTimeout"/>: that one has to outlast a 30 s exposure, while
    /// this one only makes the next command wait, and on a body that has gone silent a short wait
    /// and a refused link is what lets the caller reconnect.
    /// </summary>
    internal TimeSpan AbandonedReplyIdleTimeout { get; init; } = TimeSpan.FromSeconds(5);

    // Once a request is on the wire its reply is read to the end, whoever stopped waiting for it.
    // A cancelled caller used to leave the reply in the stream, and the next command read it as its
    // own: CloseSession failed with "Expected PTP/IP Operation_Response, got StartDataPacket" after
    // a cancelled GetEvent, 2 runs in 10 on an EOS 6D (issue #29). So the read runs on its own, the
    // caller only waits for it, and a read the caller walked away from is kept here until the next
    // command, which waits for it before it writes anything.
    private Task? _abandoned;
    private long _abandonedAt;

    // True from the moment a request is written until its reply has been read.
    private bool _replyOwed;

    // The response container of the exchange in flight. Not the caller's buffer: that one is rented
    // and goes back to its pool when a cancelled caller returns, while the read may still be writing.
    private byte[] _response = new byte[512];

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
    internal PtpIpTransport(
        string host, int port = PtpIpPort, string? clientName = null, Guid? clientGuid = null, TimeProvider? timeProvider = null)
    {
        _time = timeProvider ?? TimeProvider.System;
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
        // Every step is bounded through a token. TcpClient.ReceiveTimeout and SendTimeout, which this
        // used to set, only apply to synchronous calls: with them a host that accepted the connection
        // and never answered kept OpenSession waiting for good (issue #29).

        // Step 1: Command connection
        _commandClient = new TcpClient();
        await BoundedAsync(
            token => _commandClient.ConnectAsync(_host, _port, token), ConnectTimeout,
            $"PTP/IP {_host}:{_port} did not accept a connection within {Seconds(ConnectTimeout)}. " +
            "Check the host, and that the camera's Wi-Fi is on and waiting for a connection.", ct);
        _commandStream = _commandClient.GetStream();

        // Step 2: Send Init_Command_Request, receive Init_Command_ACK
        var initRequest = BuildInitCommandRequest();
        await _commandStream.WriteAsync(initRequest, ct);

        var ackBuf = new byte[512];
        int ackLen = await BoundedAsync(
            token => new ValueTask<int>(ReadPtpIpPacketAsync(_commandStream, ackBuf, token)), PairingTimeout,
            $"PTP/IP {_host} did not answer the connection request within {Seconds(PairingTimeout)}. " +
            "Press OK on the camera's pairing prompt, or check the host.", ct);
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
        await BoundedAsync(
            token => _eventClient.ConnectAsync(_host, _port, token), ConnectTimeout,
            $"PTP/IP {_host}:{_port} did not accept the event connection within {Seconds(ConnectTimeout)}.", ct);
        _eventStream = _eventClient.GetStream();

        // Step 4: Send Init_Event_Request with ConnectionNumber
        var eventRequest = new byte[12];
        BinaryPrimitives.WriteUInt32LittleEndian(eventRequest, 12);
        BinaryPrimitives.WriteUInt32LittleEndian(eventRequest.AsSpan(4), (uint)PtpIpPacketType.InitEventRequest);
        BinaryPrimitives.WriteUInt32LittleEndian(eventRequest.AsSpan(8), connectionNumber);
        await _eventStream.WriteAsync(eventRequest, ct);

        // Step 5: Receive Init_Event_ACK
        var eventAckBuf = new byte[64];
        int eventAckLen = await BoundedAsync(
            token => new ValueTask<int>(ReadPtpIpPacketAsync(_eventStream, eventAckBuf, token)), ConnectTimeout,
            $"PTP/IP {_host} did not acknowledge the event connection within {Seconds(ConnectTimeout)}.", ct);
        var eventAckType = (PtpIpPacketType)BinaryPrimitives.ReadUInt32LittleEndian(eventAckBuf.AsSpan(4));
        if (eventAckType is PtpIpPacketType.InitFail)
            throw InitFailed(eventAckBuf, eventAckLen);
        if (eventAckType is not PtpIpPacketType.InitEventAck)
            throw new IOException($"Expected InitEventAck, got {eventAckType}");
    }

    private static string Seconds(TimeSpan span) => $"{span.TotalSeconds:0.#} s";

    /// <summary>
    /// Runs one handshake step under <paramref name="limit"/>. Running out of it is an
    /// <see cref="IOException"/> saying what to do; the caller's own cancellation stays a cancellation.
    /// </summary>
    private async Task<T> BoundedAsync<T>(
        Func<CancellationToken, ValueTask<T>> step, TimeSpan limit, string timedOut, CancellationToken ct)
    {
        using var expiry = new CancellationTokenSource(limit, _time);
        using var bound = CancellationTokenSource.CreateLinkedTokenSource(ct, expiry.Token);
        try
        {
            return await step(bound.Token);
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new IOException(timedOut, ex);
        }
    }

    private async Task BoundedAsync(
        Func<CancellationToken, ValueTask> step, TimeSpan limit, string timedOut, CancellationToken ct) =>
        await BoundedAsync(async token => { await step(token); return true; }, limit, timedOut, ct);

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
                // A command still held here belongs to an exchange its caller gave up before anything
                // was written. It is dropped, not sent: nobody is waiting for its reply.
                _pendingCommand = null;
                ct.ThrowIfCancellationRequested();
                await SettleAbandonedReplyAsync(ct);
                _pendingCommand = packet.ToArray();
                break;

            case PtpContainerType.Data:
                ThrowIfCancelledBeforeTheRequest(ct);
                await FlushPendingCommandAsync(dataOut: true);
                await WriteAsync(PtpIpFraming.DataOutPackets(packet.Span));
                break;

            default:
                throw new ArgumentException($"Cannot send a PTP {containerType} container.", nameof(packet));
        }
    }

    public async ValueTask<int> ReceiveAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        if (_commandStream is null) throw new InvalidOperationException("Transport not connected.");

        if (_lost is not null) throw _lost;

        ThrowIfCancelledBeforeTheRequest(ct);
        await FlushPendingCommandAsync(dataOut: false);

        var response = ResponseBuffer(buffer.Length);
        int length = await ReplyAsync(PtpIpFraming.ReadResponseAsync(Replies, response, CancellationToken.None), ct);
        response.AsSpan(0, length).CopyTo(buffer.Span);
        return length;
    }

    public async ValueTask<(byte[] Data, int ResponseLength)> ReceiveDataAsync(Memory<byte> responseBuffer, CancellationToken ct = default)
    {
        if (_commandStream is null) throw new InvalidOperationException("Transport not connected.");

        if (_lost is not null) throw _lost;

        ThrowIfCancelledBeforeTheRequest(ct);
        await FlushPendingCommandAsync(dataOut: false);

        var response = ResponseBuffer(responseBuffer.Length);
        var (data, length) = await ReplyAsync(
            PtpIpFraming.ReadDataAndResponseAsync(Replies, response, CancellationToken.None), ct);
        response.AsSpan(0, length).CopyTo(responseBuffer.Span);
        return (data, length);
    }

    private IdleTimeoutStream Replies => _replies ??= new IdleTimeoutStream(_commandStream!, ReplyIdleTimeout, _time);

    private byte[] ResponseBuffer(int size) => _response.Length >= size ? _response : _response = new byte[size];

    private IOException Lose(Exception cause) =>
        _lost = new IOException($"PTP/IP {_host}: the camera stopped answering ({cause.Message}); reconnect.", cause);

    /// <summary>
    /// A caller cancelled before its request was written has sent nothing, so it can simply stop: the
    /// command is dropped and no reply is owed. Once the request is out, cancelling only stops the wait.
    /// </summary>
    private void ThrowIfCancelledBeforeTheRequest(CancellationToken ct)
    {
        if (_pendingCommand is null || !ct.IsCancellationRequested) return;
        _pendingCommand = null;
        ct.ThrowIfCancellationRequested();
    }

    /// <summary>
    /// Waits for a reply that is already being read. The read does not see the caller's token: a
    /// cancelled caller returns at once and the read is left for <see cref="SettleAbandonedReplyAsync"/>.
    /// Any other failure of the read leaves the stream out of step with the camera, so the link is lost.
    /// </summary>
    private async ValueTask<T> ReplyAsync<T>(Task<T> reading, CancellationToken ct)
    {
        try
        {
            var reply = await reading.WaitAsync(ct);
            _replyOwed = false;
            return reply;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            _abandoned = reading;
            _abandonedAt = _time.GetTimestamp();
            // Observed here in case nothing ever settles it (the transport is disposed next).
            _ = reading.ContinueWith(static t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
            throw;
        }
        catch (TimeoutException ex)
        {
            throw Lose(ex);
        }
        catch (IOException ex)
        {
            throw _lost = ex;
        }
    }

    /// <summary>
    /// Before a new command is written: the reply a cancelled caller left behind is read to its end.
    /// It may go silent for <see cref="AbandonedReplyIdleTimeout"/>, counted from the cancel or from
    /// its last byte, so a download that is still arriving is not cut; past that the link is lost.
    /// </summary>
    private async ValueTask SettleAbandonedReplyAsync(CancellationToken ct)
    {
        if (_abandoned is null)
        {
            if (!_replyOwed) return;

            // A request went out and nothing started reading its reply. Read it as either shape.
            _abandoned = PtpIpFraming.ReadDataAndResponseAsync(Replies, ResponseBuffer(_response.Length), CancellationToken.None);
            _abandonedAt = _time.GetTimestamp();
        }

        var reading = _abandoned;
        while (!reading.IsCompleted)
        {
            var since = Math.Max(_abandonedAt, Replies.LastReadTimestamp);
            var left = AbandonedReplyIdleTimeout - _time.GetElapsedTime(since);
            if (left <= TimeSpan.Zero)
            {
                throw Lose(new TimeoutException(
                    $"the reply to a cancelled command went silent for {Seconds(AbandonedReplyIdleTimeout)}"));
            }

            using var wake = CancellationTokenSource.CreateLinkedTokenSource(ct);
            await Task.WhenAny(reading, Task.Delay(left, _time, wake.Token));
            await wake.CancelAsync();
            ct.ThrowIfCancellationRequested();
        }

        _abandoned = null;
        if (reading.IsCompletedSuccessfully)
        {
            _replyOwed = false;
            return;
        }

        var failure = reading.Exception?.InnerException;
        throw failure switch
        {
            IOException io => _lost = io,
            null => Lose(new TimeoutException("the reply to a cancelled command was never read")),
            _ => Lose(failure),
        };
    }

    private async ValueTask FlushPendingCommandAsync(bool dataOut)
    {
        if (_pendingCommand is not { } command) return;
        _pendingCommand = null;
        await WriteAsync(PtpIpFraming.OperationRequest(command, dataOut));
    }

    /// <summary>
    /// Writes a whole request. Not cancellable: half a request on the wire is a stream nothing can
    /// recover, where a whole one only owes a reply. A write that cannot finish within the reply's
    /// own silence limit is a lost link.
    /// </summary>
    private async ValueTask WriteAsync(byte[] packets)
    {
        _replyOwed = true;
        try
        {
            await _commandStream!.WriteAsync(packets, CancellationToken.None).AsTask().WaitAsync(ReplyIdleTimeout, _time);
        }
        catch (TimeoutException ex)
        {
            throw Lose(ex);
        }
        catch (IOException ex)
        {
            throw _lost = ex;
        }
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
        _replies = null;
        _pendingCommand = null;
        _abandoned = null;
        _replyOwed = false;
    }
}
