using FC.SDK.Canon;

namespace FC.SDK;

public sealed class CanonPropertyChangedEventArgs(EdsPropertyId propertyId, uint value) : EventArgs
{
    public EdsPropertyId PropertyId { get; } = propertyId;
    public uint Value { get; } = value;
}
