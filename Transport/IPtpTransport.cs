namespace FC.SDK.Transport;

internal interface IPtpTransport : IAsyncDisposable
{
    Task ConnectAsync(CancellationToken ct = default);

    bool IsConnected { get; }

    ValueTask SendAsync(ReadOnlyMemory<byte> packet, CancellationToken ct = default);

    ValueTask<int> ReceiveAsync(Memory<byte> buffer, CancellationToken ct = default);

    ValueTask<int> ReceiveEventAsync(Memory<byte> buffer, CancellationToken ct = default);
}
