namespace FC.SDK.Transport;

public readonly record struct UsbDeviceInfo(
    ushort VendorId,
    ushort ProductId,
    string Manufacturer,
    string Product,
    string SerialNumber
);
