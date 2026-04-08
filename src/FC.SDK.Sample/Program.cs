using FC.SDK;
using FC.SDK.Canon;

Console.WriteLine("FC.SDK Sample — Canon EOS camera test");
Console.WriteLine();

// Try WPD first (Windows, no driver swap), then USB
CanonCamera? camera = null;
string transport;

if (OperatingSystem.IsWindows())
{
    Console.WriteLine("Scanning WPD devices...");
    foreach (var (deviceId, friendlyName) in CanonCamera.EnumerateWpdCameras())
    {
        Console.WriteLine($"  Found: {friendlyName}");
        camera = CanonCamera.ConnectWpd(deviceId);
        transport = "WPD";
        break;
    }
}

if (camera is null)
{
    Console.WriteLine("Scanning USB devices...");
    foreach (var usb in CanonCamera.EnumerateUsbCameras())
    {
        Console.WriteLine($"  Found: {usb.Product} (SN={usb.SerialNumber})");
        camera = CanonCamera.ConnectUsb(usb);
        transport = "USB";
        break;
    }
}

if (camera is null)
{
    Console.WriteLine("No Canon camera found.");
    return 1;
}

Console.WriteLine("Opening PTP session...");
var result = await camera.OpenSessionAsync();
if (result is not EdsError.OK)
{
    Console.WriteLine($"Failed to open session: {result}");
    return 1;
}
Console.WriteLine($"Connected! DeviceId={camera.DeviceId}");

// Read current ISO
var (isoErr, isoVal) = await camera.GetPropertyAsync(EdsPropertyId.ISOSpeed);
Console.WriteLine($"Current ISO: 0x{isoVal:X4} (err={isoErr})");

// Set ISO 800 (0x60)
Console.WriteLine("Setting ISO 800...");
await camera.SetPropertyAsync(EdsPropertyId.ISOSpeed, 0x00000060);

// Enable mirror lockup
Console.WriteLine("Enabling mirror lockup...");
await camera.EnableMirrorLockupAsync();

// Set up object download handler
var downloadTcs = new TaskCompletionSource<uint>();
camera.ObjectAdded += (_, e) =>
{
    Console.WriteLine($"ObjectAdded: handle={e.ObjectHandle}");
    downloadTcs.TrySetResult(e.ObjectHandle);
};
camera.StartEventPolling();

// Take a 10s bulb exposure
Console.WriteLine("Starting 10s bulb exposure...");
await camera.BulbStartAsync();
await Task.Delay(TimeSpan.FromSeconds(10));
await camera.BulbEndAsync();
Console.WriteLine("Exposure complete, waiting for image...");

// Wait for the image with timeout
using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
try
{
    var handle = await downloadTcs.Task.WaitAsync(cts.Token);

    // Download
    var outputPath = Path.Combine(Environment.CurrentDirectory, $"canon_test_{DateTime.Now:yyyyMMdd_HHmmss}.cr2");
    await using (var fs = new FileStream(outputPath, FileMode.Create, FileAccess.Write))
    {
        await camera.DownloadAsync(handle, fs);
    }
    await camera.TransferCompleteAsync(handle);
    Console.WriteLine($"Image saved: {outputPath}");
}
catch (OperationCanceledException)
{
    Console.WriteLine("Timed out waiting for image.");
}

// Cleanup
await camera.StopEventPollingAsync();
await camera.DisableMirrorLockupAsync();
await camera.CloseSessionAsync();
await camera.DisposeAsync();
Console.WriteLine("Done.");
return 0;
