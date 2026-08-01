// FC.SDK sample — connect, read status, stream live view, capture one frame to disk.
//
//   dotnet run --project src/FC.SDK.Sample -- [options]
//
//     --ioctl        talk to the WPD driver through DeviceIoControl instead of the WPD COM API
//     --frames N     live-view frames to pull (default 50, 0 to skip live view)
//     --no-capture   skip the still capture and download
//
// The two transports are alternatives, never a mix: each opens the device for itself, and the
// camera holds one PTP session. --ioctl is the interesting one for live view — see
// CanonCamera.ConnectWpdIoctl.
using System.Diagnostics;
using FC.SDK;
using FC.SDK.Canon;

bool useIoctl = args.Contains("--ioctl");
bool capture = !args.Contains("--no-capture");
int frameCount = ArgValue("--frames") is { } n && int.TryParse(n, out int parsed) ? parsed : 50;

Console.WriteLine($"FC.SDK — Canon Camera Sample ({(useIoctl ? "WPD raw ioctl" : "WPD COM")})");

CanonCamera? camera = null;
if (OperatingSystem.IsWindows())
{
    foreach (var (deviceId, friendlyName) in CanonCamera.EnumerateWpdCameras())
    {
        Console.WriteLine($"Found: {friendlyName}");
        camera = useIoctl ? CanonCamera.ConnectWpdIoctl(deviceId) : CanonCamera.ConnectWpd(deviceId);
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

// Start the event pump before touching properties. On EOS bodies the GetEvent stream is the only
// source of property values, and leaving records queued makes the camera reject property writes.
camera.StartEventPolling(TimeSpan.FromMilliseconds(200));

int exitCode = 0;
try
{
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

    // Worth printing before a capture: a 450D with mirror lockup on answers OK to a remote release
    // and then does nothing at all — no mirror, no exposure, no ObjectAdded — so a capture that
    // times out silently looks like a transport fault when it is a camera setting.
    var (mluErr, mlu) = await camera.GetMirrorLockupStateAsync();
    Console.WriteLine($"Mirror lockup: {(mluErr is EdsError.OK ? mlu.ToString() : $"unreadable ({mluErr})")}");

    // Disable auto power-off so the camera stays awake during long sessions
    var apErr = await camera.SetAutoPowerOffAsync(0);
    if (apErr is EdsError.OK) Console.WriteLine("Auto power-off disabled");

    // Written unconditionally: it is the one artefact worth attaching to a bug report, and asking a
    // reporter to remember a flag is asking for a second round trip.
    var reportPath = Path.Combine(Environment.CurrentDirectory, $"device-report_{DateTime.Now:yyyyMMdd_HHmmss}.md");
    await File.WriteAllTextAsync(reportPath, await camera.CreateDeviceReportAsync());
    Console.WriteLine($"Device report written to {reportPath}");

    if (frameCount > 0 && !await RunLiveViewAsync(camera, frameCount)) exitCode = 1;
    if (capture && !await CaptureAsync(camera)) exitCode = 1;
}
finally
{
    await camera.StopEventPollingAsync();
    Console.WriteLine("\nClosing session...");
    await camera.CloseSessionAsync();
    await camera.DisposeAsync();
    Console.WriteLine("Done.");
}
return exitCode;

/// <summary>
/// Streams live view and reports what actually arrived.
/// </summary>
/// <remarks>
/// Judged on bytes, never on response codes: a body that never left its shooting screen answers OK
/// to every read and hands back nothing, and an iteration count would call that a success. So the
/// bar here is a JPEG SOI marker, a plausible size, and a frame rate that could only come from a
/// running sensor.
/// </remarks>
static async Task<bool> RunLiveViewAsync(CanonCamera camera, int frameCount)
{
    Console.WriteLine($"\nStarting live view ({frameCount} frames)...");
    var err = await camera.StartLiveViewAsync();
    Console.WriteLine($"StartLiveView: {err}");
    if (err is not EdsError.OK) return false;

    int real = 0, notReady = 0, failed = 0, largest = 0, polls = 0;
    byte[] lastFrame = [];

    // Counts frames that carried image data, not poll cycles, and paces only enough to stay off a
    // busy loop. Sleeping a fixed interval per frame would measure the sleep: at 50 ms it puts both
    // transports at the same ~9 fps no matter how fast either one actually is.
    var deadline = Stopwatch.StartNew();
    var timeout = TimeSpan.FromSeconds(30);

    try
    {
        while (real < frameCount && deadline.Elapsed < timeout)
        {
            polls++;
            var (frameErr, jpeg) = await camera.GetLiveViewFrameAsync();

            if (frameErr is not EdsError.OK)
            {
                if (failed++ is 0) Console.WriteLine($"  poll {polls}: {frameErr}");
            }
            else if (jpeg.Length is 0) notReady++;  // body settling, or a read landing between frames
            else if (jpeg is [0xFF, 0xD8, ..])
            {
                real++;
                largest = Math.Max(largest, jpeg.Length);
                lastFrame = jpeg;
            }
            else
            {
                Console.WriteLine($"  poll {polls}: {jpeg.Length} bytes but no JPEG SOI — envelope decode is off");
                failed++;
            }

            await Task.Delay(5);
        }
    }
    finally
    {
        Console.WriteLine($"StopLiveView: {await camera.StopLiveViewAsync()}");
    }

    double seconds = deadline.Elapsed.TotalSeconds;
    Console.WriteLine($"Live view: {real} frames ({real / seconds:F1} fps) over {polls} polls, "
        + $"{notReady} not-ready, {failed} failed, largest {largest:N0} bytes, in {seconds:F1}s");

    if (real is 0)
    {
        Console.WriteLine("No frame ever carried image data — the camera was never actually streaming.");
        return false;
    }

    var path = Path.Combine(Environment.CurrentDirectory, $"liveview_{DateTime.Now:yyyyMMdd_HHmmss}.jpg");
    await File.WriteAllBytesAsync(path, lastFrame);
    Console.WriteLine($"Last frame written to {path}");
    return true;
}

static async Task<bool> CaptureAsync(CanonCamera camera)
{
    var err = await camera.SetSaveToAsync(EdsSaveTo.Host);
    Console.WriteLine($"\nSaveTo=Host: {err}");

    // Wait for ObjectAdded after capture
    var objectTcs = new TaskCompletionSource<uint>();
    camera.ObjectAdded += (_, e) =>
    {
        Console.WriteLine($"  ObjectAdded: handle=0x{e.ObjectHandle:X8}");
        objectTcs.TrySetResult(e.ObjectHandle);
    };

    Console.WriteLine("Taking picture...");
    err = await camera.TakePictureAsync();
    Console.WriteLine($"TakePicture: {err}");
    if (err is not EdsError.OK) return false;

    Console.WriteLine("Waiting for image...");
    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
    try
    {
        var handle = await objectTcs.Task.WaitAsync(cts.Token);

        // Query the camera for the real filename (handles CR2, CR3, JPG, etc.)
        var (_, fileName) = await camera.GetObjectFileNameAsync(handle);
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
        Console.WriteLine($"TransferComplete: {await camera.TransferCompleteAsync(handle)}");
        return err is EdsError.OK && fs.Length > 0;
    }
    catch (OperationCanceledException)
    {
        Console.WriteLine("Timeout waiting for image event.");
        return false;
    }
}

string? ArgValue(string name)
{
    int index = Array.IndexOf(args, name);
    return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
}
