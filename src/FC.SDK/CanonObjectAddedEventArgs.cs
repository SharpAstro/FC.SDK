namespace FC.SDK;

public sealed class CanonObjectAddedEventArgs(uint objectHandle) : EventArgs
{
    public uint ObjectHandle { get; } = objectHandle;
}
