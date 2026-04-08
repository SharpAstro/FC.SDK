using System.Buffers.Binary;

namespace FC.SDK.Protocol;

internal readonly ref struct PtpPacket
{
    public const int HeaderSize = 12;
    public const int MaxParams = 5;

    private readonly Span<byte> _buffer;

    public PtpPacket(Span<byte> buffer) => _buffer = buffer;

    public uint Length => BinaryPrimitives.ReadUInt32LittleEndian(_buffer);
    public PtpContainerType Type => (PtpContainerType)BinaryPrimitives.ReadUInt16LittleEndian(_buffer[4..]);
    public ushort Code => BinaryPrimitives.ReadUInt16LittleEndian(_buffer[6..]);
    public uint TransactionId => BinaryPrimitives.ReadUInt32LittleEndian(_buffer[8..]);

    public Span<byte> Payload => _buffer[HeaderSize..(int)Length];

    public int ParamCount => ((int)Length - HeaderSize) / sizeof(uint);

    public uint Param(int index) =>
        BinaryPrimitives.ReadUInt32LittleEndian(_buffer[(HeaderSize + index * sizeof(uint))..]);

    public static int WriteCommand(Span<byte> dest, PtpOperationCode opCode, uint txId, ReadOnlySpan<uint> @params)
    {
        int length = HeaderSize + @params.Length * sizeof(uint);
        BinaryPrimitives.WriteUInt32LittleEndian(dest, (uint)length);
        BinaryPrimitives.WriteUInt16LittleEndian(dest[4..], (ushort)PtpContainerType.Command);
        BinaryPrimitives.WriteUInt16LittleEndian(dest[6..], (ushort)opCode);
        BinaryPrimitives.WriteUInt32LittleEndian(dest[8..], txId);

        for (int i = 0; i < @params.Length; i++)
            BinaryPrimitives.WriteUInt32LittleEndian(dest[(HeaderSize + i * sizeof(uint))..], @params[i]);

        return length;
    }

    public static int WriteDataHeader(Span<byte> dest, PtpOperationCode opCode, uint txId, int totalDataLength)
    {
        int length = HeaderSize + totalDataLength;
        BinaryPrimitives.WriteUInt32LittleEndian(dest, (uint)length);
        BinaryPrimitives.WriteUInt16LittleEndian(dest[4..], (ushort)PtpContainerType.Data);
        BinaryPrimitives.WriteUInt16LittleEndian(dest[6..], (ushort)opCode);
        BinaryPrimitives.WriteUInt32LittleEndian(dest[8..], txId);
        return HeaderSize;
    }
}
