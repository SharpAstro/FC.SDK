using FC.SDK;
using FC.SDK.Canon;

Console.WriteLine("FC.SDK — WPD Canon Camera Sample");

CanonCamera? camera = null;
if (OperatingSystem.IsWindows())
{
    foreach (var (deviceId, friendlyName) in CanonCamera.EnumerateWpdCameras())
    {
        Console.WriteLine($"Found: {friendlyName}");
        camera = CanonCamera.ConnectWpd(deviceId);
        break;
    }
}
if (camera is null) { Console.WriteLine("No camera."); return 1; }

Console.WriteLine("Opening session...");
var err = await camera.OpenSessionAsync();
Console.WriteLine($"OpenSession: {err}");

if (err is not EdsError.OK) { await camera.DisposeAsync(); return 1; }
Console.WriteLine($"Battery: {camera.BatteryLevelPercent}%");
Console.WriteLine($"Model: {camera.Model}  Serial: {camera.SerialNumber}");

// --- Camera status ---
var (_, aeMode) = await camera.GetAEModeAsync();
var (_, iso) = await camera.GetISOAsync();
var (_, tv) = await camera.GetShutterSpeedAsync();
var (_, av) = await camera.GetApertureAsync();
Console.WriteLine($"Mode: {aeMode}  ISO: {iso}  Tv: {tv}  Av: {av}");

var (shotErr, shots) = await camera.GetAvailableShotsAsync();
if (shotErr is EdsError.OK) Console.WriteLine($"Available shots: {shots}");

var (tempErr, temp) = await camera.GetTempStatusAsync();
if (tempErr is EdsError.OK) Console.WriteLine($"Temperature status: {temp}");

var (hisoErr, hisoNr) = await camera.GetHighIsoNRAsync();
if (hisoErr is EdsError.OK) Console.WriteLine($"High ISO NR: {hisoNr}");

// Disable auto power-off so the camera stays awake during long sessions
var apErr = await camera.SetAutoPowerOffAsync(0);
if (apErr is EdsError.OK) Console.WriteLine("Auto power-off disabled");

// --- Capture ---
err = await camera.SetSaveToAsync(EdsSaveTo.Host);
Console.WriteLine($"\nSaveTo=Host: {err}");

// Wait for ObjectAdded event after capture
var objectTcs = new TaskCompletionSource<uint>();
camera.ObjectAdded += (_, e) =>
{
    Console.WriteLine($"  ObjectAdded: handle=0x{e.ObjectHandle:X8}");
    objectTcs.TrySetResult(e.ObjectHandle);
};

camera.StartEventPolling(TimeSpan.FromMilliseconds(200));

Console.WriteLine("Taking picture...");
err = await camera.TakePictureAsync();
Console.WriteLine($"TakePicture: {err}");
if (err is not EdsError.OK) { await camera.StopEventPollingAsync(); await camera.CloseSessionAsync(); await camera.DisposeAsync(); return 1; }

// Wait for the ObjectAdded event (timeout 10s)
Console.WriteLine("Waiting for image...");
using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
try
{
    var handle = await objectTcs.Task.WaitAsync(cts.Token);

    // Query the camera for the real filename (handles CR2, CR3, JPG, etc.)
    var (infoErr, fileName) = await camera.GetObjectFileNameAsync(handle);
    var extension = Path.GetExtension(fileName) ?? ".cr2";
    Console.WriteLine($"  FileName: {fileName ?? "(unknown)"}");

    // Quick thumbnail preview
    var (thumbErr, thumbData) = await camera.GetThumbAsync(handle);
    if (thumbErr is EdsError.OK && thumbData.Length > 0)
        Console.WriteLine($"  Thumbnail: {thumbData.Length:N0} bytes JPEG");

    // Download the full image
    var outPath = Path.Combine(Environment.CurrentDirectory, $"capture_{DateTime.Now:yyyyMMdd_HHmmss}{extension}");
    Console.WriteLine($"Downloading to {outPath}...");
    await using var fs = File.Create(outPath);
    err = await camera.DownloadAsync(handle, fs);
    Console.WriteLine($"Download: {err} ({fs.Length:N0} bytes)");

    // Tell camera we're done
    err = await camera.TransferCompleteAsync(handle);
    Console.WriteLine($"TransferComplete: {err}");
}
catch (OperationCanceledException)
{
    Console.WriteLine("Timeout waiting for image event.");
}

await camera.StopEventPollingAsync();
Console.WriteLine("\nClosing session...");
await camera.CloseSessionAsync();
await camera.DisposeAsync();
Console.WriteLine("Done.");
return 0;
