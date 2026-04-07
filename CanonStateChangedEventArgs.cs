using FC.SDK.Canon;

namespace FC.SDK;

public sealed class CanonStateChangedEventArgs(CanonEventType eventType, uint param) : EventArgs
{
    public CanonEventType EventType { get; } = eventType;
    public uint Param { get; } = param;
}
