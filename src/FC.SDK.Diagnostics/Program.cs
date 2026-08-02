// Hardware diagnostics harness: sequences a real Canon body through experiments the unit tests
// cannot reach, and judges them on delivered bytes rather than response codes.
//
//   dotnet run --project src/FC.SDK.Diagnostics -- <mode> [options]
//
//     (default)     the mirror-lockup × remote-release matrix   [--settle N] [--only T3,T4]
//     --diag        connect, dump the properties that gate a release, then release / bulb / live view
//     --mlucheck    does a MirrorUpSetting write actually land? (read-back, not the ACK)
//     --mluself     watch 0xD1BF across a self-timer countdown  [--drive 10] [--wait N]
//     --clack       A/B listening test: self-timer with lockup off, then on
//
//   Common: --host (capture to the body's RAM) | default card, --release <hex,hex> to hand back
//   handles a crashed run left the camera holding.
//
// The question: is there ANY remote command sequence that produces an exposure while C.Fn mirror
// lockup is armed? The physical body needs two shutter presses in MLU — one to raise the mirror,
// one to expose — so every sequence here is some way of spelling "two presses" over PTP, including
// the bulb-window variants that have never been tried.
//
// Judged on delivered files, never on response codes: this body ACKs commands it discards. A
// discarded release returns in ~20 ms against ~2.1 s for a real one, so the timings are logged too.
//
// Controls run first AND last with MLU off. A negative result is only worth anything if the same
// harness got a positive one from the same body on the same battery minutes earlier.
using System.Diagnostics;
using System.Runtime.InteropServices;
using FC.SDK;
using FC.SDK.Canon;

// The 450D drops a raised mirror by itself after ~30 s. Each MLU test therefore has to start from a
// known-down mirror, or a "first press" is really someone else's second one.
int settleSeconds = ArgValue("--settle") is { } s && int.TryParse(s, out int parsed) ? parsed : 35;

// The two self-timer drive values to exercise, as hex, taken off the body's own allowed list rather
// than from EdsDriveMode — see the list this prints at startup. Defaults are EDSDK's 2 s and 10 s.
uint SelfTimerDrive = ArgValue("--drive") is { } d1 ? Convert.ToUInt32(d1, 16) : 0x10;
uint SelfTimerDrive2 = ArgValue("--drive2") is { } d2 ? Convert.ToUInt32(d2, 16) : 0x07;
uint SelfTimerDrive3 = ArgValue("--drive3") is { } d3 ? Convert.ToUInt32(d3, 16) : 0x11;
string[]? only = ArgValue("--only")?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

var clock = Stopwatch.StartNew();
var objects = new List<(TimeSpan At, uint Handle)>();
var outDir = Path.Combine(Environment.CurrentDirectory, $"mlu-matrix_{DateTime.Now:yyyyMMdd_HHmmss}");
Directory.CreateDirectory(outDir);

void Log(string line) => Console.WriteLine($"[{clock.Elapsed:mm\\:ss\\.f}] {line}");

CanonCamera? camera = null;
if (OperatingSystem.IsWindows())
{
    foreach (var (deviceId, friendlyName) in CanonCamera.EnumerateWpdCameras())
    {
        Log($"Found: {friendlyName}");
        camera = await CanonCamera.ConnectWpdAutoAsync(deviceId);
        break;
    }
}
if (camera is null) { Log("No camera."); return 1; }

Log($"Transport: {camera.TransportName}");
if (camera.TransportFallbackReason is { } why) Log($"  raw ioctl rejected: {why}");

EdsError err;
try
{
    err = await camera.OpenSessionAsync();
}
catch (COMException ex)
{
    // ERROR_BUSY means another client already owns the device — NINA, EOS Utility, a stuck earlier
    // run. Worth naming, because the stack trace it used to print points at the property-request
    // deep inside session open and reads like a wire-format bug rather than "something else has it".
    Log(ex.HResult == unchecked((int)0x800700AA)
        ? "OpenSession: the camera is held by another application (ERROR_BUSY). Disconnect it there first."
        : $"OpenSession threw {ex.GetType().Name}: {ex.Message}");
    await camera.DisposeAsync();
    return 1;
}
Log($"OpenSession = {err}");
if (err is not EdsError.OK) { await camera.DisposeAsync(); return 1; }

camera.ObjectAdded += (_, e) =>
{
    lock (objects) objects.Add((clock.Elapsed, e.ObjectHandle));
    Log($"  *** ObjectAdded  handle=0x{e.ObjectHandle:X8}");
};
// Verbose in --diag: when a release is refused the only account of why is whatever the body
// volunteers on the event stream, and it volunteers it to nobody in particular.
if (args.Contains("--diag"))
{
    camera.PropertyChanged += (_, e) => Log($"       evt prop {e.PropertyId} = 0x{e.Value:X} ({e.Value})");
    camera.StateChanged += (_, e) => Log($"       evt state {e.EventType} param=0x{e.Param:X}");
}
camera.StartEventPolling(TimeSpan.FromMilliseconds(200));

int failures = 0;
try
{
    Log($"Model: {camera.Model}  Serial: {camera.SerialNumber}  Battery: {camera.BatteryLevelPercent}%");
    Log($"0x9130 ResetMirrorLockupState supported: {camera.SupportedOperations.Contains(0x9130)}");
    Log($"0x9128 RemoteReleaseOn supported:        {camera.SupportedOperations.Contains(0x9128)}");

    // Card, not host. A host-destination frame sits in the body's RAM until someone fetches it,
    // and two un-fetched frames are enough to make the next release answer DeviceBusy — a failure
    // mode with nothing to do with the question being asked, which cost a whole matrix run. On the
    // card the body owns its own storage and every exposure still announces itself as ObjectAddedEx.
    var dest = args.Contains("--host") ? CanonCaptureDestination.Host : CanonCaptureDestination.Card;
    Log($"CaptureDestination={dest} = {await camera.SetCaptureDestinationAsync(dest)}");

    // Anything a previous (or crashed) run left un-fetched keeps the body busy. Handles are given
    // on the command line because a dead process took its own list to the grave.
    foreach (var h in (ArgValue("--release") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries))
    {
        var handle = Convert.ToUInt32(h.Trim(), 16);
        Log($"TransferComplete(0x{handle:X8}) = {await camera.TransferCompleteAsync(handle)}");
    }
    Log($"AutoPowerOff=off = {await camera.SetAutoPowerOffAsync(0)}");
    // Self-timer drive was left set by an earlier session and adds a delay to every release; the
    // matrix wants the plainest possible release.
    Log($"DriveMode=Single = {await camera.SetDriveModeAsync(EdsDriveMode.SingleShooting)}");

    var (aeErr, ae) = await camera.GetAEModeAsync();
    var (tvErr, tv) = await camera.GetShutterSpeedAsync();
    Log($"AEMode = {ae} ({aeErr})   Tv = {tv} ({tvErr})");

    // Printed every run, because EdsDriveMode's numbering is EDSDK's and not necessarily this body's:
    // a 450D was found sitting on 0x11, which the enum does not contain at all. Self-timer tests take
    // their value from --drive, chosen off this list rather than guessed from the enum.
    var driveValues = await camera.GetAllowedValuesAsync(EdsPropertyId.DriveMode);
    Log("DriveMode allowed: " + (driveValues is null
        ? "none announced"
        : string.Join(", ", driveValues.Select(v => $"0x{v:X}({(EdsDriveMode)v})"))));

    if (args.Contains("--clack"))
    {
        // Counting clacks does not work — an ordinary release is already mirror-up/shutter/mirror-down
        // inside ~100 ms. What a person CAN judge is whether anything happened at the instant of the
        // release, ten seconds before the exposure. Lockup engaged puts a mirror flip there; lockup
        // ignored leaves it silent and does everything at the end. Run with lockup off first so the
        // "silent at T+0" case is heard before the one being tested.
        // The two rounds are timing-identical — image at T+11s either way — so the log as it stood
        // could not tell them apart and the whole test rested on hearing one clack. Recording every
        // event record against its offset from the release makes the comparison objective: if the
        // body does something extra at T+0 with lockup armed, it shows up as a record the other
        // round does not have.
        var sinceFire = Stopwatch.StartNew();
        var round = "setup";
        var timeline = new List<string>();
        void Note(string what)
        {
            var line = $"[{round}] +{sinceFire.Elapsed.TotalSeconds,5:F1}s {what}";
            lock (timeline) timeline.Add(line);
            Log("    " + line);
        }
        camera.PropertyChanged += (_, e) => Note($"prop {e.PropertyId} = 0x{e.Value:X}");
        camera.StateChanged += (_, e) => Note($"state {e.EventType} = 0x{e.Param:X}");

        foreach (var mlu in new[] { EdsMirrorUpSetting.Off, EdsMirrorUpSetting.On })
        {
            Console.WriteLine();
            Log($"########## MIRROR LOCKUP {mlu.ToString().ToUpperInvariant()} ##########");
            Log($"  set = {await camera.SetMirrorLockupAsync(mlu)}");
            await Task.Delay(1500);
            await camera.DrainEventsAsync();
            var (_, setting) = await camera.GetRawPropertyAsync(0xD13A);
            Log($"  0xD13A reads {setting} — {(setting == (uint)mlu ? "confirmed" : "MISMATCH, result is void")}");
            Log($"  drive 0x{SelfTimerDrive:X} = {await camera.SetRawPropertyAsync(0xD106, SelfTimerDrive)}");
            await camera.DrainEventsAsync();

            // Beeped, because this harness's stdout is not on the listener's screen — it goes to a
            // tool result — so a printed "FIRED NOW" reaches them late and out of band. The beeps
            // come from the machine next to the camera and stop one second BEFORE the release, so
            // the cue never masks the sound being listened for: three beeps, one second of silence,
            // then the shutter command. Anything the camera does, it does after that silence.
            for (int i = 12; i > 0; i--)
            {
                if (i <= 5 || i % 5 == 0) Log($"  ... firing in {i}s");
                if (i is 4 or 3 or 2 && OperatingSystem.IsWindows()) Console.Beep(1200, 120);
                await Task.Delay(i is 4 or 3 or 2 ? 880 : 1000);
            }

            lock (objects) objects.Clear();
            round = mlu.ToString();
            sinceFire.Restart();
            var sw = Stopwatch.StartNew();
            var e = await camera.TakePictureAsync();
            Log($"  >>>>> FIRED NOW <<<<<  ({e}) — one second after the last beep");
            for (int i = 1; i <= 12; i++)
            {
                await Task.Delay(1000);
                bool got; lock (objects) got = objects.Count > 0;
                Log($"  T+{i,2}s{(got ? "   <<< image arrived — the exposure was HERE" : "")}");
                if (got) break;
            }
            Log($"  {mlu}: {await CollectAsync($"CLACK-{mlu}", TimeSpan.FromSeconds(10))} after {sw.Elapsed.TotalSeconds:F1}s");
            round = "between rounds";
        }

        // The whole point: the two rounds were previously indistinguishable in the log, so print
        // them side by side and let a difference — or the absence of one — be the finding.
        Console.WriteLine();
        Log("======== EVENT TIMELINE, BY ROUND (offsets are from the release) ========");
        string[] snapshot;
        lock (timeline) snapshot = [.. timeline];
        foreach (var tag in new[] { "Off", "On" })
        {
            Log($"--- lockup {tag} ---");
            var rows = snapshot.Where(l => l.StartsWith($"[{tag}]", StringComparison.Ordinal)).ToArray();
            if (rows.Length is 0) Log("  (no event records at all in this round)");
            foreach (var row in rows) Log("  " + row);
        }
        return 0;
    }

    if (args.Contains("--mluself"))
    {
        // The measurement nobody has taken: with MLU armed and a self-timer running, does the mirror
        // actually go up during the countdown? A delivered file cannot answer it — the 6D delivers a
        // file either way — and the mirror is back down by the time anyone looks afterwards. So watch
        // 0xD1BF continuously across the whole countdown instead of sampling it at the ends.
        const ushort MirrorLockUpState = 0xD1BF;
        const ushort MirrorUpSetting = 0xD13A;

        var drive = ArgValue("--drive") is { } d ? Convert.ToUInt32(d, 16) : 0x10u;
        Log($"MLU -> On = {await camera.SetMirrorLockupAsync(EdsMirrorUpSetting.On)}");
        await Task.Delay(1500);
        await camera.DrainEventsAsync();
        var (_, mluSetting) = await camera.GetRawPropertyAsync(MirrorUpSetting);
        Log($"0xD13A reads {mluSetting} — MLU is {(mluSetting == 1 ? "CONFIRMED ON" : "NOT ON, stop here")}");
        if (mluSetting is not 1) return 1;

        Log($"DriveMode -> 0x{drive:X} = {await camera.SetRawPropertyAsync(0xD106, drive)}");
        await camera.DrainEventsAsync();
        var (_, driveNow) = await camera.GetPropertyAsync(EdsPropertyId.DriveMode);
        Log($"DriveMode reads 0x{driveNow:X}");

        lock (objects) objects.Clear();
        camera.PropertyChanged += (_, e) =>
        {
            if (e.PropertyId is EdsPropertyId.MirrorLockUpState)
                Log($"  !! 0xD1BF event -> 0x{e.Value:X}");
        };

        // Lead-in so a human can be listening at the right moment. The mirror is the instrument here:
        // 0xD1BF does not report live position on this body, so two distinct clacks (mirror, then
        // shutter) versus one is the only available evidence that lockup engaged.
        if (ArgValue("--wait") is { } w && int.TryParse(w, out int lead))
        {
            for (int i = lead; i > 0; i--)
            {
                if (i <= 5 || i % 5 == 0) Log($"  ... firing in {i}s — LISTEN");
                await Task.Delay(1000);
            }
        }

        var sw = Stopwatch.StartNew();
        Log($"release = {await camera.TakePictureAsync()}  ({sw.ElapsedMilliseconds} ms)");
        uint last = uint.MaxValue;
        while (sw.Elapsed < TimeSpan.FromSeconds(20))
        {
            var (e, v) = await camera.GetRawPropertyAsync(MirrorLockUpState);
            if (e is EdsError.OK && v != last)
            {
                Log($"  [{sw.Elapsed.TotalSeconds,5:F1}s] 0xD1BF = 0x{v:X}{(v != 0 ? "   <<< MIRROR UP" : "")}");
                last = v;
            }
            lock (objects) { if (objects.Count > 0) break; }
            await Task.Delay(150);
        }
        Log($"image after {sw.Elapsed.TotalSeconds:F1}s: {await CollectAsync("SELF", TimeSpan.FromSeconds(20))}");
        return 0;
    }

    if (args.Contains("--mlucheck"))
    {
        // Does a MirrorUpSetting write actually land? SetMirrorLockupAsync trusts the response code
        // on the property path, and this body ACKs the write while shooting straight through it.
        const ushort MirrorUpSetting = 0xD13A;
        const ushort MirrorLockUpState = 0xD1BF;
        var allowed = await camera.GetAllowedValuesAsync(EdsPropertyId.MirrorUpSetting);
        Log("0xD13A allowed values: " + (allowed is null ? "none announced" : string.Join(',', allowed)));

        foreach (var want in new[] { EdsMirrorUpSetting.On, EdsMirrorUpSetting.Off })
        {
            var (beforeErr, before) = await camera.GetRawPropertyAsync(MirrorUpSetting);
            var setErr = await camera.SetMirrorLockupAsync(want);
            await Task.Delay(2500);
            await camera.DrainEventsAsync();
            var (afterErr, after) = await camera.GetRawPropertyAsync(MirrorUpSetting);
            var (stateErr, state) = await camera.GetRawPropertyAsync(MirrorLockUpState);
            Log($"  set {want}: {setErr} | 0xD13A {before}({beforeErr}) -> {after}({afterErr}) "
                + $"| 0xD1BF {state}({stateErr}) | SDK claims {camera.MirrorLockupEnabled}");
            Log($"  => the write {(after == (uint)want ? "LANDED" : "DID NOT LAND — the ACK was empty")}");
        }
        return 0;
    }

    if (args.Contains("--diag"))
    {
        // Everything the body will admit about why it might refuse to release, before asking it to.
        foreach (var id in new[]
                 {
                     EdsPropertyId.BatteryLevel, EdsPropertyId.AvailableShots, EdsPropertyId.AFMode,
                     EdsPropertyId.DriveMode, EdsPropertyId.Av, EdsPropertyId.ISOSpeed,
                     EdsPropertyId.SaveTo, EdsPropertyId.ImageQuality,
                 })
        {
            var (e, v) = await camera.GetPropertyAsync(id);
            Log($"  {id,-16} = {(e is EdsError.OK ? $"0x{v:X} ({v})" : e.ToString())}");
        }

        Log("\n--- diag 1: MLU off, Tv 1/60, single 0x910F, watch the stream for 15 s");
        await SetMlu(false);
        await SetTv(EdsTv.Tv_1_60);
        lock (objects) objects.Clear();
        await camera.DrainEventsAsync();
        await Release("0x910F");
        Log($"  result: {await CollectAsync("D1", TimeSpan.FromSeconds(15))}");

        Log("\n--- diag 2: MLU off, bulb 3 s — the path that worked last session");
        await SetTv(EdsTv.Bulb);
        lock (objects) objects.Clear();
        await camera.DrainEventsAsync();
        await BulbStart();
        await Wait(3);
        await BulbEnd();
        Log($"  result: {await CollectAsync("D2", TimeSpan.FromSeconds(15))}");

        Log("\n--- diag 3: MLU off, live view vitality probe (mirror should flip audibly)");
        Log($"  StartLiveView = {await camera.StartLiveViewAsync()}");
        int real = 0;
        for (int i = 0; i < 400 && real < 10; i++)
        {
            var (fe, jpeg) = await camera.GetLiveViewFrameAsync();
            if (fe is EdsError.OK && jpeg is [0xFF, 0xD8, ..]) real++;
            await Task.Delay(25);
        }
        Log($"  live-view frames with real JPEG data: {real}");
        Log($"  StopLiveView = {await camera.StopLiveViewAsync()}");
        return 0;
    }

    var tests = new (string Id, string What, Func<Task> Run)[]
    {
        ("C1", "CONTROL, MLU off: single 0x910F at 1/60 — must deliver a file",
            async () => { await SetTv(EdsTv.Tv_1_60); await Release("0x910F"); }),

        ("T1", "MLU on: single 0x910F at 1/60 (baseline discard)",
            async () => { await SetTv(EdsTv.Tv_1_60); await Release("0x910F"); }),

        ("T2", "MLU on: 0x910F, wait 3s, 0x910F — the two-press analogue",
            async () => { await SetTv(EdsTv.Tv_1_60); await Release("0x910F #1"); await Wait(3); await Release("0x910F #2"); }),

        ("T3", "MLU on: BulbStart, 1s, 0x910F, 4s, BulbEnd — release inside the bulb window",
            async () => { await SetTv(EdsTv.Bulb); await BulbStart(); await Wait(1); await Release("0x910F"); await Wait(4); await BulbEnd(); }),

        ("T4", "MLU on: 0x910F, 2s, BulbStart, 4s, BulbEnd — release raises, bulb exposes",
            async () => { await SetTv(EdsTv.Bulb); await Release("0x910F"); await Wait(2); await BulbStart(); await Wait(4); await BulbEnd(); }),

        ("T5", "MLU on: BulbStart, 2s, BulbStart, 4s, BulbEnd — bulb start twice",
            async () => { await SetTv(EdsTv.Bulb); await BulbStart(); await Wait(2); await BulbStart(); await Wait(4); await BulbEnd(); }),

        ("T6", "MLU on: BulbStart, 4s, BulbEnd, 1.5s, 0x910F — bulb arms, release fires",
            async () => { await SetTv(EdsTv.Bulb); await BulbStart(); await Wait(4); await BulbEnd(); await Wait(1.5); await Release("0x910F"); }),

        ("T7", "MLU on: release, 1s, 0x9130 ResetMirrorLockupState, 1s, release",
            async () => { await SetTv(EdsTv.Tv_1_60); await Release("release #1"); await Wait(1); await Step("0x9130 reset MLU state", () => camera.ResetMirrorLockupStateAsync()); await Wait(1); await Release("release #2"); }),

        // Only a body with the real 0xD1BF property can answer this, and it is the measurement the
        // 450D could never give: did the first press actually raise the mirror, or was it discarded?
        // "Nothing was delivered" cannot tell those apart; a mirror reported Up between the presses
        // would mean MLU remote capture is a sequencing problem rather than a refusal.
        ("T8", "MLU on: release, read 0xD1BF mirror state, 2s, release, read again",
            async () =>
            {
                await SetTv(EdsTv.Tv_1_60);
                await Release("release #1");
                await MirrorState("after press 1");
                await Wait(2);
                await Release("release #2");
                await MirrorState("after press 2");
            }),

        ("C2", "CONTROL, MLU off: BulbStart, 3s, BulbEnd — must deliver a file",
            async () => { await SetTv(EdsTv.Bulb); await BulbStart(); await Wait(3); await BulbEnd(); }),

        // The manual's own MLU workaround for the physical shutter button: in self-timer the body
        // raises the mirror, waits out the countdown, then exposes — one press, no second press. If
        // any drive mode makes remote MLU work, it is this one.
        ("T9", $"MLU on + self-timer drive 0x{SelfTimerDrive:X}: single release",
            async () => { await SetDrive(SelfTimerDrive); await SetTv(EdsTv.Tv_1_60); await Release("release"); }),

        ("T10", $"MLU on + self-timer drive 0x{SelfTimerDrive2:X}: single release",
            async () => { await SetDrive(SelfTimerDrive2); await SetTv(EdsTv.Tv_1_60); await Release("release"); }),

        ("T11", $"MLU on + self-timer drive 0x{SelfTimerDrive3:X}: single release",
            async () => { await SetDrive(SelfTimerDrive3); await SetTv(EdsTv.Tv_1_60); await Release("release"); }),

        // Self-timer plus a second press. If the timer raises the mirror and then waits for a
        // confirming press rather than exposing on its own, this is the combination that finds it.
        ("T12", $"MLU on + self-timer drive 0x{SelfTimerDrive:X}: release, 5s, release",
            async () => { await SetDrive(SelfTimerDrive); await SetTv(EdsTv.Tv_1_60); await Release("release #1"); await Wait(5); await Release("release #2"); }),

        ("C3", "CONTROL, MLU off: single release at 1/60 — must deliver a file",
            async () => { await SetDrive(0x00); await SetTv(EdsTv.Tv_1_60); await Release("release"); }),

        // The control that decides whether the T9/T10/T11 rows mean anything. If a self-timer drive
        // does not deliver with lockup OFF, then the body does not honour self-timer on a PTP release
        // at all, and those rows are measuring the drive mode rather than mirror lockup.
        ("C4", $"CONTROL, MLU off + self-timer drive 0x{SelfTimerDrive:X}: does the timer fire remotely at all?",
            async () => { await SetDrive(SelfTimerDrive); await SetTv(EdsTv.Tv_1_60); await Release("release"); }),

        ("C5", $"CONTROL, MLU off + self-timer drive 0x{SelfTimerDrive2:X}: does the timer fire remotely at all?",
            async () => { await SetDrive(SelfTimerDrive2); await SetTv(EdsTv.Tv_1_60); await Release("release"); }),

        ("C6", $"CONTROL, MLU off + self-timer drive 0x{SelfTimerDrive3:X}: does the timer fire remotely at all?",
            async () => { await SetDrive(SelfTimerDrive3); await SetTv(EdsTv.Tv_1_60); await Release("release"); }),
    };

    var results = new List<(string Id, string What, bool Mlu, bool Delivered, long Bytes)>();

    foreach (var (id, what, run) in tests)
    {
        if (only is not null && !only.Contains(id, StringComparer.OrdinalIgnoreCase)) continue;
        if (id is "T7" && !camera.SupportedOperations.Contains(0x9130))
        {
            Log($"\n===== {id} SKIPPED — body does not advertise 0x9130 =====");
            continue;
        }

        bool wantMlu = id[0] is 'T';
        Console.WriteLine();
        Log($"===== {id}  {what}");

        if (!await SetMlu(wantMlu)) { failures++; results.Add((id, what, wantMlu, false, 0)); continue; }

        lock (objects) objects.Clear();
        await camera.DrainEventsAsync();
        await run();

        var (delivered, bytes) = await CollectAsync(id, TimeSpan.FromSeconds(15));
        results.Add((id, what, wantMlu, delivered, bytes));
        Log($"===== {id} {(delivered ? $"EXPOSED — {bytes:N0} bytes" : "nothing delivered")}");

        // Long enough for a raised mirror to drop on its own, so the next test starts from a known
        // state rather than inheriting this one's.
        if (wantMlu && !delivered) await Wait(settleSeconds, "settle (let any raised mirror drop)");
    }

    Console.WriteLine();
    Log("================ SUMMARY ================");
    foreach (var (id, what, mlu, delivered, bytes) in results)
        Log($"{id,-3} MLU={(mlu ? "on " : "off")}  {(delivered ? $"EXPOSED {bytes,10:N0} b" : "nothing        ")}  {what}");

    var controls = results.Where(r => r.Id[0] is 'C').ToList();
    if (controls.Count > 0 && controls.All(c => !c.Delivered))
    {
        Log("!! Both controls failed — the body was not exposing at all, so every MLU result here is void.");
        failures++;
    }
}
finally
{
    Log($"\nRestoring: MLU off = {await camera.SetMirrorLockupAsync(EdsMirrorUpSetting.Off)}");
    Log($"Restoring: Tv = 1/60 = {await camera.SetShutterSpeedAsync(EdsTv.Tv_1_60)}");
    await camera.StopEventPollingAsync();
    Log($"CloseSession = {await camera.CloseSessionAsync()}");
    await camera.DisposeAsync();
    Log($"Artefacts in {outDir}");
}
return failures;

async Task<bool> SetMlu(bool on)
{
    var want = on ? EdsMirrorUpSetting.On : EdsMirrorUpSetting.Off;
    var e = await camera!.SetMirrorLockupAsync(want);
    // SetMirrorLockupAsync verifies with a fresh C.Fn read-back and answers OperationRefused when
    // the body kept its old value, so this is a real check and not an echoed ACK.
    Log($"  MLU -> {want} = {e}   (camera reports {camera.MirrorLockupEnabled?.ToString() ?? "unknown"})");
    if (e is EdsError.OK) return true;
    Log("  !! could not establish the mirror-lockup state — skipping this test rather than guessing");
    return false;
}

async Task SetTv(EdsTv tv)
{
    var e = await camera!.SetShutterSpeedAsync(tv);
    var (_, now) = await camera.GetShutterSpeedAsync();
    Log($"  Tv -> {tv} = {e}   (camera reports {now})");
}

// Named for what actually goes on the wire: TakePictureAsync sends the 0x9128/0x9129 press pair on
// bodies that have it and the single-shot 0x910F on those that do not, and a log that says "0x910F"
// on a 6D is a log that will mislead whoever reads it next.
Task<EdsError> Release(string label) =>
    Step($"{label} ({(camera!.SupportedOperations.Contains(0x9128) ? "0x9128/9" : "0x910F")})",
        () => camera.TakePictureAsync());

// Raw, not EdsDriveMode: the value has to come from the body's own allowed list (printed at
// startup), and a 450D offers one the enum does not name.
async Task SetDrive(uint mode)
{
    var e = await camera!.SetRawPropertyAsync(0xD106, mode);
    await camera.DrainEventsAsync();
    var (_, now) = await camera.GetPropertyAsync(EdsPropertyId.DriveMode);
    Log($"  DriveMode -> 0x{mode:X} = {e}   (camera reports 0x{now:X}"
        + $"{(now == mode ? "" : " — DID NOT TAKE, this row is void")})");
}

// Only meaningful where 0xD1BF exists; elsewhere GetMirrorLockupStateAsync derives Enable/Disable
// from the setting and says nothing about the mirror, so the log marks which kind of answer it is.
async Task MirrorState(string when)
{
    var (e, state) = await camera!.GetPropertyAsync(EdsPropertyId.MirrorLockUpState);
    Log(e is EdsError.OK
        ? $"  mirror state {when}: 0x{state:X} (0xD1BF, real)"
        : $"  mirror state {when}: unavailable ({e}) — body has no 0xD1BF, position unknowable");
}
Task<EdsError> BulbStart() => Step("0x9125 BulbStart", () => camera!.BulbStartAsync());
Task<EdsError> BulbEnd() => Step("0x9126 BulbEnd", () => camera!.BulbEndAsync());

async Task<EdsError> Step(string label, Func<Task<EdsError>> op)
{
    var sw = Stopwatch.StartNew();
    var e = await op();
    sw.Stop();
    // The timing is the tell: a discarded release comes back in ~20 ms, a real release sequence
    // occupies the body for ~2.1 s.
    Log($"  {label,-24} = {e,-16} {sw.ElapsedMilliseconds,6} ms");
    return e;
}

async Task Wait(double seconds, string? label = null)
{
    if (label is not null) Log($"  ... {label}, {seconds:0.#}s");
    await Task.Delay(TimeSpan.FromSeconds(seconds));
}

// Waits out the window, then downloads whatever arrived. Returns the largest file, because a
// sequence that fires twice is exactly the thing worth noticing.
async Task<(bool Delivered, long Bytes)> CollectAsync(string id, TimeSpan window)
{
    var deadline = Stopwatch.StartNew();
    while (deadline.Elapsed < window)
    {
        lock (objects) { if (objects.Count > 0) break; }
        await Task.Delay(100);
    }
    // Something arrived — give a second frame a chance to show up before moving on.
    lock (objects) { if (objects.Count > 0) { } }
    await Task.Delay(1500);

    (TimeSpan At, uint Handle)[] found;
    lock (objects) found = [.. objects];
    if (found.Length is 0) return (false, 0);

    long largest = 0;
    foreach (var (at, handle) in found)
    {
        var (_, name) = await camera!.GetObjectFileNameAsync(handle);
        var ext = Path.GetExtension(name) is { Length: > 0 } x ? x : ".cr2";
        var path = Path.Combine(outDir, $"{id}_{at:mm\\-ss}_{handle:X8}{ext}");
        await using (var fs = File.Create(path))
        {
            var e = await camera.DownloadAsync(handle, fs);
            Log($"  downloaded {Path.GetFileName(path)}: {e}, {fs.Length:N0} bytes  (camera name {name ?? "?"})");
            largest = Math.Max(largest, fs.Length);
        }
        await camera.TransferCompleteAsync(handle);
    }
    return (true, largest);
}

string? ArgValue(string name)
{
    int i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}
