namespace FC.SDK.Canon;

internal readonly struct CanonPtpEvent
{
    public CanonEventType Type { get; init; }
    public uint Param1 { get; init; }
    public uint Param2 { get; init; }
    public uint Param3 { get; init; }
}
