using FC.SDK;
using FC.SDK.Canon;

Console.WriteLine("FC.SDK — TakePicture → GetEvent → Download test");

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
Console.WriteLine("Connected with remote mode!");

// Drain initial events
Console.WriteLine("\nGetEvent (initial drain):");
var evt1 = await camera.TestVendorDataReadAsync(0x9116);
Console.WriteLine($"  {evt1}");

// Take picture
Console.WriteLine("\nTaking picture...");
var takeResult = await camera.TakePictureAsync();
Console.WriteLine($"TakePicture: {takeResult}");

if (takeResult is EdsError.OK)
{
    // Wait for camera to process
    Console.WriteLine("Waiting 3s for camera...");
    await Task.Delay(3000);

    // Try GetEvent — should now have ObjectAdded
    Console.WriteLine("\nGetEvent (after TakePicture):");
    var evt2 = await camera.TestVendorDataReadAsync(0x9116);
    Console.WriteLine($"  {evt2}");

    // Try multiple times
    for (int i = 0; i < 5; i++)
    {
        await Task.Delay(1000);
        var evt = await camera.TestVendorDataReadAsync(0x9116);
        Console.WriteLine($"  Poll {i+1}: {evt}");
        if (evt.Contains("dataLen=") && !evt.Contains("dataLen=0"))
        {
            Console.WriteLine("  *** GOT EVENT DATA! ***");
            break;
        }
    }
}

await camera.CloseSessionAsync();
await camera.DisposeAsync();
Console.WriteLine("Done.");
return 0;
