namespace FC.SDK.Transport;

internal interface IPtpTransport : IAsyncDisposable
{
    Task ConnectAsync(CancellationToken ct = default);

    bool IsConnected { get; }

    /// <summary>
    /// A stable device identifier for the connected camera.
    /// USB: serial number or device path. WiFi: responder GUID from PTP/IP handshake.
    /// </summary>
    string DeviceId { get; }

    ValueTask SendAsync(ReadOnlyMemory<byte> packet, CancellationToken ct = default);

    ValueTask<int> ReceiveAsync(Memory<byte> buffer, CancellationToken ct = default);

    ValueTask<int> ReceiveEventAsync(Memory<byte> buffer, CancellationToken ct = default);
}
