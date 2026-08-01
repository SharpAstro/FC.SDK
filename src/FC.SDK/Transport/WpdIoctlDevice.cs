using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace FC.SDK.Transport;

/// <summary>
/// An open handle to a WPD device node, driven by <c>DeviceIoControl</c> instead of the WPD COM API.
/// </summary>
/// <remarks>
/// <para>
/// This is the path EDSDK takes: the device-interface path WPD enumerates is a perfectly ordinary
/// Win32 path, and <c>wpdmtp.sys</c> accepts the same property bags <c>PortableDeviceApi.dll</c>
/// would have serialized on the caller's behalf. See <c>docs/canon-windows-transports.md</c>.
/// </para>
/// <para>
/// Completion is genuinely asynchronous — the handle is bound to the thread pool's I/O completion
/// port, so a pending ioctl parks no thread at all. The COM path could not do this: a synchronous
/// <c>IPortableDevice::SendCommand</c> can only be bounded by waiting on it from somewhere else.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
internal sealed partial class WpdIoctlDevice : IDisposable
{
    private readonly SafeFileHandle _handle;
    private readonly ThreadPoolBoundHandle _bound;
    private int _disposed;

    private WpdIoctlDevice(SafeFileHandle handle, ThreadPoolBoundHandle bound)
    {
        _handle = handle;
        _bound = bound;
    }

    /// <summary>Opens a device-interface path, e.g. the id <c>IPortableDeviceManager</c> reports.</summary>
    internal static WpdIoctlDevice Open(string devicePath)
    {
        nint raw = CreateFileW(devicePath, GenericRead | GenericWrite, FileShareRead | FileShareWrite,
            0, OpenExisting, FileFlagOverlapped, 0);

        if (raw == -1)
        {
            throw new IOException(
                $"Could not open '{devicePath}' (Win32 error {Marshal.GetLastPInvokeError()}). " +
                "Another process holding the camera open is the usual cause.");
        }

        var handle = new SafeFileHandle(raw, ownsHandle: true);
        try
        {
            return new WpdIoctlDevice(handle, ThreadPoolBoundHandle.BindHandle(handle));
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Issues one ioctl and returns how many bytes the driver wrote into <paramref name="output"/>.
    /// </summary>
    /// <remarks>
    /// Not cancellable once issued. A pending device ioctl can be pulled back with
    /// <c>CancelIoEx</c>, but doing so races the completion callback over the overlapped's lifetime,
    /// and there is nothing here worth that: the caller's command gate already bounds the wait, and
    /// abandoning it has exactly the same effect on the caller as a cancellation would.
    /// </remarks>
    internal unsafe Task<int> SendAsync(uint controlCode, byte[] input, int inputLength, byte[] output)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);

        var completion = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);

        // Pinned for the whole operation by the overlapped itself, so the addresses taken below stay
        // valid after this method returns — which, for an async ioctl, is the entire point.
        var overlapped = _bound.AllocateNativeOverlapped(OnCompleted, completion, new object[] { input, output });

        try
        {
            byte* inPtr = (byte*)Marshal.UnsafeAddrOfPinnedArrayElement(input, 0);
            byte* outPtr = (byte*)Marshal.UnsafeAddrOfPinnedArrayElement(output, 0);

            bool issued = DeviceIoControl(_handle, controlCode, inPtr, (uint)inputLength,
                outPtr, (uint)output.Length, null, overlapped);

            // Either way the completion packet is queued — synchronous success still posts one,
            // since nothing here asks the kernel to skip it. So the callback owns the overlapped
            // from this point on, and the only case left to clean up after is an outright failure.
            if (!issued && Marshal.GetLastPInvokeError() is var error and not ErrorIoPending)
            {
                _bound.FreeNativeOverlapped(overlapped);
                throw new IOException($"DeviceIoControl(0x{controlCode:X}) failed with Win32 error {error}.");
            }
        }
        catch
        {
            completion.TrySetCanceled();
            throw;
        }

        return completion.Task;
    }

    private unsafe void OnCompleted(uint errorCode, uint bytesTransferred, NativeOverlapped* overlapped)
    {
        var completion = (TaskCompletionSource<int>)ThreadPoolBoundHandle.GetNativeOverlappedState(overlapped)!;
        _bound.FreeNativeOverlapped(overlapped);

        if (errorCode is 0)
            completion.TrySetResult((int)bytesTransferred);
        else
            completion.TrySetException(new IOException($"WPD ioctl failed with Win32 error {errorCode}."));
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        // Order matters: the bound handle must go first, or its finalizer can outlive the handle it
        // is bound to. Closing the handle is also what releases any transfer the driver still holds
        // open — the property that makes per-frame teardown unnecessary here in the first place.
        _bound.Dispose();
        _handle.Dispose();
    }

    // --- ioctl codes ---
    //
    // Both are public, documented constants from PortableDevice.h, not reverse-engineered:
    //   CTL_CODE(FILE_DEVICE_WPD = 0x40, WPD_CONTROL_FUNCTION_GENERIC_MESSAGE = 0x42, METHOD_BUFFERED, access)
    // Only the property-bag format inside them was undocumented.

    /// <summary><c>IOCTL_WPD_MESSAGE_READWRITE_ACCESS</c> — carries every MTP extension command.</summary>
    internal const uint MessageReadWrite = 0x0040C108;

    /// <summary>
    /// <c>IOCTL_WPD_MESSAGE_READ_ACCESS</c> — same function, FILE_READ_ACCESS only.
    /// </summary>
    /// <remarks>
    /// Which code a command takes is not a matter of taste: a driver validates the incoming code
    /// against its own command-access map (PortableDevice.h's WPD_COMMAND_ACCESS_LOOKUP macro), and
    /// that map declares WPD_COMMAND_COMMON_SAVE_CLIENT_INFORMATION as WPD_COMMAND_ACCESS_READ. So
    /// the handshake goes here and everything afterwards uses the read/write code — which is exactly
    /// the split the EDSDK captures show.
    /// </remarks>
    internal const uint MessageRead = 0x00404108;

    // --- Win32 ---

    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint FileShareRead = 1;
    private const uint FileShareWrite = 2;
    private const uint OpenExisting = 3;
    private const uint FileFlagOverlapped = 0x40000000;
    private const int ErrorIoPending = 997;

    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial nint CreateFileW(string fileName, uint desiredAccess, uint shareMode,
        nint securityAttributes, uint creationDisposition, uint flagsAndAttributes, nint templateFile);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool DeviceIoControl(SafeFileHandle device, uint controlCode,
        byte* inBuffer, uint inBufferSize, byte* outBuffer, uint outBufferSize,
        uint* bytesReturned, NativeOverlapped* overlapped);
}
