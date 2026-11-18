using System.Collections.Concurrent;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using FC.SDK;
using FC.SDK.Canon;

/// <summary>
/// `cfnwalk`: walks the operator through every value of every Custom Function item, in menu order,
/// and files whatever the body announces under the step that was showing when it arrived.
/// </summary>
/// <remarks>
/// `watch` with a script: the operator sets one value on the body, the tool shows and keeps what
/// comes in (property changes, state events, the C.Fn block diffed), the operator validates, the next
/// step comes up. Nothing is written to the camera. The menu table is the operator's (EOS R6, read off
/// the body on 2026-11-17) and carries no wire ids: finding them is what the run is for.
/// </remarks>
internal static class CfnWalk
{
    private enum Kind { Option, Check, Range }

    private sealed record Row(int Group, int Number, string Name, Kind Kind, string[] Values)
    {
        public string Label => $"C.Fn{Group}-{Number}";
    }

    // EOS R6, menu order. Left out: 3-3 Customize buttons and 3-4 Customize dials (screens with
    // too many options for this walk), 3-5 and 5-1 (Clear: actions, and OK would reset what is
    // being measured).
    private static readonly Row[] EosR6 =
    [
        new(1, 1, "Exposure level increments", Kind.Option, ["1/3-stop (1/3)", "1/2-stop (1/2)"]),
        new(1, 2, "ISO speed setting increments", Kind.Option, ["1/3-stop (1/3)", "1-stop (1/1)"]),
        new(1, 3, "Speed from metering/ISO Auto", Kind.Option, ["Restore Auto after metering", "Retain speed after metering"]),
        new(1, 4, "Bracketing auto cancel", Kind.Option, ["Enable (ON)", "Disable (OFF)"]),
        new(1, 5, "Bracketing sequence", Kind.Option, ["0,-,+ (0-+)", "-,0,+ (-0+)", "+,0,- (+0-)"]),
        new(1, 6, "Number of bracketed shots", Kind.Option, ["3 shots (3)", "2 shots (2)", "5 shots (5)", "7 shots (7)"]),
        new(1, 7, "Safety shift", Kind.Option, ["Disable (OFF)", "Shutter speed/Aperture (Tv/Av)", "ISO speed (ISO)"]),
        new(2, 1, "Same expo. for new aperture", Kind.Option, ["Disable (OFF)", "ISO speed (ISO)", "ISO speed/Shutter speed (ISO/Tv)", "Shutter speed (Tv)"]),
        new(2, 2, "AE lock meter. mode after focus", Kind.Check, ["Evaluative metering", "Partial metering", "Spot metering", "Center-weighted average"]),
        new(2, 3, "Set shutter speed range", Kind.Range, ["Lowest speed (30 sec.-1/4000 sec.)", "Highest speed (1/8000 sec.-15 sec.)"]),
        new(2, 4, "Set aperture range", Kind.Range, ["Max. aperture (f/1.0-f/64)", "Min. aperture (f/91-f/1.4)"]),
        new(3, 1, "Dial direction during Tv/Av", Kind.Option, ["Normal (- to +)", "Reverse direction (+ to -)"]),
        new(3, 2, "Control ring rotation", Kind.Option, ["Normal (- to +)", "Reverse direction (+ to -)"]),
        new(4, 1, "Add cropping information", Kind.Option,
            ["Disable (OFF)", "Aspect ratio 6:6", "Aspect ratio 3:4", "Aspect ratio 4:5", "Aspect ratio 6:7", "Aspect ratio 5:6", "Aspect ratio 5:7"]),
        new(4, 2, "Audio compression", Kind.Option, ["Enable (ON)", "Disable (OFF)"]),
        new(4, 3, "Default Erase option", Kind.Option, ["[Cancel] selected", "[Erase] selected", "[Erase RAW] selected", "[Erase non-RAW] selected"]),
        new(4, 4, "Release shutter w/o lens", Kind.Option, ["Disable (OFF)", "Enable (ON)"]),
        new(4, 5, "Retract lens on power off", Kind.Option, ["Enable (ON)", "Disable (OFF)"]),
        new(4, 6, "Add IPTC information", Kind.Option, ["Disable (OFF)", "Enable (ON)"]),
    ];

    private sealed record Step(Row Row, int Index)
    {
        public string Value => Row.Values[Index];

        public string Instruction => Row.Kind switch
        {
            Kind.Check => $"tick ONLY \"{Value}\"",
            Kind.Range => $"change \"{Value}\" (any new value; you will be asked which)",
            _ => $"set \"{Value}\"",
        };
    }

    /// <summary>One thing the body announced, filed under the step showing when it arrived.</summary>
    private sealed record Datum(long At, string Kind, string Text)
    {
        public ushort? Code { get; init; }
        public uint? Value { get; init; }
        public uint? Group { get; init; }
        public int? Entry { get; init; }
        public uint? FunctionId { get; init; }
        public string? Was { get; init; }
        public string? Now { get; init; }
        public CanonEventType? Event { get; init; }

        /// <summary>How many identical announcements arrived in the same poll, shown as one line.</summary>
        public int Count { get; init; } = 1;

        public string Line => Count > 1 ? $"{Text}  x{Count}" : Text;
    }

    private static readonly (ushort Code, EdsPropertyId Id)[] Watched = ExposureLists.Watched;

    public static async Task<int> RunAsync(CanonCamera camera, Action<string> log, TimeProvider time,
        string outDir, int first, int rows)
    {
        if (Console.IsInputRedirected)
        {
            log("cfnwalk needs someone at the camera and at this terminal; stdin is redirected.");
            return 1;
        }
        Console.OutputEncoding = Encoding.UTF8;
        if (camera.Model != "Canon EOS R6")
            log($"The menu table is the EOS R6's; this is a {camera.Model}. The steps will not match its menu.");

        var selected = EosR6.Skip(Math.Max(0, first - 1)).Take(rows > 0 ? rows : int.MaxValue).ToArray();
        var steps = selected.SelectMany(r => r.Values.Select((_, i) => new Step(r, i))).ToArray();
        if (steps.Length == 0) { log("No row selected."); return 1; }

        var textPath = Path.Combine(outDir, "cfnwalk.txt");
        var jsonPath = Path.Combine(outDir, "cfnwalk.jsonl");
        var text = new List<string> { $"cfnwalk on {camera.Model} ({camera.TransportName}), rows {first}..{first + selected.Length - 1}" };

        // Events arrive on the poller's thread; only this loop prints, so a line never lands inside
        // the step header. 0xD1A0 is left out: every refresh below makes the body re-emit it, and the
        // block diff is the useful form of it.
        var incoming = new ConcurrentQueue<Datum>();
        long stepStart = time.GetTimestamp();
        long Now() => time.GetTimestamp();
        double T(Datum d) => (d.At - stepStart) / (double)time.TimestampFrequency;
        camera.PropertyChanged += (_, e) =>
        {
            if (e.PtpCode == 0xD1A0) return;
            var name = e.IsMapped ? e.PropertyId.ToString() : "(unmapped)";
            incoming.Enqueue(new Datum(Now(), "prop", $"prop 0x{e.PtpCode:X4} {name} = 0x{e.Value:X8} ({e.Value})")
                { Code = e.PtpCode, Value = e.Value });
        };
        camera.StateChanged += (_, e) =>
            incoming.Enqueue(new Datum(Now(), "state", $"state {e.EventType} param=0x{e.Param:X}") { Value = e.Param, Event = e.EventType });

        // Read in this loop, never in the handlers above: those run on the poller's thread, and a
        // list not yet cached is fetched by draining events.
        var lastAllowed = new Dictionary<ushort, string>();
        static string Format(uint[]? values) => ExposureLists.Format(values);
        async Task<List<Datum>> AllowedChangesAsync(IEnumerable<Datum> batch)
        {
            var changes = new List<Datum>();
            var codes = batch.Where(d => d.Event is CanonEventType.AllowedValuesChanged).Select(d => (ushort)d.Value!.Value).ToHashSet();
            foreach (var (code, id) in Watched.Where(w => codes.Contains(w.Code)))
            {
                var now = Format(await camera.GetAllowedValuesAsync(id));
                if (lastAllowed.TryGetValue(code, out var was) && was == now) continue;
                lastAllowed[code] = now;
                changes.Add(new Datum(Now(), "allowed", $"allowed 0x{code:X4} {id} now {now}") { Code = code, Was = was, Now = now });
            }
            return changes;
        }

        Dictionary<(uint Group, uint Id), (int Entry, string Values)>? lastCfn = null;
        async Task DiffCfnAsync()
        {
            var (err, block) = await camera.GetCustomFunctionBlockAsync(refresh: true);
            if (err is not EdsError.OK || block is null)
            {
                incoming.Enqueue(new Datum(Now(), "cfn-error", $"C.Fn block: {err}"));
                return;
            }
            var now = block.Entries.ToDictionary(e => (e.GroupId, e.FunctionId), e => (e.MenuNumber, string.Join(",", e.Values)));
            if (lastCfn is not null)
            {
                foreach (var ((g, id), (entry, v)) in now)
                {
                    if (!lastCfn.TryGetValue((g, id), out var was))
                        incoming.Enqueue(Cfn("cfn-new", $"C.Fn NEW     group {g} entry {entry} 0x{id:X4} = {v}", g, entry, id, null, v));
                    else if (was.Values != v)
                        incoming.Enqueue(Cfn("cfn-changed", $"C.Fn CHANGED group {g} entry {entry} 0x{id:X4}: {was.Values} -> {v}", g, entry, id, was.Values, v));
                }
                foreach (var ((g, id), (entry, v)) in lastCfn.Where(kv => !now.ContainsKey(kv.Key)))
                    incoming.Enqueue(Cfn("cfn-gone", $"C.Fn GONE    group {g} entry {entry} 0x{id:X4} (was {v})", g, entry, id, v, null));
            }
            else
            {
                text.Add($"C.Fn block at start, {block.RawData.Length} bytes:");
                foreach (var ((g, id), (entry, v)) in now) text.Add($"  group {g} entry {entry} 0x{id:X4} = {v}");
                foreach (var line in text.Skip(1)) log(line);
            }
            lastCfn = now;

            Datum Cfn(string kind, string line, uint g, int entry, uint id, string? was, string? v) =>
                new(Now(), kind, line) { Group = g, Entry = entry, FunctionId = id, Was = was, Now = v };
        }

        await DiffCfnAsync();
        text.Add("Allowed lists at start:");
        foreach (var (code, id) in Watched)
        {
            lastAllowed[code] = Format(await camera.GetAllowedValuesAsync(id));
            text.Add($"  0x{code:X4} {id} {lastAllowed[code]}");
        }
        foreach (var line in text.TakeLast(Watched.Length + 1)) log(line);
        log("");
        log("Press Enter to start.");
        Console.ReadLine();
        // What came in before the first step belongs to none.
        while (incoming.TryDequeue(out var early)) text.Add($"before step 1: {early.Text}");

        try
        {
            for (int s = 0; s < steps.Length; s++)
            {
                var step = steps[s];
                var data = new List<Datum>();
                stepStart = time.GetTimestamp();
                Header(step, s, steps.Length);
                // Whatever came in between two steps (the operator still moving) is kept under the new
                // step with a negative time, so it reads as having come before the header.
                var refreshAt = TimeSpan.Zero;
                string outcome;
                string? note = null;
                while (true)
                {
                    var batch = new List<Datum>();
                    while (incoming.TryDequeue(out var d)) batch.Add(d);
                    if (batch.Count > 0)
                    {
                        batch.AddRange(await AllowedChangesAsync(batch));
                        // A body repeats itself (AllowedValuesChanged 0xD138/0xD1D5 come in pairs, over
                        // and over): one line per distinct announcement in a poll, with its count.
                        foreach (var g in batch.GroupBy(d => d.Text))
                        {
                            var one = g.First() with { Count = g.Count() };
                            data.Add(one);
                            Console.WriteLine($"  {T(one),6:0.0}s  {one.Line}");
                        }
                    }
                    if (Console.KeyAvailable)
                    {
                        var key = Console.ReadKey(intercept: true);
                        if (key.Key == ConsoleKey.Enter) { outcome = "validated"; break; }
                        if (key.KeyChar is 's' or 'S') { outcome = "skipped"; break; }
                        if (key.KeyChar is 'a' or 'A') { outcome = "aborted"; break; }
                        if (key.KeyChar is 'n' or 'N')
                        {
                            Console.Write("  note: ");
                            note = Join(note, Console.ReadLine());
                            continue;
                        }
                        if (key.KeyChar is 'r' or 'R')
                        {
                            Record(step, "redone", data, note);
                            data.Clear();
                            note = null;
                            stepStart = time.GetTimestamp();
                            refreshAt = TimeSpan.Zero;
                            Header(step, s, steps.Length);
                            continue;
                        }
                    }
                    if (time.GetElapsedTime(stepStart) >= refreshAt)
                    {
                        // A menu change may or may not be announced; asking every second finds it
                        // either way, and the prop lines show whether it was.
                        await DiffCfnAsync();
                        refreshAt = time.GetElapsedTime(stepStart) + TimeSpan.FromSeconds(1);
                    }
                    await Task.Delay(TimeSpan.FromMilliseconds(100), time);
                }

                if (outcome == "validated" && step.Row.Kind == Kind.Range)
                {
                    Console.Write($"  Which value did you set for \"{step.Value}\"? ");
                    note = Join(note, "set to " + Console.ReadLine());
                }
                if (outcome == "validated" && !data.Any(d => d.Kind.StartsWith("cfn-", StringComparison.Ordinal)))
                    Console.WriteLine("  (no C.Fn change was seen for this step: was the camera already on this value?)");
                Record(step, outcome, data, note);
                if (outcome == "aborted") return 1;
            }
            Console.WriteLine();
            log("Every step done.");
            return 0;
        }
        finally
        {
            log($"Transcript: {textPath}");
            log($"Data:       {jsonPath}");
        }

        void Header(Step step, int s, int total)
        {
            const int width = 72;
            Console.WriteLine();
            Console.WriteLine(new string('═', width));
            Console.WriteLine($" Step {s + 1}/{total}   {step.Row.Label}  {step.Row.Name}");
            Console.WriteLine($" On the camera: {step.Instruction}   [value {step.Index + 1} of {step.Row.Values.Length}]");
            Console.WriteLine(new string('─', width));
            Console.WriteLine(" Enter validate · r redo · n note · s skip · a abort");
            Console.WriteLine(" Incoming:");
            if (OperatingSystem.IsWindows()) Console.Beep(1200, 120);
        }

        void Record(Step step, string outcome, List<Datum> data, string? note)
        {
            text.Add("");
            text.Add($"{step.Row.Label} {step.Row.Name} : {step.Value}  -> {outcome}{(note is null ? "" : $"  (note: {note})")}");
            text.AddRange(data.Count == 0 ? ["  nothing"] : data.Select(d => $"  {T(d),6:0.0}s  {d.Line}"));
            File.WriteAllLines(textPath, text);

            using var buffer = new MemoryStream();
            using (var w = new Utf8JsonWriter(buffer, new JsonWriterOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
            {
                w.WriteStartObject();
                w.WriteNumber("group", step.Row.Group);
                w.WriteNumber("item", step.Row.Number);
                w.WriteString("name", step.Row.Name);
                w.WriteString("kind", step.Row.Kind.ToString());
                w.WriteNumber("valueIndex", step.Index);
                w.WriteString("value", step.Value);
                w.WriteString("outcome", outcome);
                if (note is not null) w.WriteString("note", note);
                w.WriteStartArray("data");
                foreach (var d in data)
                {
                    w.WriteStartObject();
                    w.WriteNumber("t", Math.Round(T(d), 2));
                    w.WriteString("kind", d.Kind);
                    if (d.Count > 1) w.WriteNumber("count", d.Count);
                    if (d.Code is { } code) w.WriteString("code", $"0x{code:X4}");
                    if (d.Value is { } value) w.WriteNumber("value", value);
                    if (d.Group is { } g) w.WriteNumber("cfnGroup", g);
                    if (d.Entry is { } entry) w.WriteNumber("cfnEntry", entry);
                    if (d.FunctionId is { } id) w.WriteString("functionId", $"0x{id:X4}");
                    if (d.Was is not null) w.WriteString("was", d.Was);
                    if (d.Now is not null) w.WriteString("now", d.Now);
                    w.WriteString("text", d.Text);
                    w.WriteEndObject();
                }
                w.WriteEndArray();
                w.WriteEndObject();
            }
            File.AppendAllText(jsonPath, Encoding.UTF8.GetString(buffer.ToArray()) + Environment.NewLine);
        }

        static string Join(string? a, string? b) => string.IsNullOrWhiteSpace(a) ? b ?? "" : $"{a}; {b}";
    }
}
