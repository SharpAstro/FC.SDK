using FC.SDK;
using FC.SDK.Canon;

Console.WriteLine("FC.SDK Sample — Canon EOS 6D via WPD");
Console.WriteLine();

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

var result = await camera.OpenSessionAsync();
if (result is not EdsError.OK) { Console.WriteLine($"OpenSession: {result}"); return 1; }
Console.WriteLine("Connected!");

// Test property access (previously failed with 0x80070490)
var (isoErr, isoVal) = await camera.GetPropertyAsync(EdsPropertyId.ISOSpeed);
Console.WriteLine($"GetProperty ISO: err={isoErr} val=0x{isoVal:X4}");

var setResult = await camera.SetPropertyAsync(EdsPropertyId.ISOSpeed, 0x00000060);
Console.WriteLine($"SetProperty ISO 800: {setResult}");

var (mluErr, mluSetting) = await camera.GetMirrorUpSettingAsync();
Console.WriteLine($"Mirror lockup: err={mluErr} setting={mluSetting}");

// Set up PTP event polling (previously failed)
camera.ObjectAdded += (_, e) => Console.WriteLine($"*** PTP ObjectAdded: handle={e.ObjectHandle} ***");
camera.StartEventPolling();

// Take picture
Console.WriteLine("Taking picture...");
var takeResult = await camera.TakePictureAsync();
Console.WriteLine($"TakePicture: {takeResult}");

// Wait for events
Console.WriteLine("Waiting 10s for PTP events...");
await Task.Delay(10000);

await camera.StopEventPollingAsync();
await camera.CloseSessionAsync();
await camera.DisposeAsync();
Console.WriteLine("Done.");
return 0;
