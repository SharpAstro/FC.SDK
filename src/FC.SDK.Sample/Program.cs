using FC.SDK;
using FC.SDK.Canon;

Console.WriteLine("FC.SDK Sample — Canon EOS camera test");
Console.WriteLine();

// Try WPD first (Windows, no driver swap), then USB
CanonCamera? camera = null;

if (OperatingSystem.IsWindows())
{
    Console.WriteLine("Scanning WPD devices...");
    foreach (var (deviceId, friendlyName) in CanonCamera.EnumerateWpdCameras())
    {
        Console.WriteLine($"  Found: {friendlyName}");
        camera = CanonCamera.ConnectWpd(deviceId);
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
Console.WriteLine($"GetProperty ISO: err={isoErr} val=0x{isoVal:X4}");

// Try SetProperty ISO 800
var setResult = await camera.SetPropertyAsync(EdsPropertyId.ISOSpeed, 0x00000060);
Console.WriteLine($"SetProperty ISO 800: {setResult}");

// Try mirror lockup
var (mluErr, mluSetting) = await camera.GetMirrorUpSettingAsync();
Console.WriteLine($"Mirror lockup: err={mluErr} setting={mluSetting}");

// Set up object download handler
var downloadTcs = new TaskCompletionSource<uint>();
camera.ObjectAdded += (_, e) =>
{
    Console.WriteLine($"ObjectAdded: handle={e.ObjectHandle}");
    downloadTcs.TrySetResult(e.ObjectHandle);
};
camera.StartEventPolling();

// Try TakePicture (no-data command, should work via WPD)
Console.WriteLine("Taking picture (Tv mode)...");
var takeResult = await camera.TakePictureAsync();
Console.WriteLine($"TakePicture: {takeResult}");

if (takeResult is EdsError.OK)
{
    Console.WriteLine("Waiting for image...");
    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
    try
    {
        var handle = await downloadTcs.Task.WaitAsync(cts.Token);
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
}

// Cleanup
await camera.StopEventPollingAsync();
await camera.CloseSessionAsync();
await camera.DisposeAsync();
Console.WriteLine("Done.");
return 0;
