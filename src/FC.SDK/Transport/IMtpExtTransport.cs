namespace FC.SDK.Transport;

/// <summary>
/// A transport that speaks the WPD MTP extension command phases rather than raw PTP containers.
/// </summary>
/// <remarks>
/// <para>
/// Windows' MTP class driver owns the PTP framing — transaction ids, container headers, the
/// bulk endpoints — and exposes only three shapes: a command with no data phase, one that reads,
/// one that writes. <see cref="IPtpTransport.SendAsync"/> and its receive half have nothing to do
/// there, which is why both WPD transports throw from them.
/// </para>
/// <para>
/// The interface exists so <c>PtpSession</c> can route to those three phases without naming a
/// concrete transport. There are two implementations reaching the same driver by different roads —
/// <see cref="WpdPtpTransport"/> through the WPD COM API, <see cref="WpdIoctlPtpTransport"/> through
/// <c>DeviceIoControl</c> — and a session uses one or the other for its whole life. They are not
/// mixed: each opens its own handle to the device, and the camera is a half-duplex, single-session
/// peer.
/// </para>
/// </remarks>
internal interface IMtpExtTransport : IPtpTransport
{
    /// <summary>Executes an operation with no data phase.</summary>
    Task<(ushort ResponseCode, uint[] ResponseParams)> ExecuteCommandAsync(
        ushort opCode, uint[] @params, CancellationToken ct = default);

    /// <summary>Executes an operation whose data phase flows from the camera.</summary>
    Task<(ushort ResponseCode, uint[] ResponseParams, byte[] Data)> ExecuteCommandReadDataAsync(
        ushort opCode, uint[] @params, CancellationToken ct = default);

    /// <summary>Executes an operation whose data phase flows to the camera.</summary>
    Task<(ushort ResponseCode, uint[] ResponseParams)> ExecuteCommandWriteDataAsync(
        ushort opCode, uint[] @params, byte[] data, CancellationToken ct = default);
}
