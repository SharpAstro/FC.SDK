using FC.SDK;
using FC.SDK.Canon;

Console.WriteLine("FC.SDK — WPD vendor data-phase test");

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

// Test 1: WITHOUT our PTP session (WPD manages its own session internally)
Console.WriteLine("\n=== Test 1: WPD-managed session (no OpenSession) ===");
// Just connect the transport, don't open Canon session
await camera.ConnectTransportAsync();

Console.WriteLine("GetEvent (0x9116) data-read:");
var evtResult = await camera.TestVendorDataReadAsync(0x9116);
Console.WriteLine($"  {evtResult}");

Console.WriteLine("GetDevicePropValue(BatteryLevel 0x5001):");
var batResult = await camera.TestStandardDataReadAsync(0x1015, 0x5001);
Console.WriteLine($"  {batResult}");

Console.WriteLine("GetDevicePropValue(Canon ISO 0xD103):");
var isoResult = await camera.TestStandardDataReadAsync(0x1015, 0xD103);
Console.WriteLine($"  {isoResult}");

// Test 2: WITH our PTP session (OpenSession + SetRemoteMode)
Console.WriteLine("\n=== Test 2: Canon remote mode session ===");
var result = await camera.OpenSessionAsync();
Console.WriteLine($"OpenSession: {result}");

Console.WriteLine("GetEvent (0x9116) data-read:");
evtResult = await camera.TestVendorDataReadAsync(0x9116);
Console.WriteLine($"  {evtResult}");

Console.WriteLine("GetProperty ISO via Canon path:");
var (isoErr, isoVal) = await camera.GetPropertyAsync(EdsPropertyId.ISOSpeed);
Console.WriteLine($"  err={isoErr} val=0x{isoVal:X}");

// Shutter still works
Console.WriteLine("\nTakePicture:");
var takeResult = await camera.TakePictureAsync();
Console.WriteLine($"  {takeResult}");

await camera.CloseSessionAsync();
await camera.DisposeAsync();
Console.WriteLine("\nDone.");
return 0;
