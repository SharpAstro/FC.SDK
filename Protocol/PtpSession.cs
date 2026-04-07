using System.Buffers;
using System.Buffers.Binary;
using FC.SDK.Transport;

namespace FC.SDK.Protocol;

internal sealed class PtpSession(IPtpTransport transport) : IAsyncDisposable
{
    private uint _nextTransactionId;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private static readonly ArrayPool<byte> Pool = ArrayPool<byte>.Shared;

    private const int MaxCommandSize = PtpPacket.HeaderSize + PtpPacket.MaxParams * sizeof(uint);
    private const int ReceiveBufferSize = 512 * 1024;

    private uint NextTransactionId() => Interlocked.Increment(ref _nextTransactionId);

    internal async Task<PtpResponse> SendCommandAsync(
        PtpOperationCode opCode,
        CancellationToken ct,
        params uint[] @params)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var txId = NextTransactionId();
            var cmdBuf = Pool.Rent(MaxCommandSize);
            try
            {
                int len = PtpPacket.WriteCommand(cmdBuf, opCode, txId, @params);
                await transport.SendAsync(cmdBuf.AsMemory(0, len), ct);

                return await ReceiveResponseAsync(ct);
            }
            finally
            {
                Pool.Return(cmdBuf);
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    internal async Task<PtpResponse> SendCommandWithDataAsync(
        PtpOperationCode opCode,
        ReadOnlyMemory<byte> data,
        CancellationToken ct,
        params uint[] @params)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var txId = NextTransactionId();

            // Send command container
            var cmdBuf = Pool.Rent(MaxCommandSize);
            try
            {
                int cmdLen = PtpPacket.WriteCommand(cmdBuf, opCode, txId, @params);
                await transport.SendAsync(cmdBuf.AsMemory(0, cmdLen), ct);
            }
            finally
            {
                Pool.Return(cmdBuf);
            }

            // Send data container (header + payload)
            var dataBuf = Pool.Rent(PtpPacket.HeaderSize + data.Length);
            try
            {
                int hdrLen = PtpPacket.WriteDataHeader(dataBuf, opCode, txId, data.Length);
                data.Span.CopyTo(dataBuf.AsSpan(hdrLen));
                int totalLen = hdrLen + data.Length;
                await transport.SendAsync(dataBuf.AsMemory(0, totalLen), ct);
            }
            finally
            {
                Pool.Return(dataBuf);
            }

            return await ReceiveResponseAsync(ct);
        }
        finally
        {
            _lock.Release();
        }
    }

    internal async Task<(PtpResponse Response, byte[] Data)> SendCommandReceiveDataAsync(
        PtpOperationCode opCode,
        CancellationToken ct,
        params uint[] @params)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var txId = NextTransactionId();

            // Send command
            var cmdBuf = Pool.Rent(MaxCommandSize);
            try
            {
                int cmdLen = PtpPacket.WriteCommand(cmdBuf, opCode, txId, @params);
                await transport.SendAsync(cmdBuf.AsMemory(0, cmdLen), ct);
            }
            finally
            {
                Pool.Return(cmdBuf);
            }

            // Receive data container(s) then response
            var recvBuf = Pool.Rent(ReceiveBufferSize);
            try
            {
                int received = await transport.ReceiveAsync(recvBuf, ct);
                if (received < PtpPacket.HeaderSize)
                    return (new PtpResponse { Code = PtpResponseCode.GeneralError }, []);

                var type = (PtpContainerType)BinaryPrimitives.ReadUInt16LittleEndian(recvBuf.AsSpan(4));

                if (type is PtpContainerType.Data)
                {
                    uint dataLen = BinaryPrimitives.ReadUInt32LittleEndian(recvBuf.AsSpan());
                    int payloadLen = (int)dataLen - PtpPacket.HeaderSize;
                    var data = new byte[payloadLen];
                    recvBuf.AsSpan(PtpPacket.HeaderSize, payloadLen).CopyTo(data);

                    // Now receive the response container
                    var response = await ReceiveResponseAsync(ct);
                    return (response, data);
                }

                if (type is PtpContainerType.Response)
                {
                    var response = ParseResponse(recvBuf.AsSpan(0, received));
                    return (response, []);
                }

                return (new PtpResponse { Code = PtpResponseCode.GeneralError }, []);
            }
            finally
            {
                Pool.Return(recvBuf);
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task<PtpResponse> ReceiveResponseAsync(CancellationToken ct)
    {
        var buf = Pool.Rent(MaxCommandSize);
        try
        {
            int received = await transport.ReceiveAsync(buf, ct);
            if (received < PtpPacket.HeaderSize)
                return new PtpResponse { Code = PtpResponseCode.GeneralError };

            return ParseResponse(buf.AsSpan(0, received));
        }
        finally
        {
            Pool.Return(buf);
        }
    }

    private static PtpResponse ParseResponse(ReadOnlySpan<byte> buffer)
    {
        var code = (PtpResponseCode)BinaryPrimitives.ReadUInt16LittleEndian(buffer[6..]);
        uint p1 = 0, p2 = 0, p3 = 0;
        int payloadBytes = (int)BinaryPrimitives.ReadUInt32LittleEndian(buffer) - PtpPacket.HeaderSize;

        if (payloadBytes >= 4)
            p1 = BinaryPrimitives.ReadUInt32LittleEndian(buffer[12..]);
        if (payloadBytes >= 8)
            p2 = BinaryPrimitives.ReadUInt32LittleEndian(buffer[16..]);
        if (payloadBytes >= 12)
            p3 = BinaryPrimitives.ReadUInt32LittleEndian(buffer[20..]);

        return new PtpResponse { Code = code, Param1 = p1, Param2 = p2, Param3 = p3 };
    }

    public async ValueTask DisposeAsync()
    {
        _lock.Dispose();
        await transport.DisposeAsync();
    }
}
