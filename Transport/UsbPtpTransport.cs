using LibUsbDotNet;
using LibUsbDotNet.Main;

namespace FC.SDK.Transport;

internal sealed class UsbPtpTransport : IPtpTransport
{
    public const ushort CanonVendorId = 0x04A9;
    private const byte PtpInterfaceClass = 0x06;

    private readonly UsbDeviceFinder _finder;
    private UsbDevice? _device;
    private UsbEndpointReader? _bulkIn;
    private UsbEndpointWriter? _bulkOut;
    private UsbEndpointReader? _interruptIn;

    public bool IsConnected => _device?.IsOpen is true;

    internal UsbPtpTransport(ushort vendorId, ushort productId)
    {
        _finder = new UsbDeviceFinder(vendorId, productId);
    }

    internal UsbPtpTransport(UsbDeviceInfo info)
        : this(info.VendorId, info.ProductId)
    {
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
                serial
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
