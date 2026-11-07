namespace FC.SDK;

/// <summary>The background event pump hit a transport failure and stopped: the body is gone (cable pulled, powered off).</summary>
public sealed class CanonConnectionLostEventArgs(Exception error) : EventArgs
{
    /// <summary>What the transport threw, e.g. an <see cref="IOException"/> "WPD ioctl failed with Win32 error 995".</summary>
    public Exception Error { get; } = error;
}
