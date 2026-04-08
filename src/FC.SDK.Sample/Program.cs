using FC.SDK;
using FC.SDK.Canon;

Console.WriteLine("FC.SDK — WPD_COMMAND_MTP_EXT_GET_SUPPORTED_VENDOR_OPCODES test");

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

await camera.ConnectTransportAsync();
Console.WriteLine("Connected (no PTP session)");

// Query supported vendor opcodes — PID 11
Console.WriteLine("\nGET_SUPPORTED_VENDOR_OPCODES (pid=11):");
Console.WriteLine(camera.TestWpdMtpExtCommand(11));

// Query vendor extension description — PID 18
Console.WriteLine("\nGET_VENDOR_EXTENSION_DESCRIPTION (pid=18):");
Console.WriteLine(camera.TestWpdMtpExtCommand(18));

// Also test: standard PTP GetDevicePropValue for various props
Console.WriteLine("\n--- Standard PTP properties ---");
(ushort code, string name)[] props = [
    (0x5001, "BatteryLevel"),
    (0x5003, "ImageSize"),
    (0xD101, "Canon Aperture"),
    (0xD102, "Canon ShutterSpeed"),
    (0xD103, "Canon ISO"),
    (0xD104, "Canon ExpComp"),
];
foreach (var (code, name) in props)
{
    var r = await camera.TestStandardDataReadAsync(0x1015, code);
    Console.WriteLine($"  {name} (0x{code:X4}): {r}");
}

await camera.DisposeAsync();
Console.WriteLine("Done.");
return 0;
