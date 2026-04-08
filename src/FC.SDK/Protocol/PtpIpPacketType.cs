namespace FC.SDK.Protocol;

internal enum PtpIpPacketType : uint
{
    InitCommandRequest = 0x00000001,
    InitCommandAck = 0x00000002,
    InitEventRequest = 0x00000003,
    InitEventAck = 0x00000004,
    InitFail = 0x00000005,
    OperationRequest = 0x00000006,
    OperationResponse = 0x00000007,
    Event = 0x00000008,
    StartDataPacket = 0x00000009,
    DataPacket = 0x0000000A,
    CancelTransaction = 0x0000000B,
    EndDataPacket = 0x0000000C,
    ProbeRequest = 0x0000000D,
    ProbeResponse = 0x0000000E,
}
