namespace FC.SDK.Transport;

/// <summary>
/// A transport that receives a data-in phase whole, at whatever size the camera sends, instead of into
/// <see cref="Protocol.PtpSession"/>'s fixed receive buffer. PTP/IP announces the size in Start_Data, so a
/// 25 MB RAW over WiFi needs no buffer guessed in advance.
/// </summary>
internal interface IPtpDataInTransport
{
    /// <summary>
    /// Receives the data phase (empty when the camera refused before sending any) and then the response,
    /// written to <paramref name="responseBuffer"/> as a PTP response container.
    /// </summary>
    ValueTask<(byte[] Data, int ResponseLength)> ReceiveDataAsync(Memory<byte> responseBuffer, CancellationToken ct = default);
}
