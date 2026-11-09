using System.Buffers.Binary;
using FC.SDK.Protocol;

namespace FC.SDK.Transport;

/// <summary>
/// Translates between PTP containers, which is what <see cref="PtpSession"/> speaks, and PTP/IP packets
/// (CIPA DC-005). PTP/IP drops the container's length and type words, moves the data phase into
/// Start_Data/Data/End_Data packets that carry no opcode, and announces data-out in the request itself.
/// </summary>
internal static class PtpIpFraming
{
    public const int HeaderSize = 8;

    /// <summary>DataPhaseInfo: 1 = no data or data-in, 2 = data-out (initiator → responder).</summary>
    private const uint NoDataOrDataIn = 1, DataOut = 2;

    /// <summary>Operation_Request from a PTP command container: header, DataPhaseInfo, code, txid, params.</summary>
    public static byte[] OperationRequest(ReadOnlySpan<byte> command, bool dataOut)
    {
        int paramBytes = command.Length - PtpPacket.HeaderSize;
        var packet = new byte[HeaderSize + 4 + 2 + 4 + paramBytes];
        BinaryPrimitives.WriteUInt32LittleEndian(packet, (uint)packet.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(4), (uint)PtpIpPacketType.OperationRequest);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(8), dataOut ? DataOut : NoDataOrDataIn);
        command[6..].CopyTo(packet.AsSpan(12)); // code, txid, params
        return packet;
    }

    /// <summary>Start_Data then a single End_Data carrying the whole payload of a PTP data container.</summary>
    public static byte[] DataOutPackets(ReadOnlySpan<byte> dataContainer)
    {
        var txId = BinaryPrimitives.ReadUInt32LittleEndian(dataContainer[8..]);
        var payload = dataContainer[PtpPacket.HeaderSize..];

        var packets = new byte[20 + 12 + payload.Length];
        var start = packets.AsSpan(0, 20);
        BinaryPrimitives.WriteUInt32LittleEndian(start, 20);
        BinaryPrimitives.WriteUInt32LittleEndian(start[4..], (uint)PtpIpPacketType.StartDataPacket);
        BinaryPrimitives.WriteUInt32LittleEndian(start[8..], txId);
        BinaryPrimitives.WriteUInt64LittleEndian(start[12..], (ulong)payload.Length);

        var end = packets.AsSpan(20);
        BinaryPrimitives.WriteUInt32LittleEndian(end, (uint)end.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(end[4..], (uint)PtpIpPacketType.EndDataPacket);
        BinaryPrimitives.WriteUInt32LittleEndian(end[8..], txId);
        payload.CopyTo(end[12..]);
        return packets;
    }

    /// <summary>Reads an Operation_Response into <paramref name="destination"/> as a PTP response container.</summary>
    public static async Task<int> ReadResponseAsync(Stream stream, Memory<byte> destination, CancellationToken ct)
    {
        var header = new byte[HeaderSize];
        var (length, type) = await ReadHeaderAsync(stream, header, ct);
        if (type is not PtpIpPacketType.OperationResponse)
            throw new IOException($"Expected PTP/IP Operation_Response, got {type}.");
        return await ReadResponseBodyAsync(stream, length, destination, ct);
    }

    /// <summary>
    /// Reads the reply to a data-in command: the Start_Data … End_Data run, however large, then the
    /// Operation_Response, which lands in <paramref name="responseDestination"/> as a PTP response
    /// container. A command refused before its data phase answers with the response alone.
    /// </summary>
    public static async Task<(byte[] Data, int ResponseLength)> ReadDataAndResponseAsync(
        Stream stream, Memory<byte> responseDestination, CancellationToken ct)
    {
        var header = new byte[HeaderSize];
        var (length, type) = await ReadHeaderAsync(stream, header, ct);
        if (type is PtpIpPacketType.OperationResponse)
            return ([], await ReadResponseBodyAsync(stream, length, responseDestination, ct));
        if (type is not PtpIpPacketType.StartDataPacket)
            throw new IOException($"Expected PTP/IP Start_Data or Operation_Response, got {type}.");

        // body: txid(4) totalLength(8). The total only sizes the buffer; the packets decide the length.
        // The body's size comes from the device's own length word, so it is bounded before allocating.
        if (length - HeaderSize > MaxStartDataBody)
            throw new IOException($"PTP/IP Start_Data declares a {length - HeaderSize}-byte body; it carries 12.");
        var startBody = new byte[length - HeaderSize];
        await stream.ReadExactlyAsync(startBody, ct);
        ulong total = startBody.Length >= 12 ? BinaryPrimitives.ReadUInt64LittleEndian(startBody.AsSpan(4)) : 0;
        using var data = new MemoryStream(total <= MaxPreallocation ? (int)total : 0);

        var chunk = new byte[64 * 1024];
        while (true)
        {
            var (dataLength, dataType) = await ReadHeaderAsync(stream, header, ct);
            if (dataType is not (PtpIpPacketType.DataPacket or PtpIpPacketType.EndDataPacket) || dataLength < HeaderSize + 4)
                throw new IOException($"Expected PTP/IP Data or End_Data, got {dataType} of {dataLength} bytes.");

            await stream.ReadExactlyAsync(header.AsMemory(0, 4), ct); // txid
            for (int remaining = dataLength - HeaderSize - 4; remaining > 0;)
            {
                int n = Math.Min(remaining, chunk.Length);
                await stream.ReadExactlyAsync(chunk.AsMemory(0, n), ct);
                data.Write(chunk, 0, n);
                remaining -= n;
            }

            if (dataType is PtpIpPacketType.EndDataPacket) break;
        }

        int responseLength = await ReadResponseAsync(stream, responseDestination, ct);
        return (data.ToArray(), responseLength);
    }

    /// <summary>
    /// A Start_Data total above this is not trusted to size an allocation up front: anything answering on
    /// port 15740 can announce one. A 25 MB CR3 from an R6 still fits in one go; past this the buffer grows
    /// with the data that actually arrives.
    /// </summary>
    private const ulong MaxPreallocation = 64 * 1024 * 1024;

    /// <summary>A Start_Data body is txid(4) + total(8); this leaves room for padding, not for an allocation.</summary>
    private const int MaxStartDataBody = 64;

    private static async Task<int> ReadResponseBodyAsync(Stream stream, int length, Memory<byte> destination, CancellationToken ct)
    {
        // body: code(2) txid(4) params — the PTP container is the same with length+type in front
        int body = length - HeaderSize;
        int containerLength = 6 + body;
        if (body < 6 || containerLength > destination.Length)
            throw new IOException($"PTP/IP Operation_Response of {length} bytes does not fit a response container.");
        await stream.ReadExactlyAsync(destination.Slice(6, body), ct);
        BinaryPrimitives.WriteUInt32LittleEndian(destination.Span, (uint)containerLength);
        BinaryPrimitives.WriteUInt16LittleEndian(destination.Span[4..], (ushort)PtpContainerType.Response);
        return containerLength;
    }

    public static async Task<(int Length, PtpIpPacketType Type)> ReadHeaderAsync(Stream stream, byte[] header, CancellationToken ct)
    {
        await stream.ReadExactlyAsync(header.AsMemory(0, HeaderSize), ct);
        int length = (int)BinaryPrimitives.ReadUInt32LittleEndian(header);
        var type = (PtpIpPacketType)BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4));
        if (length < HeaderSize)
            throw new IOException($"PTP/IP packet {type} declares an impossible length of {length}.");
        return (length, type);
    }
}
