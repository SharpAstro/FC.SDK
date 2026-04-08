using FC.SDK;
using FC.SDK.Canon;

Console.WriteLine("FC.SDK Sample — Canon EOS 6D via WPD");
Console.WriteLine();

string? wpdDeviceId = null;

if (OperatingSystem.IsWindows())
{
    foreach (var (deviceId, friendlyName) in CanonCamera.EnumerateWpdCameras())
    {
        Console.WriteLine($"Found: {friendlyName}");
        wpdDeviceId = deviceId;
        break;
    }
}

if (wpdDeviceId is null) { Console.WriteLine("No camera."); return 1; }

// Session 1: take picture
var camera = CanonCamera.ConnectWpd(wpdDeviceId);
var result = await camera.OpenSessionAsync();
if (result is not EdsError.OK) { Console.WriteLine($"OpenSession: {result}"); return 1; }
Console.WriteLine("Connected!");

Console.WriteLine("Taking picture...");
var takeResult = await camera.TakePictureAsync();
Console.WriteLine($"TakePicture: {takeResult}");

// Close session (camera writes to card)
await camera.CloseSessionAsync();
await camera.DisposeAsync();
Console.WriteLine("Session closed. Waiting for card write...");
await Task.Delay(3000);

// Session 2: enumerate + download
camera = CanonCamera.ConnectWpd(wpdDeviceId);
result = await camera.OpenSessionNoRemoteModeAsync();
if (result is not EdsError.OK) { Console.WriteLine($"Reopen: {result}"); return 1; }

var objects = camera.EnumerateWpdObjects();
Console.WriteLine($"Objects: {objects.Count}");
if (objects.Count > 0)
{
    var (objId, fileName) = objects[^1];
    Console.WriteLine($"Latest: {fileName}");

    var outputPath = Path.Combine(Environment.CurrentDirectory, fileName ?? "photo.cr2");
    Console.WriteLine($"Downloading...");
    await using (var fs = new FileStream(outputPath, FileMode.Create, FileAccess.Write))
    {
        await camera.DownloadWpdObjectAsync(objId, fs);
    }
    Console.WriteLine($"Saved: {outputPath} ({new FileInfo(outputPath).Length:N0} bytes)");
}

await camera.CloseSessionAsync();
await camera.DisposeAsync();
Console.WriteLine("Done.");
return 0;
