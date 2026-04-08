using System.Collections;
using FC.SDK.Transport;

namespace FC.SDK;

public sealed class CanonCameraIterator : IEnumerable<UsbDeviceInfo>
{
    public IEnumerator<UsbDeviceInfo> GetEnumerator()
        => UsbPtpTransport.Enumerate().GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
