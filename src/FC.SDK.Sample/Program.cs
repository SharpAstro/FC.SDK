using FC.SDK;
using FC.SDK.Canon;

Console.WriteLine("FC.SDK Sample — ChangeUSBProtocol test");

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

// Try data command BEFORE protocol change
Console.WriteLine("\n--- Before ChangeUSBProtocol ---");
var (isoErr1, isoVal1) = await camera.GetPropertyAsync(EdsPropertyId.ISOSpeed);
Console.WriteLine($"GetProperty ISO: err={isoErr1} val=0x{isoVal1:X}");

// Send ChangeUSBProtocol (0x901F) — no-data command, should work via WPD
Console.WriteLine("\nSending ChangeUSBProtocol (0x901F)...");
var changeResult = await camera.SendRawCommandAsync(0x901F, 1); // param=1 might mean "switch to vendor mode"
Console.WriteLine($"ChangeUSBProtocol: {changeResult}");

// Try data command AFTER protocol change
Console.WriteLine("\n--- After ChangeUSBProtocol ---");
var (isoErr2, isoVal2) = await camera.GetPropertyAsync(EdsPropertyId.ISOSpeed);
Console.WriteLine($"GetProperty ISO: err={isoErr2} val=0x{isoVal2:X}");

var (mluErr, mluSetting) = await camera.GetMirrorUpSettingAsync();
Console.WriteLine($"Mirror lockup: err={mluErr} setting={mluSetting}");

await camera.CloseSessionAsync();
await camera.DisposeAsync();
Console.WriteLine("Done.");
return 0;
