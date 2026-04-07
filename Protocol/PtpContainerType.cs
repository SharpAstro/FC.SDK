namespace FC.SDK.Protocol;

internal enum PtpContainerType : ushort
{
    Command = 0x0001,
    Data = 0x0002,
    Response = 0x0003,
    Event = 0x0004,
}
