// Hardware diagnostics harness: sequences a real Canon body through experiments the unit tests
// cannot reach, and judges them on delivered bytes rather than response codes.
//
//   dotnet run --project src/FC.SDK.Diagnostics -- <command> [options]
//   ...                                          -- --help
//
// Every experiment is a subcommand; `--help` lists them with their options. This used to be
// `args.Contains("--evf")` and friends, which had a nasty property for a harness that drives real
// hardware: an unrecognised argument was not an error, it silently selected the DEFAULT mode. A
// mistyped `--evff` therefore ran the mirror-lockup matrix — minutes long, firing the shutter
// repeatedly — instead of reading some properties. System.CommandLine rejects it instead.
//
// The original question, which the matrix answers: is there ANY remote command sequence that
// produces an exposure while C.Fn mirror lockup is armed? The physical body needs two shutter
// presses in MLU — one to raise the mirror, one to expose — so every sequence there is some way of
// spelling "two presses" over PTP, including a release inside an open bulb window.
//
// Judged on delivered files, never on response codes: these bodies ACK commands they discard. A
// discarded release returns in ~20 ms against ~2.1 s for a real one, so the timings are logged too.
//
// Controls run first AND last with MLU off. A negative result is only worth anything if the same
// harness got a positive one from the same body on the same battery minutes earlier.
using System.CommandLine;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using FC.SDK;
using FC.SDK.Canon;

// Hex throughout, because every drive-mode and property value in this domain is quoted in hex —
// and taken off the body's own allowed-value list, never from EdsDriveMode, whose numbering is
// EDSDK's rather than any particular body's.
static uint ParseHex(System.CommandLine.Parsing.ArgumentResult r) =>
    uint.Parse(r.Tokens[0].Value.TrimStart('0', 'x', 'X') is { Length: > 0 } t ? t : "0",
        NumberStyles.HexNumber, CultureInfo.InvariantCulture);

// The 450D drops a raised mirror by itself after ~30 s. Each MLU test therefore has to start from a
// known-down mirror, or a "first press" is really someone else's second one.
var settleOpt = new Option<int>("--settle") { Description = "Seconds to let a raised mirror drop between tests.", DefaultValueFactory = _ => 35 };
var onlyOpt = new Option<string[]>("--only") { Description = "Run only these matrix rows, e.g. T3 T4.", AllowMultipleArgumentsPerToken = true };
var driveOpt = new Option<uint>("--drive") { Description = "Self-timer drive value (hex).", CustomParser = ParseHex, DefaultValueFactory = _ => 0x10 };
var drive2Opt = new Option<uint>("--drive2") { Description = "Second self-timer drive value (hex).", CustomParser = ParseHex, DefaultValueFactory = _ => 0x07 };
var drive3Opt = new Option<uint>("--drive3") { Description = "Third self-timer drive value (hex).", CustomParser = ParseHex, DefaultValueFactory = _ => 0x11 };
var waitOpt = new Option<int>("--wait") { Description = "Seconds to watch before the release.", DefaultValueFactory = _ => 0 };

// Card by default and deliberately: a host-destination frame sits in the body's RAM until someone
// fetches it, and two un-fetched frames are enough to wedge a 450D into DeviceBusy.
var hostOpt = new Option<bool>("--host") { Description = "Capture into the body's RAM instead of to the card." };
var releaseOpt = new Option<uint[]>("--release") { Description = "Object handles (hex) a crashed run left the camera holding.", AllowMultipleArgumentsPerToken = true, CustomParser = r => [.. r.Tokens.Select(t => uint.Parse(t.Value.TrimStart('0', 'x', 'X'), NumberStyles.HexNumber, CultureInfo.InvariantCulture))] };

// Declaring the cap is on is a claim about the physical world that the harness cannot check, so it
// has to be asserted deliberately rather than defaulted to.
var cappedOpt = new Option<bool>("--capped") { Description = "The lens cap is ON — enables the phase that discriminates AF from MF." };

var avOpt = new Option<EdsAv>("--av") { Description = "Aperture.", DefaultValueFactory = _ => EdsAv.Av_5_6 , HelpName = "aperture" };
var tvOpt = new Option<EdsTv>("--tv") { Description = "Shutter speed.", DefaultValueFactory = _ => EdsTv.Tv_1_125 , HelpName = "shutter" };
var isoOpt = new Option<EdsISOSpeed>("--iso") { Description = "ISO.", DefaultValueFactory = _ => EdsISOSpeed.ISO_400 , HelpName = "iso" };

var root = new RootCommand("Canon hardware diagnostics — judged on delivered bytes, not response codes.");
foreach (var o in new Option[] { hostOpt, releaseOpt }) root.Options.Add(o);

// Subcommands inherit the two common options via Recursive, so `--host` works wherever it makes
// sense without being declared five times.
hostOpt.Recursive = releaseOpt.Recursive = true;

(string Name, string Description, Option[] Options)[] modes =
[
    ("matrix",   "The mirror-lockup x remote-release matrix. The default experiment.", [settleOpt, onlyOpt, driveOpt, drive2Opt, drive3Opt]),
    ("diag",     "Dump the properties that gate a release, then release / bulb / live view.", []),
    ("mlucheck", "Does a MirrorUpSetting write actually land? Read-back, not the ACK.", []),
    ("mluself",  "Watch 0xD1BF across a self-timer countdown.", [driveOpt, waitOpt]),
    ("clack",    "A/B listening test: self-timer with lockup off, then on.", [driveOpt]),
    ("evf",      "String properties, live-view zoom / pan / autofocus, envelope record dump.", []),
    ("zoom",     "Why a zoom is ignored: sweep factors and LV AF systems against the zoom rect.", []),
    ("zoompix",  "Visual proof of magnification and panning — writes JPEGs to compare.", [avOpt, tvOpt, isoOpt]),
    ("lens",     "Dump the lens / focus properties. Run with no lens, lens at AF, lens at MF and diff.", []),
    ("press",    "0x9128's two parameters: press stage x AF/MF, judged on delivered files.", [avOpt, tvOpt, isoOpt, cappedOpt]),
    ("afpress",  "Does 0x9128's p2 drive the focus motor? Judged on frame sharpness, against controls.", [avOpt, tvOpt, isoOpt]),
    ("meter",    "Live-view histogram: is it real metering? Structure, channel order, exposure sweep.", [avOpt, tvOpt, isoOpt]),
    ("power",    "Is AutoPowerOff (0xD114) writable, or was its DeviceBusy just a no-op write?", []),
];

string? mode = null;
foreach (var (name, description, options) in modes)
{
    var cmd = new Command(name, description);
    foreach (var o in options) cmd.Options.Add(o);
    cmd.SetAction(_ => { mode = name; return 0; });
    root.Subcommands.Add(cmd);
}
root.SetAction(_ => { mode = "matrix"; return 0; });

var parse = root.Parse(args);
int parseExit = parse.Invoke();
// Help, version and parse errors all leave the mode unset — they have already printed, so the only
// thing left to do is carry their exit code out.
if (mode is null) return parseExit;

int settleSeconds = parse.GetValue(settleOpt);
uint SelfTimerDrive = parse.GetValue(driveOpt);
uint SelfTimerDrive2 = parse.GetValue(drive2Opt);
uint SelfTimerDrive3 = parse.GetValue(drive3Opt);
string[]? only = parse.GetValue(onlyOpt) is { Length: > 0 } selectedRows ? selectedRows : null;

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
if (mode is "diag")
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
    var dest = parse.GetValue(hostOpt) ? CanonCaptureDestination.Host : CanonCaptureDestination.Card;
    Log($"CaptureDestination={dest} = {await camera.SetCaptureDestinationAsync(dest)}");

    // Anything a previous (or crashed) run left un-fetched keeps the body busy. Handles are given
    // on the command line because a dead process took its own list to the grave.
    foreach (var handle in parse.GetValue(releaseOpt) ?? [])
        Log($"TransferComplete(0x{handle:X8}) = {await camera.TransferCompleteAsync(handle)}");
    // Not SetAutoPowerOffAsync: a 6D refuses 0xD114 with DeviceBusy in every state tried — drained,
    // under UILock, in live view — and announces no allowed values for it, so auto power-off is a
    // camera-menu setting here. The old line logged "AutoPowerOff=off = DeviceBusy" every run, which
    // read as if the harness were managing power when it could not. 0x911D is what actually works.
    Log($"KeepDeviceOn = {await camera.KeepDeviceOnAsync()}");
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

    if (mode is "clack")
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

    if (mode is "mluself")
    {
        // The measurement nobody has taken: with MLU armed and a self-timer running, does the mirror
        // actually go up during the countdown? A delivered file cannot answer it — the 6D delivers a
        // file either way — and the mirror is back down by the time anyone looks afterwards. So watch
        // 0xD1BF continuously across the whole countdown instead of sampling it at the ends.
        const ushort MirrorLockUpState = 0xD1BF;
        const ushort MirrorUpSetting = 0xD13A;

        var drive = SelfTimerDrive;
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
        if (parse.GetValue(waitOpt) is var lead && lead > 0)
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

    if (mode is "mlucheck")
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

    if (mode is "power")
    {
        // Every run logs "AutoPowerOff=off = DeviceBusy", and that one line has two readings that
        // call for opposite actions: a write the body refused, or the documented no-op response to
        // setting a property to the value it already holds. Guessing was wrong once already today,
        // so this reads the value, reads its allowed list, and moves it — a write is only proven by
        // a read-back that changed.
        const ushort AutoPowerOff = 0xD114;

        var (readErr, current) = await camera.GetRawPropertyAsync(AutoPowerOff);
        var allowed = await camera.GetAllowedValuesAsync(EdsPropertyId.AutoPowerOffSetting);
        Log($"0xD114 currently {current} ({readErr})");
        Log($"0xD114 allowed:  {(allowed is null ? "none announced" : string.Join(", ", allowed))}");

        if (readErr is not EdsError.OK)
        {
            Log("*** The body does not report this property, so nothing below would mean anything.");
            return 1;
        }

        // DeviceBusy is Canon's generic refusal, not "read-only": this repo documents an undrained
        // event queue, a value already held, and a malformed record as three unrelated causes. So a
        // single busy write does not establish that the property cannot be written, and concluding
        // that from one attempt was reading a response code as truth again.
        //
        // The queue cause is already covered — SetPropertyBytesAsync drains and retries on busy — so
        // what is left is state: does the body accept this write under UILock, or in live view? Each
        // phase writes a value that DIFFERS from the current one, so a no-op can never explain a
        // pass, and each is judged on a read-back rather than its response code.
        var target = allowed?.FirstOrDefault(v => v != current)
            ?? (current == 0 ? 1u : 0u);
        Log($"\nTarget {target}, differs from {current} — a no-op cannot explain a pass.");

        async Task<(EdsError Set, uint After, bool Landed)> TryWriteAsync(string label, Func<Task> setup, Func<Task> teardown)
        {
            await setup();
            await camera!.DrainEventsAsync();
            var setErr = await camera.SetAutoPowerOffAsync(target);
            await Task.Delay(2000);
            await camera.DrainEventsAsync();
            var (_, got) = await camera.GetRawPropertyAsync(AutoPowerOff);
            await teardown();

            var landed = got == target;
            Log($"  {label,-22} set = {setErr,-12} reads back {got} => {(landed ? "LANDED" : "did not land")}");

            // Leave the body where it was found, so a later phase is not testing a changed baseline.
            if (landed)
            {
                await camera.SetAutoPowerOffAsync(current);
                await Task.Delay(1500);
                await camera.DrainEventsAsync();
            }
            return (setErr, got, landed);
        }

        Log("");
        var plain = await TryWriteAsync("plain (drained)", () => Task.CompletedTask, () => Task.CompletedTask);

        // EDSDK takes the UI lock around some writes; a body that will not let a host change a menu
        // setting while the user could be in that menu is a plausible refusal this would clear.
        var locked = await TryWriteAsync("under UILock",
            async () => Log($"    SetUILock = {await camera.SetUILockAsync(true)}"),
            async () => Log($"    ResetUILock = {await camera.SetUILockAsync(false)}"));

        // The condition actually asked about. Live view is the body's busiest state, so if anything
        // makes this worse it is this — and if it makes it BETTER that is worth knowing too.
        var inLiveView = await TryWriteAsync("in live view",
            async () => { await camera.StartLiveViewAsync(); await Task.Delay(1500); },
            async () => { await camera.StopLiveViewAsync(); await Task.Delay(1000); });

        Log("");
        if (plain.Landed || locked.Landed || inLiveView.Landed)
        {
            Log("=> WRITABLE, but only in some states. The earlier 'not writable' verdict was wrong.");
            Log($"   plain {(plain.Landed ? "yes" : "no")}, UILock {(locked.Landed ? "yes" : "no")}, live view {(inLiveView.Landed ? "yes" : "no")}");
        }
        else
        {
            Log("=> Refused in all three states, and the body announces no allowed values for 0xD114.");
            Log("   That is as far as this can be pushed from the host: treat auto power-off as a");
            Log("   camera-menu setting, and use KeepDeviceOn (0x911D) to hold a body awake.");
            Log("   Still only one body — a 450D may differ, as it does for so much else.");
        }
        return 0;
    }

    if (mode is "meter")
    {
        // Is live-view record 17 real metering? Three questions, each with a way to fail.
        //
        // The record has been called "histogram — 4 channels x 256 bins x uint32" on the strength of
        // its size alone, which 4096 bytes of anything would satisfy. Structure, identity and
        // liveness all have to be shown separately.
        Log($"Av={parse.GetValue(avOpt)}: {await camera.SetApertureAsync(parse.GetValue(avOpt))}");
        Log($"Tv={parse.GetValue(tvOpt)}: {await camera.SetShutterSpeedAsync(parse.GetValue(tvOpt))}");

        await camera.StartLiveViewAsync();
        await Task.Delay(1500);
        await camera.SetEvfAfSystemAsync(CanonEvfAfSystem.Live);

        var hist = await camera.GetEvfHistogramAsync();
        if (hist is not { } h)
        {
            Log("*** No histogram record. Either this body sends none, or the 4x256 grouping was");
            Log("*** rejected because the four channels did not count the same pixels.");
            await camera.StopLiveViewAsync();
            return 1;
        }

        // 1. Structure. The decoder already refused a payload whose four groups disagreed on the
        //    pixel count, so reaching here is itself the check; print it so it is visible.
        Log($"Structure: 4 x 256 bins, {h.PixelCount:N0} pixels per channel (all four agree)");
        Log($"Luma: {h}");

        // 2. Identity. Luma is a fixed blend of the colour channels, so of the 24 ways to assign four
        //    groups to (Y,R,G,B) only the right one satisfies Y = 0.299R + 0.587G + 0.114B. A scene
        //    where the channels differ — any indoor light is warm enough — makes that decisive.
        //    If the best fit is not clearly better than the next, this says so instead of asserting.
        double[] means = [Mean(h.Luma), Mean(h.Red), Mean(h.Green), Mean(h.Blue)];
        var fits = new List<(double Err, string Order)>();
        for (var y = 0; y < 4; y++)
        foreach (var (r, g, b) in Permutations(Enumerable.Range(0, 4).Where(i => i != y).ToArray()))
            fits.Add((Math.Abs(means[y] - (0.299 * means[r] + 0.587 * means[g] + 0.114 * means[b])),
                      $"Y={y} R={r} G={g} B={b}"));
        fits.Sort((a, b) => a.Err.CompareTo(b.Err));

        Log($"Channel means (as decoded Y,R,G,B): {string.Join(", ", means.Select(m => m.ToString("P1")))}");
        Log($"Best luma fit:   {fits[0].Order}  residual {fits[0].Err:P2}");
        Log($"Runner-up:       {fits[1].Order}  residual {fits[1].Err:P2}");
        Log(fits[0].Order is "Y=0 R=1 G=2 B=3"
            ? "  => the decoded order is confirmed."
            : "  *** the decoded order is WRONG — CanonEvfHistogram is mislabelling its channels.");
        if (fits[1].Err < fits[0].Err * 3)
            Log("  *** but the margin is thin. A scene with more colour separation would settle it.");

        // 3. Liveness. A static blob would survive every check above. Sweeping ISO must move it, and
        //    monotonically: this is the property that makes it a light meter rather than a constant.
        Log("\nExposure sweep — the histogram must track ISO, or it is not metering anything:");
        var sweep = new List<(EdsISOSpeed Iso, double Mean)>();
        foreach (var iso in new[] { EdsISOSpeed.ISO_100, EdsISOSpeed.ISO_400, EdsISOSpeed.ISO_1600, EdsISOSpeed.ISO_6400 })
        {
            var setErr = await camera.SetISOAsync(iso);
            await Task.Delay(1200);
            var s = await camera.GetEvfHistogramAsync();
            if (s is not { } sh) { Log($"  {iso}: {setErr}, no frame"); continue; }
            sweep.Add((iso, sh.MeanLevel));
            Log($"  {iso,-12} mean {sh.MeanLevel,7:P2}   p99 {sh.Percentile(0.99),7:P1}   clipped high {sh.ClippedHighlights,6:P1}");
        }

        var rising = sweep.Count >= 3 && sweep.Zip(sweep.Skip(1)).All(p => p.Second.Mean >= p.First.Mean);
        Log(rising
            ? "  => monotonic in ISO. This is live metering of the actual exposure."
            : "  *** NOT monotonic — either the sweep clipped, or this record is not what it looks like.");

        await camera.StopLiveViewAsync();

        static double Mean(uint[] bins)
        {
            long total = 0, weighted = 0;
            for (var i = 0; i < bins.Length; i++) { total += bins[i]; weighted += (long)bins[i] * i; }
            return total is 0 ? 0 : weighted / (double)total / (bins.Length - 1);
        }

        static IEnumerable<(int, int, int)> Permutations(int[] v) =>
        [
            (v[0], v[1], v[2]), (v[0], v[2], v[1]), (v[1], v[0], v[2]),
            (v[1], v[2], v[0]), (v[2], v[0], v[1]), (v[2], v[1], v[0]),
        ];
        return 0;
    }

    if (mode is "afpress")
    {
        // Does 0x9128's p2 drive autofocus? Neither timing nor delivery could tell: with the cap on,
        // an "AF" press delivered a frame in 146 ms, far too quick to be a failed hunt, so probably
        // no AF was attempted — but "probably" is not a measurement. Listening did not settle it
        // either: no motor was heard on any row, and "I heard nothing" is the one report a listening
        // test cannot distinguish from "I missed it".
        //
        // So the observable is the delivered frame's own sharpness. Every row is preceded by the
        // same 6-step DriveLens defocus, and a row that autofocused comes back sharp.
        //
        // That only means something against controls, because a sharpness measure that cannot see
        // focus would rank four defocused frames "all equal" and look exactly like a null result.
        // Each round therefore carries both ends of the scale:
        //
        //   blur — defocus, then release with no focus command at all   (known soft)
        //   doaf — defocus, then 0x9154 DoAf, which is proven on this   (known sharp)
        //          body: the STM is audible and the LV JPEG jumps 51->120 KB
        //
        // If blur and doaf do not separate, the instrument is blind and the p2 rows are void.
        // Cap OFF for this one, and the lens switch at AF.
        const ushort RemoteReleaseOn = 0x9128, RemoteReleaseOff = 0x9129;

        var (_, focus) = await camera.GetFocusStateAsync();
        Log($"Focus: {focus}");
        if (!focus.AutoFocusAvailable) { Log("*** Autofocus unavailable — mount a lens, switch to AF."); return 1; }

        // The first pass ran f/5.6 1/125 at whatever ISO the body held and came back at 1% of full
        // scale. Underexposure is not neutral here: it buries scene structure under read noise,
        // which is the one thing a sharpness measure must not be reading.
        Log($"Av={parse.GetValue(avOpt)}: {await camera.SetApertureAsync(parse.GetValue(avOpt))}");
        Log($"Tv={parse.GetValue(tvOpt)}: {await camera.SetShutterSpeedAsync(parse.GetValue(tvOpt))}");

        // ...so the ISO is metered rather than guessed. The body is in Manual, so nothing else will
        // do it, and a run whose exposure was wrong is a run wasted — as the first one was.
        await camera.StartLiveViewAsync();
        await Task.Delay(1500);
        await camera.SetEvfAfSystemAsync(CanonEvfAfSystem.Live);

        const double TargetLevel = 0.20;
        (EdsISOSpeed Iso, double Mean)? best = null;
        foreach (var candidate in new[]
        {
            EdsISOSpeed.ISO_100, EdsISOSpeed.ISO_200, EdsISOSpeed.ISO_400, EdsISOSpeed.ISO_800,
            EdsISOSpeed.ISO_1600, EdsISOSpeed.ISO_3200, EdsISOSpeed.ISO_6400,
        })
        {
            await camera.SetISOAsync(candidate);
            await Task.Delay(900);
            if (await camera.GetEvfHistogramAsync() is not { } m) continue;
            Log($"  meter {candidate,-12} mean {m.MeanLevel,7:P2}  clipped high {m.ClippedHighlights,6:P1}");
            if (best is null || Math.Abs(m.MeanLevel - TargetLevel) < Math.Abs(best.Value.Mean - TargetLevel))
                best = (candidate, m.MeanLevel);
        }
        await camera.StopLiveViewAsync();
        await Task.Delay(1000);

        if (best is not { } pick)
        {
            Log("*** The meter never answered, so the exposure would be a guess again. Refusing.");
            return 1;
        }
        Log($"ISO={pick.Iso} (metered {pick.Mean:P1}, target {TargetLevel:P0}): {await camera.SetISOAsync(pick.Iso)}");
        if (pick.Mean < 0.05)
            Log("*** Even the best rung is very dark — expect the sharpness controls not to separate.");

        // Returns how many of the six steps the body acknowledged. An ACK is not motion, but a zero
        // is conclusive the other way: nothing moved, so there is nothing for a focus command to
        // correct and the row cannot answer anything.
        async Task<int> DefocusReported()
        {
            await camera!.StartLiveViewAsync();
            await Task.Delay(1200);
            await camera.SetEvfAfSystemAsync(CanonEvfAfSystem.Live);
            int ok = 0;
            for (int i = 0; i < 6; i++)
            {
                if (await camera.DriveLensAsync(EdsDriveLensStep.NearLarge) is EdsError.OK) ok++;
                await Task.Delay(250);
            }
            return ok;
        }

        int busyRows = 0;
        foreach (var round in new[] { 1, 2 })
        foreach (var (tag, p2) in new (string, uint?)[] { ("blur", null), ("doaf", null), ("p2is0", 0), ("p2is1", 1) })
        {
            var defocused = await DefocusReported();
            Log($"\nround {round}, {tag} — defocus drove the lens {defocused}/6");
            if (defocused is 0) { Log("  *** the lens did not move — void"); await camera.StopLiveViewAsync(); continue; }

            // DoAf needs live view; the other rows must not have it, since entering live view is
            // itself a focus opportunity on a body doing contrast AF.
            //
            // The hunt MUST be cancelled before live view goes away. Tearing down live view with a
            // hunt still in flight wedged this body for three solid minutes — every subsequent
            // release answered DeviceBusy, including the controls, which invalidated a whole run.
            // 0x9154 returns on acceptance, not on focus, so its OK says nothing about whether the
            // lens has stopped moving; in a dim room at the near limit the hunt outlasts any delay
            // worth waiting. AfCancel is not a tidy-up here, it is what keeps the body usable.
            string focusNote = "none";
            if (tag is "doaf")
            {
                var afErr = await camera.AutoFocusLiveViewAsync();
                await Task.Delay(4000);
                var cancelErr = await camera.CancelAutoFocusAsync();
                await Task.Delay(500);
                focusNote = $"0x9154 = {afErr}, 0x9160 = {cancelErr}";
            }
            await camera.StopLiveViewAsync();
            await Task.Delay(1500);
            await camera.DrainEventsAsync();

            var pressMs = 0d;
            if (p2 is { } stage)
            {
                Log("  LISTEN NOW (3 beeps, then the press)");
                for (int i = 0; i < 3; i++)
                {
                    if (OperatingSystem.IsWindows()) Console.Beep(1200, 120);
                    await Task.Delay(700);
                }
            }

            lock (objects) objects.Clear();
            await camera.DrainEventsAsync();
            var sw = Stopwatch.StartNew();
            EdsError e1;
            if (p2 is { } s2)
            {
                e1 = await camera.SendRawCommandAsync(RemoteReleaseOn, 3, s2);
                pressMs = sw.Elapsed.TotalMilliseconds;
                await Task.Delay(2500);
                await camera.SendRawCommandAsync(RemoteReleaseOff, 2);
                await camera.SendRawCommandAsync(RemoteReleaseOff, 1);
            }
            else
            {
                // The controls release the ordinary way, so the only thing that differs between a
                // control and a p2 row is the focus command under test.
                e1 = await camera.TakePictureAsync();
                pressMs = sw.Elapsed.TotalMilliseconds;
            }
            var (got, bytes) = await CollectAsync($"{round}{tag}", TimeSpan.FromSeconds(12));
            Log($"  {tag}: release = {e1}   focus {focusNote}   {pressMs:F0} ms   {(got ? $"IMAGE {bytes:N0}" : "no image")}");

            // A wedged body answers DeviceBusy to everything, and the rows keep printing as if they
            // were results. That is the shape of a run that reads like six tidy findings and is
            // really one fault repeated, so it stops here instead.
            if (e1 is EdsError.DeviceBusy)
            {
                Log("  *** DeviceBusy — attempting recovery (AfCancel + drain)");
                await camera.CancelAutoFocusAsync();
                await camera.DrainEventsAsync();
                await Task.Delay(2000);
                if (++busyRows >= 2)
                {
                    Log("*** Two busy releases: the body is wedged and every later row would be void.");
                    Log("*** Aborting. Nothing above the first busy row is affected.");
                    break;
                }
            }
            else busyRows = 0;
        }

        Log("\nNow measure sharpness across the eight frames.");
        Log("If blur and doaf do not separate, the measurement is blind and nothing here is evidence.");
        Log("If they do, p2=0 landing on doaf means p2 selects AF; landing on blur means it does not.");
        return 0;
    }

    if (mode is "press")
    {
        // What are 0x9128's two parameters? libgphoto2 documents them as
        //   p1: 1 = half press, 2 = full press, 3 = half+full in one go
        //   p2: 0 = AF, 1 = MF
        // but that is someone else's source read, not a measurement, and this repo has been wrong
        // before by trusting a claim it did not test. We send only p1 today, so if p2=1 really does
        // release without autofocus it is exactly what a telescope needs and we are not using it.
        const ushort RemoteReleaseOn = 0x9128, RemoteReleaseOff = 0x9129;

        // The matrix is void unless autofocus is actually possible: with the lens at MF or absent,
        // p2=0 and p2=1 would both do nothing about focus and the rows would look identical for a
        // reason that has nothing to do with the parameter.
        var (_, focus) = await camera.GetFocusStateAsync();
        Log($"Focus: {focus}");
        if (!focus.AutoFocusAvailable)
        {
            Log("*** Autofocus is unavailable, so the AF/MF parameter cannot show a difference.");
            Log("*** Mount a lens and set its switch to AF. Refusing to run a matrix that cannot answer.");
            return 1;
        }
        if (!camera.SupportedOperations.Contains(RemoteReleaseOn))
        {
            Log("*** This body has no 0x9128 (it is a DIGIC III single-shot-release body). Nothing to test.");
            return 1;
        }

        Log($"Av={parse.GetValue(avOpt)}: {await camera.SetApertureAsync(parse.GetValue(avOpt))}");
        Log($"Tv={parse.GetValue(tvOpt)}: {await camera.SetShutterSpeedAsync(parse.GetValue(tvOpt))}");
        Log($"ISO={parse.GetValue(isoOpt)}: {await camera.SetISOAsync(parse.GetValue(isoOpt))}");

        // Autofocus with nothing to do completes instantly and hides the very difference being
        // measured, so the lens is driven off focus before each AF row. DriveLens only works in
        // live view, hence the start/stop around it.
        async Task Defocus()
        {
            await camera!.StartLiveViewAsync();
            await Task.Delay(1200);
            await camera.SetEvfAfSystemAsync(CanonEvfAfSystem.Live);

            // Reported, not assumed. A defocus that quietly failed would leave autofocus with
            // nothing to do, and then the AF and MF rows would agree for a reason that has nothing
            // to do with p2 — which is exactly how the first run of this matrix proved nothing.
            var results = new List<EdsError>();
            for (int i = 0; i < 6; i++)
            {
                results.Add(await camera.DriveLensAsync(EdsDriveLensStep.NearLarge));
                await Task.Delay(250);
            }
            var ok = results.Count(r => r is EdsError.OK);
            Log($"  defocus: DriveLens NearLarge x6 -> {ok}/6 OK"
                + (ok == 0 ? "   *** the lens did not move; the AF row below is void" : ""));

            await camera.StopLiveViewAsync();
            await Task.Delay(1200);
        }

        async Task<(bool Delivered, long Bytes, double PressMs, double TotalMs)> Press(string id, uint p1, uint p2)
        {
            lock (objects) objects.Clear();
            await camera!.DrainEventsAsync();

            // The event stream is the instrument that settled mirror lockup, and it is the right one
            // here too: if p2 selects autofocus, an AF row emits focus records that an MF row does
            // not, whatever the timings say.
            var seen = new List<string>();
            void OnProp(object? _, CanonPropertyChangedEventArgs e)
            { lock (seen) seen.Add($"prop {(uint)e.PropertyId:X4}=0x{e.Value:X}"); }
            void OnState(object? _, CanonStateChangedEventArgs e)
            { lock (seen) seen.Add($"{e.EventType}=0x{e.Param:X}"); }
            camera.PropertyChanged += OnProp;
            camera.StateChanged += OnState;

            var sw = Stopwatch.StartNew();
            var onErr = await camera.SendRawCommandAsync(RemoteReleaseOn, p1, p2);
            var pressMs = sw.Elapsed.TotalMilliseconds;

            await Task.Delay(1500);

            // Mirror the press: full then half, so a half-press left over cannot hold the body.
            if (p1 is 2 or 3) await camera.SendRawCommandAsync(RemoteReleaseOff, 2);
            if (p1 is 1 or 3) await camera.SendRawCommandAsync(RemoteReleaseOff, 1);

            var (delivered, bytes) = await CollectAsync(id, TimeSpan.FromSeconds(12));
            Log($"  0x9128({p1},{p2}) = {onErr,-12} press {pressMs,7:F0} ms   "
                + $"{(delivered ? $"IMAGE {bytes:N0} bytes" : "no image")}   total {sw.Elapsed.TotalSeconds:F1}s");
            return (delivered, bytes, pressMs, sw.Elapsed.TotalMilliseconds);
        }

        Log("\n--- control: the ordinary path must deliver, or nothing below means anything ---");
        lock (objects) objects.Clear();
        await camera.DrainEventsAsync();
        Log($"TakePicture = {await camera.TakePictureAsync()}");
        var (c1, c1Bytes) = await CollectAsync("CTRL-1", TimeSpan.FromSeconds(15));
        Log($"  control 1: {(c1 ? $"IMAGE {c1Bytes:N0} bytes" : "*** NO IMAGE — matrix is void")}");
        if (!c1) { Log("Aborting: the body is not delivering at all."); return 1; }

        Log("\n--- phase 1: what p1 does ---");
        Log("    p1: 1=half 2=full 3=half+full     p2: 0=AF 1=MF   (per libgphoto2 — under test)");
        foreach (var (p1, p2) in new (uint, uint)[] { (1, 0), (1, 1), (2, 0), (2, 1), (3, 0), (3, 1) })
        {
            // Only the AF rows need the lens moved off focus; doing it for the MF rows too keeps the
            // starting state identical, so a timing difference cannot be blamed on the setup.
            await Defocus();
            await Press($"P{p1}{p2}", p1, p2);
        }

        // Phase 2 exists because phase 1 cannot answer p2. Both AF and MF rows delivered a frame in
        // ~290 ms, which is what you get whenever autofocus has nothing to do — and timing was
        // always the weaker instrument anyway.
        //
        // The discriminating question is what happens when autofocus CANNOT succeed. In One-Shot the
        // body is focus-priority: no lock, no shutter. So with the lens capped, a press that really
        // uses AF should deliver nothing, while a press that skips AF should still expose (a black
        // frame is still a frame — this is judged on delivery, not on content). AI Servo is
        // release-priority and should expose either way, which is the control that proves the cap
        // itself is not what stopped the shutter.
        if (parse.GetValue(cappedOpt))
        {
            Log("\n--- phase 2: p2 where autofocus CANNOT lock (lens capped) ---");
            Log("    expectation if p2 selects AF:  OneShot+AF fails, everything else delivers");

            foreach (var af in new[] { EdsAFMode.OneShot, EdsAFMode.AIServo })
            {
                var setErr = await camera.SetPropertyAsync(EdsPropertyId.AFMode, (uint)af);
                await Task.Delay(700);
                var (_, nowRaw) = await camera.GetPropertyAsync(EdsPropertyId.AFMode);
                var now = (EdsAFMode)nowRaw;
                Log($"\n  AFMode -> {af} ({setErr}), reads {now}"
                    + (now == af ? "" : "   *** DID NOT TAKE, these two rows are void"));

                foreach (uint p2 in new uint[] { 0, 1 })
                    await Press($"CAP-{af}-{p2}", 3, p2);
            }
            await camera.SetPropertyAsync(EdsPropertyId.AFMode, (uint)EdsAFMode.OneShot);
        }
        else
        {
            Log("\n(phase 2 skipped — pass --capped, with the lens cap ON, to discriminate p2)");
        }

        Log("\n--- control: same again at the end ---");
        lock (objects) objects.Clear();
        await camera.DrainEventsAsync();
        Log($"TakePicture = {await camera.TakePictureAsync()}");
        var (c2, c2Bytes) = await CollectAsync("CTRL-2", TimeSpan.FromSeconds(15));
        Log($"  control 2: {(c2 ? $"IMAGE {c2Bytes:N0} bytes" : "*** NO IMAGE — the body stopped mid-run")}");
        if (!c2) failures++;
        return failures;
    }

    if (mode is "lens")
    {
        // Can the body tell us where the lens's AF/MF switch is? It matters twice over: a caller on
        // a telescope wants to know autofocus is unavailable, and the 0x9128 AF/MF parameter can
        // only be tested in the one configuration where AF is actually possible.
        //
        // Run three times — no lens, lens at AF, lens at MF — and diff. Any code that moves between
        // AF and MF while the lens stays mounted is reporting the switch; anything that only moves
        // when the lens comes off is reporting the mount.
        (ushort Code, string Name)[] candidates =
        [
            (0xD108, "FocusMode"), (0xD1A8, "LensStatus"), (0xD1DD, "LensID"),
            (0xD128, "LensBarrelStatus"), (0xD17A, "ContinuousAFValid"), (0xD124, "RefocusState"),
            (0xD1BA, "Evf_AFMode"), (0xD1D3, "FocusInfoEx"),
        ];

        var (_, lens) = await camera.GetLensNameAsync();
        Log($"Lens name: \"{lens}\"   (tag this run: no lens / lens AF / lens MF)");
        Log("");

        foreach (var (code, name) in candidates)
        {
            var (e, v) = await camera.GetRawPropertyAsync(code);
            var (be, bytes) = await camera.GetRawPropertyBytesAsync(code);
            var allowed = await camera.GetRawAllowedValuesAsync(code);
            var hex = be is EdsError.OK && bytes.Length > 0
                ? string.Join(' ', bytes.Take(16).Select(b => b.ToString("X2")))
                : "-";
            Log($"  0x{code:X4} {name,-18} = {(e is EdsError.OK ? $"0x{v:X8} ({v})" : e.ToString()),-22} "
                + $"[{bytes.Length,3} bytes: {hex}]");
            if (allowed is { Length: > 0 })
                Log($"         allowed: {string.Join(',', allowed.Select(a => $"0x{a:X}"))}");
        }

        // FocusMode is the one our EdsAFMode already names, so spell out its reading.
        var (fe, fv) = await camera.GetPropertyAsync(EdsPropertyId.AFMode);
        Log($"\n  EdsPropertyId.AFMode = {(EdsAFMode)fv} ({fe})");
        var (fsErr, focus) = await camera.GetFocusStateAsync();
        Log($"  GetFocusStateAsync   = {focus}  ({fsErr})");
        Log($"  0x9128 RemoteReleaseOn advertised: {camera.SupportedOperations.Contains(0x9128)}");
        Log($"  0x9154 DoAf advertised:            {camera.SupportedOperations.Contains(0x9154)}");
        return 0;
    }

    if (mode is "zoompix")
    {
        // The rect record is the body's own claim about magnification. This checks the claim against
        // the pixels: a 5x crop must LOOK like a crop. Needed because the record numbering is our own
        // reading of one model's bytes — if type 18 were something else, the rect could track
        // perfectly while the image never changed.
        //
        // Stopped down first, because the earlier runs were so overexposed that a crop and a full
        // frame were both white rectangles and the visual test could not have failed.
        // The mode dial is physical on a 6D, so the body cannot be put into Av over PTP to meter for
        // itself — the exposure has to be dialled in from here. Overridable, because the right
        // values depend on the room.
        var av = parse.GetValue(avOpt);
        var shutter = parse.GetValue(tvOpt);
        var iso = parse.GetValue(isoOpt);
        Log($"Av = {av}: {await camera.SetApertureAsync(av)}");
        Log($"Tv = {shutter}: {await camera.SetShutterSpeedAsync(shutter)}");
        Log($"ISO = {iso}: {await camera.SetISOAsync(iso)}");

        Log($"StartLiveView = {await camera.StartLiveViewAsync()}");
        await Task.Delay(2000);
        Log($"SetEvfAfSystem(Live) = {await camera.SetEvfAfSystemAsync(CanonEvfAfSystem.Live)}");
        await Task.Delay(900);

        async Task Shot(string tag)
        {
            for (int i = 0; i < 30; i++)
            {
                var (e, jpeg) = await camera!.GetLiveViewFrameAsync();
                if (e is EdsError.OK && jpeg.Length > 1000 && jpeg[0] == 0xFF && jpeg[1] == 0xD8)
                {
                    await File.WriteAllBytesAsync(Path.Combine(outDir, $"{tag}.jpg"), jpeg);
                    Log($"  {tag,-16} {jpeg.Length,7:N0} bytes   rect {await camera.GetEvfZoomRectAsync()}");
                    return;
                }
                await Task.Delay(100);
            }
            Log($"  {tag}: NO FRAME");
        }

        // Centre the crop first, so 1x / 5x / 10x differ only in magnification.
        await camera.SetEvfZoomAsync(CanonEvfZoom.X5);
        await camera.SetEvfZoomPositionAsync(2184, 1456);
        await Task.Delay(1000);
        await camera.SetEvfZoomAsync(CanonEvfZoom.Fit);
        await Task.Delay(1200);

        Log("\n--- magnification, same centre ---");
        await Shot("mag_1x");
        Log($"  zoom X5  = {await camera.SetEvfZoomAsync(CanonEvfZoom.X5)}");
        await Shot("mag_5x");
        Log($"  zoom X10 = {await camera.SetEvfZoomAsync(CanonEvfZoom.X10)}");
        await Shot("mag_10x");

        // Panning at 5x across the frame. Four corners plus centre: if these are really different
        // regions of the sensor, the five images show five different parts of the scene.
        Log("\n--- panning at 5x ---");
        await camera.SetEvfZoomAsync(CanonEvfZoom.X5);
        await Task.Delay(1200);
        // Corners in SENSOR coordinates. An earlier pass used 9999 as "far enough" and the axis
        // simply kept its previous value: a coordinate beyond the sensor is rejected outright, not
        // clamped, so the frames all looked like the pan had failed. 4368 (inside 5472) does clamp.
        foreach (var (tag, x, y) in new (string, uint, uint)[]
                 {
                     ("pan_topleft", 0, 0), ("pan_topright", 5471, 0), ("pan_centre", 2184, 1456),
                     ("pan_bottomleft", 0, 3647), ("pan_bottomright", 5471, 3647),
                 })
        {
            await camera.SetEvfZoomPositionAsync(x, y);
            await Task.Delay(1000);
            await Shot(tag);
        }

        await camera.SetEvfZoomAsync(CanonEvfZoom.Fit);
        Log($"\nStopLiveView = {await camera.StopLiveViewAsync()}");
        Log($"Images in {outDir}");
        return 0;
    }

    if (mode is "zoom")
    {
        // Why does 0x9158 answer OK and leave the feed at full frame? Judged on the zoom rect the
        // body itself reports in the live-view envelope (record type 18: x, y, w, h) — not on the
        // response code, which is already known to lie here, and not on eyeballing a JPEG.
        const uint ZoomRectRecord = 18;
        const ushort LvAfSystem = 0xD1BA;
        const ushort LvViewTypeSelect = 0xD1BC;

        static uint U32(ReadOnlySpan<byte> s, int at) => BitConverter.ToUInt32(s[at..(at + 4)]);

        async Task<(uint X, uint Y, uint W, uint H)?> Rect(int tries = 30)
        {
            for (int i = 0; i < tries; i++)
            {
                var (e, records) = await camera!.GetLiveViewRecordsAsync();
                if (e is EdsError.OK)
                    foreach (var r in records)
                        if (r.Type == ZoomRectRecord && r.Payload.Length >= 16)
                        {
                            var s = r.Payload.Span;
                            return (U32(s, 0), U32(s, 4), U32(s, 8), U32(s, 12));
                        }
                await Task.Delay(100);
            }
            return null;
        }

        string Show((uint X, uint Y, uint W, uint H)? r) =>
            r is { } v ? $"({v.X},{v.Y}) {v.W}x{v.H}" : "no frame";

        Log($"StartLiveView = {await camera.StartLiveViewAsync()}");
        await Task.Delay(2000);

        var baseline = await Rect();
        Log($"baseline rect: {Show(baseline)}");
        if (baseline is null) { Log("No frames — nothing below this line means anything."); return 1; }

        foreach (var code in new[] { LvAfSystem, LvViewTypeSelect })
        {
            var (e, v) = await camera.GetRawPropertyAsync(code);
            var allowed = await camera.GetRawAllowedValuesAsync(code);
            Log($"  0x{code:X4} = {(e is EdsError.OK ? $"0x{v:X}" : e.ToString())}"
                + $"   allowed: {(allowed is null ? "none announced" : string.Join(',', allowed.Select(a => $"0x{a:X}")))}");
        }

        // 1: does ANY zoom factor move the rect? gphoto2 passes the value straight through and
        // documents only 1 and 5; EDSDK's property uses 1/5/10. Neither is evidence for this body.
        Log("\n--- zoom factors, AF system as found ---");
        foreach (uint z in new uint[] { 1, 2, 3, 5, 10 })
        {
            var err2 = await camera.SetEvfZoomAsync(z);
            await Task.Delay(1200);
            await camera.DrainEventsAsync();
            var r = await Rect();
            Log($"  zoom {z,-3} = {err2,-10} rect {Show(r)}"
                + $"{(r is { } v && baseline is { } b && (v.W != b.W || v.H != b.H) ? "   <<< MOVED" : "")}");
        }
        await camera.SetEvfZoomAsync(1);
        await Task.Delay(800);

        // 2: a 6D disables magnification in Face+Tracking AF. If that is the gate, changing the LV
        // AF system and re-trying is what shows it.
        Log("\n--- zoom 5 across LV AF systems (0xD1BA) ---");
        foreach (uint af in new uint[] { 0, 1, 2 })
        {
            var setAf = await camera.SetRawPropertyAsync(LvAfSystem, af);
            await Task.Delay(900);
            await camera.DrainEventsAsync();
            var (_, nowAf) = await camera.GetRawPropertyAsync(LvAfSystem);
            var err3 = await camera.SetEvfZoomAsync(5);
            await Task.Delay(1200);
            var r = await Rect();
            Log($"  LvAfSystem->{af} ({setAf}, reads 0x{nowAf:X})  zoom5={err3}  rect {Show(r)}"
                + $"{(r is { } v && baseline is { } b && (v.W != b.W || v.H != b.H) ? "   <<< MOVED" : "")}");
            await camera.SetEvfZoomAsync(1);
            await Task.Delay(600);
        }

        // 3: with the gate open, which factors are real? The first sweep ran in the blocking AF mode
        // and so proved nothing about the factors themselves.
        Log("\n--- zoom factors, LvAfSystem=1 (gate open) ---");
        await camera.SetRawPropertyAsync(LvAfSystem, 1);
        await Task.Delay(900);
        foreach (uint z in new uint[] { 1, 2, 3, 4, 5, 6, 8, 10, 15 })
        {
            var err4 = await camera.SetEvfZoomAsync(z);
            await Task.Delay(1100);
            var r = await Rect();
            var factor = r is { } v && v.W > 0 ? $"{5472.0 / v.W:F2}x" : "?";
            Log($"  zoom {z,-3} = {err4,-10} rect {Show(r),-24} => {factor}");
        }

        // 4: panning. Only meaningful while zoomed, and the rect origin is the read-out that says
        // whether a coordinate landed where it was asked to.
        Log("\n--- pan at zoom 5 (rect origin is the instrument) ---");
        await camera.SetEvfZoomAsync(5);
        await Task.Delay(1100);
        foreach (var (x, y) in new (uint, uint)[] { (0, 0), (1000, 500), (2184, 1456), (4368, 2912), (9999, 9999) })
        {
            var errp = await camera.SetEvfZoomPositionAsync(x, y);
            await Task.Delay(900);
            var r = await Rect();
            var landed = r is { } v && v.X == x && v.Y == y ? "exact"
                : r is { } v2 ? $"clamped to ({v2.X},{v2.Y})" : "no frame";
            Log($"  asked ({x},{y}) = {errp,-10} rect {Show(r),-24} {landed}");
        }

        await camera.SetEvfZoomAsync(1);
        Log($"\nStopLiveView = {await camera.StopLiveViewAsync()}");
        return 0;
    }

    if (mode is "evf")
    {
        // Live-view magnification, panning and autofocus — plus the string properties, which are
        // read here because they are the cheapest possible check that the byte accessor works at
        // all, and one of them can be checked against a value from a different part of the firmware.
        Log("--- string properties (byte accessor) ---");
        foreach (var id in new[]
                 {
                     EdsPropertyId.LensName, EdsPropertyId.BodyIDEx, EdsPropertyId.OwnerName,
                     EdsPropertyId.Artist, EdsPropertyId.Copyright,
                 })
        {
            var (e, text) = await camera.GetPropertyStringAsync(id);
            var (bytesErr, bytes) = await camera.GetPropertyBytesAsync(id);
            Log($"  {id,-12} = {(e is EdsError.OK ? $"\"{text}\"" : e.ToString())}"
                + $"   [{bytes.Length} bytes{(bytesErr is EdsError.OK ? "" : $", {bytesErr}")}]");
        }
        // Two different identifiers, not two readings of one: 0xD1AF is the serial printed on the
        // body, GetDeviceInfo's is a 32-hex-digit internal id. Printed together so nobody re-derives
        // that the hard way.
        var (_, bodyId) = await camera.GetBodyIdAsync();
        Log($"  BodyIDEx (0xD1AF): \"{bodyId}\"   GetDeviceInfo serial: \"{camera.SerialNumber}\"");

        Log($"\n  0x9154 DoAf:         {camera.SupportsLiveViewAutoFocus}");
        Log($"  0x9158 Zoom:         {camera.SupportsEvfZoom}");
        Log($"  0x9159 ZoomPosition: {camera.SupportsEvfZoomPosition}");

        Log("\n--- live view ---");
        Log($"StartLiveView = {await camera.StartLiveViewAsync()}");
        await Task.Delay(1500);

        // A frame is only evidence if it has real bytes in it — a body that has stopped streaming
        // answers OK forever with a zero-length payload.
        async Task<int> Frames(string tag, int want = 3)
        {
            int got = 0;
            for (int i = 0; i < 25 && got < want; i++)
            {
                var (e, jpeg) = await camera!.GetLiveViewFrameAsync();
                if (e is EdsError.OK && jpeg.Length > 1000 && jpeg[0] == 0xFF && jpeg[1] == 0xD8)
                {
                    var path = Path.Combine(outDir, $"{tag}_{got}.jpg");
                    await File.WriteAllBytesAsync(path, jpeg);
                    if (got is 0) Log($"  {tag}: {jpeg.Length:N0} bytes -> {Path.GetFileName(path)}");
                    got++;
                }
                else await Task.Delay(120);
            }
            if (got is 0) Log($"  {tag}: NO FRAME — nothing below this line means anything");
            return got;
        }

        // The envelope's non-image records are where the zoom rect must live. Logging type+length at
        // each zoom level is what makes it identifiable: the record that changes when zoom changes.
        async Task Records(string tag)
        {
            var (e, records) = await camera!.GetLiveViewRecordsAsync();
            if (e is not EdsError.OK || records.Count is 0) { Log($"  {tag} records: none ({e})"); return; }
            foreach (var r in records)
            {
                var head = string.Join(' ', r.Payload.Span[..Math.Min(32, r.Payload.Length)].ToArray().Select(b => b.ToString("X2")));
                Log($"  {tag} record type={r.Type,-3} len={r.Payload.Length,-8} {(r.IsImage ? "(JPEG)" : head)}");
            }
        }

        await Frames("zoom1");
        await Records("zoom1");
        Log($"  rect: {await camera.GetEvfZoomRectAsync()}");

        // The guard, both ways round. LiveFace is the factory default and silently blocks
        // magnification, so the SDK must refuse rather than relay the camera's OK — and it must NOT
        // refuse once the AF method allows it. A test that only showed one direction would be
        // satisfied by a method that always refuses.
        Log("\n--- the AF-method gate ---");
        var (_, afWas) = await camera.GetEvfAfSystemAsync();
        var afAllowed = await camera.GetRawAllowedValuesAsync(0xD1BA);
        Log($"  Evf_AFMode as found: {afWas}");
        Log($"  Evf_AFMode allowed:  {(afAllowed is null ? "none announced" : string.Join(", ", afAllowed.Select(a => $"{(CanonEvfAfSystem)a}({a})")))}");
        // With a manual lens or a telescope there are no electronic contacts, so whether this
        // setting is even changeable is the thing to watch — the unlock depends on it entirely.
        Log($"  Lens: \"{(await camera.GetLensNameAsync()).Value}\"");

        foreach (var af in new[] { CanonEvfAfSystem.LiveFace, CanonEvfAfSystem.Live })
        {
            Log($"\n  SetEvfAfSystem({af}) = {await camera.SetEvfAfSystemAsync(af)}");
            await Task.Delay(900);
            await camera.DrainEventsAsync();
            var (_, afNow) = await camera.GetEvfAfSystemAsync();
            Log($"    reads back: {afNow}{(afNow == af ? "" : "  <<< DID NOT TAKE")}");

            var zoomErr = await camera.SetEvfZoomAsync(CanonEvfZoom.X5);
            var rect = await camera.GetEvfZoomRectAsync();
            Log($"    SetEvfZoom(X5) = {zoomErr}   rect {rect}");

            // Only one of these is a rule. Zoom in Live MUST work — if it does not, the feature is
            // broken. Whether LiveFace blocks it is lens-dependent: with an EF lens attached it is
            // silently ignored, with no lens (the telescope case) it magnifies anyway. So that row
            // is reported rather than asserted, and the SDK decides by verifying the rect instead of
            // by knowing this rule — which is why it gets both cases right without being told.
            if (af is CanonEvfAfSystem.Live)
            {
                Log($"    => must be OK: {(zoomErr is EdsError.OK ? "CORRECT" : "*** WRONG ***")}");
                if (zoomErr is not EdsError.OK) failures++;
            }
            else
            {
                Log($"    => lens-dependent, not asserted: this body "
                    + $"{(zoomErr is EdsError.OK ? "ALLOWED" : "BLOCKED")} zoom in {af}");
            }

            await Frames($"af_{af}_zoom5", want: 1);
            await camera.SetEvfZoomAsync(CanonEvfZoom.Fit, verify: false);
            await Task.Delay(700);
        }

        // Pan, with the gate open. The rect origin says where the crop actually landed; the camera
        // clamps silently to keep the crop on the sensor.
        Log("\n--- pan at 5x ---");
        await camera.SetEvfAfSystemAsync(CanonEvfAfSystem.Live);
        await Task.Delay(700);
        Log($"  zoom X5 = {await camera.SetEvfZoomAsync(CanonEvfZoom.X5)}");
        foreach (var (x, y) in new (uint, uint)[] { (0, 0), (2184, 1456), (4368, 2912) })
        {
            Log($"  SetEvfZoomPosition({x},{y}) = {await camera.SetEvfZoomPositionAsync(x, y)}");
            await Task.Delay(900);
            Log($"    landed: {await camera.GetEvfZoomRectAsync()}");
            await Frames($"pan_{x}x{y}", want: 1);
        }

        Log($"\n  back to 1x = {await camera.SetEvfZoomAsync(CanonEvfZoom.Fit)}");
        await Task.Delay(1000);
        await Frames("zoom1_again", want: 1);

        Log("\n--- live-view autofocus (0x9154) ---");
        camera.PropertyChanged += (_, e) => Log($"       evt prop {e.PropertyId} = 0x{e.Value:X}");
        camera.StateChanged += (_, e) => Log($"       evt state {e.EventType} param=0x{e.Param:X}");
        var afStart = clock.Elapsed;
        Log($"  AutoFocusLiveView = {await camera.AutoFocusLiveViewAsync()}  (+{(clock.Elapsed - afStart).TotalMilliseconds:F0} ms)");
        // AF returns on acceptance, not on focus — the outcome is on the event stream, and the frames
        // are the visual record of whether the image actually snapped into focus.
        for (int i = 0; i < 6; i++) { await Task.Delay(500); await camera.DrainEventsAsync(); }
        await Frames("after_af", want: 2);
        Log($"  AfCancel = {await camera.CancelAutoFocusAsync()}");

        Log($"\nStopLiveView = {await camera.StopLiveViewAsync()}");
        Log($"Frames written to {outDir}");
        return 0;
    }

    if (mode is "diag")
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


