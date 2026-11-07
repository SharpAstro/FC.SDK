using System.Buffers.Binary;
using FC.SDK.Protocol;
using LibUsbDotNet;
using LibUsbDotNet.Main;

namespace FC.SDK.Transport;

internal sealed class UsbPtpTransport : IPtpTransport, IPtpDataInTransport
{
    public const ushort CanonVendorId = 0x04A9;
    private const byte PtpInterfaceClass = 0x06;

    private readonly UsbDeviceFinder _finder;
    private readonly string _deviceId;
    private UsbDevice? _device;
    private UsbEndpointReader? _bulkIn;
    private UsbEndpointWriter? _bulkOut;
    private UsbEndpointReader? _interruptIn;

    public bool IsConnected => _device?.IsOpen is true;

    public string DeviceId => _deviceId;

    internal UsbPtpTransport(ushort vendorId, ushort productId)
    {
        _finder = new UsbDeviceFinder(vendorId, productId);
        _deviceId = $"{vendorId:X4}:{productId:X4}";
    }

    internal UsbPtpTransport(UsbDeviceInfo info)
        : this(info.VendorId, info.ProductId)
    {
        // Prefer serial number, fall back to device path, then VID:PID
        _deviceId = !string.IsNullOrEmpty(info.SerialNumber) ? info.SerialNumber
            : !string.IsNullOrEmpty(info.DevicePath) ? info.DevicePath
            : $"{info.VendorId:X4}:{info.ProductId:X4}";
    }

    public Task ConnectAsync(CancellationToken ct = default)
    {
        _device = UsbDevice.OpenUsbDevice(_finder)
            ?? throw new InvalidOperationException($"Canon camera not found (VID={_finder.Vid:X4} PID={_finder.Pid:X4}). Ensure WinUSB driver is installed.");

        if (_device is IUsbDevice wholeDevice)
        {
            wholeDevice.SetConfiguration(1);
            wholeDevice.ClaimInterface(0);
        }

        _bulkIn = _device.OpenEndpointReader(ReadEndpointID.Ep01);
        _bulkOut = _device.OpenEndpointWriter(WriteEndpointID.Ep02);
        _interruptIn = _device.OpenEndpointReader(ReadEndpointID.Ep03);

        return Task.CompletedTask;
    }

    public ValueTask SendAsync(ReadOnlyMemory<byte> packet, CancellationToken ct = default)
    {
        if (_bulkOut is null) throw new InvalidOperationException("Transport not connected.");

        var errorCode = _bulkOut.Write(packet.ToArray(), 0, packet.Length, 5000, out _);
        if (errorCode != ErrorCode.None)
            throw new IOException($"USB write failed: {errorCode}");

        return ValueTask.CompletedTask;
    }

    public ValueTask<int> ReceiveAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        if (_bulkIn is null) throw new InvalidOperationException("Transport not connected.");

        var buf = buffer.Length <= 4096
            ? buffer.ToArray()
            : new byte[buffer.Length];

        var errorCode = _bulkIn.Read(buf, 0, buf.Length, 5000, out int bytesRead);
        if (errorCode != ErrorCode.None && errorCode != ErrorCode.IoTimedOut)
            throw new IOException($"USB read failed: {errorCode}");

        buf.AsSpan(0, bytesRead).CopyTo(buffer.Span);
        return ValueTask.FromResult(bytesRead);
    }

    /// <summary>
    /// A data container's own length word says how big it is, so keep reading until it is complete,
    /// however many bulk transfers that takes, then read the response. One fixed-size read cut a
    /// full-size image off at PtpSession's 512 KB receive buffer.
    /// </summary>
    public ValueTask<(byte[] Data, int ResponseLength)> ReceiveDataAsync(Memory<byte> responseBuffer, CancellationToken ct = default)
    {
        if (_bulkIn is null) throw new InvalidOperationException("Transport not connected.");

        var first = new byte[1 << 20]; // a multiple of every bulk max packet size
        int read = ReadBulk(first, 0, first.Length);
        if (read < PtpPacket.HeaderSize)
            throw new IOException($"USB reply began with {read} bytes, short of a PTP header.");

        var type = (PtpContainerType)BinaryPrimitives.ReadUInt16LittleEndian(first.AsSpan(4));
        if (type is PtpContainerType.Response) // refused before any data
        {
            first.AsSpan(0, read).CopyTo(responseBuffer.Span);
            return ValueTask.FromResult<(byte[], int)>(([], read));
        }
        if (type is not PtpContainerType.Data)
            throw new IOException($"Expected a PTP data or response container over USB, got {type}.");

        int total = (int)BinaryPrimitives.ReadUInt32LittleEndian(first);
        if (total < PtpPacket.HeaderSize)
            throw new IOException($"USB data container declares an impossible length of {total}.");
        var data = new byte[total - PtpPacket.HeaderSize];
        int have = Math.Min(read, total) - PtpPacket.HeaderSize;
        first.AsSpan(PtpPacket.HeaderSize, have).CopyTo(data);
        while (have < data.Length)
        {
            int n = ReadBulk(data, have, data.Length - have);
            if (n == 0) throw new IOException($"USB data phase stopped at {have} of {data.Length} bytes.");
            have += n;
        }

        // A data phase that fills its last packet exactly is closed by a zero-length packet.
        var response = new byte[512];
        int responseLength = ReadBulk(response, 0, response.Length);
        if (responseLength == 0) responseLength = ReadBulk(response, 0, response.Length);
        if (responseLength > responseBuffer.Length)
            throw new IOException($"USB response of {responseLength} bytes does not fit a response container.");
        response.AsSpan(0, responseLength).CopyTo(responseBuffer.Span);
        return ValueTask.FromResult((data, responseLength));
    }

    private int ReadBulk(byte[] buffer, int offset, int count)
    {
        var errorCode = _bulkIn!.Read(buffer, offset, count, 5000, out int bytesRead);
        if (errorCode != ErrorCode.None && errorCode != ErrorCode.IoTimedOut)
            throw new IOException($"USB read failed: {errorCode}");
        return bytesRead;
    }

    public ValueTask<int> ReceiveEventAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        if (_interruptIn is null) throw new InvalidOperationException("Transport not connected.");

        var buf = new byte[Math.Min(buffer.Length, 64)];
        var errorCode = _interruptIn.Read(buf, 0, buf.Length, 100, out int bytesRead);

        if (errorCode is ErrorCode.IoTimedOut)
            return ValueTask.FromResult(0);

        if (errorCode != ErrorCode.None)
            throw new IOException($"USB interrupt read failed: {errorCode}");

        buf.AsSpan(0, bytesRead).CopyTo(buffer.Span);
        return ValueTask.FromResult(bytesRead);
    }

    public static IEnumerable<UsbDeviceInfo> Enumerate()
    {
        foreach (UsbRegistry reg in UsbDevice.AllDevices)
        {
            if (reg.Vid != CanonVendorId)
                continue;

            reg.DeviceProperties.TryGetValue("Mfg", out object? mfg);
            reg.DeviceProperties.TryGetValue("DeviceDesc", out object? desc);

            string serial = string.Empty;
            try
            {
                if (reg.Open(out var dev))
                {
                    serial = dev.Info?.SerialString ?? string.Empty;
                    dev.Close();
                }
            }
            catch
            {
                // Device may be in use — serial stays empty
            }

            yield return new UsbDeviceInfo(
                (ushort)reg.Vid,
                (ushort)reg.Pid,
                mfg?.ToString() ?? "Canon",
                desc?.ToString() ?? $"Canon Camera (PID={reg.Pid:X4})",
                serial,
                reg.DevicePath
            );
        }
    }

    public ValueTask DisposeAsync()
    {
        if (_device is IUsbDevice wholeDevice)
        {
            wholeDevice.ReleaseInterface(0);
        }

        _bulkIn?.Dispose();
        _bulkOut?.Dispose();
        _interruptIn?.Dispose();
        _device?.Close();
        _device = null;

        return ValueTask.CompletedTask;
    }
}
