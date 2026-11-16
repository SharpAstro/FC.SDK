using FC.SDK;
using FC.SDK.Canon;

/// <summary>
/// `cfnmenu`: writes a 6D Custom Function remotely, draws the menu screen the camera should now show,
/// and asks the operator whether it does. Then writes the original back.
/// </summary>
/// <remarks>
/// The other direction from `watch`, where the operator changes the menu and the tool reads. Here the
/// tool changes and the operator reads, which checks two things at once: whether a 6D takes a C.Fn
/// write at all (a body can answer OK and keep its value, so every write is read back), and whether
/// a value means what the table below says. Labels come from the 6D's own menu as read off the body on
/// 2026-11-16; where they are a guess the operator's "no, it shows ..." is the measurement.
/// </remarks>
internal static class CfnMenu
{
    private sealed record Item(string Group, int WireGroup, int Number, uint Id, string Name, int Word, string[]? Options, string[]? Boxes = null, uint? Trial = null);

    // 6D, menu order. Options are indexed by wire value; Boxes are a checkbox mask, bit 0 first.
    private static readonly Item[] Items =
    [
        new("C.Fn I: Exposure", 1, 1, 0x0101, "Exposure level increments", 0, ["1/3-stop", "1/2-stop"]),
        new("C.Fn I: Exposure", 1, 2, 0x0102, "ISO speed setting increments", 0, ["1/3-stop", "1-stop"]),
        new("C.Fn I: Exposure", 1, 3, 0x0104, "Bracketing auto cancel", 0, ["ON", "OFF"]),
        new("C.Fn I: Exposure", 1, 4, 0x0105, "Bracketing sequence", 0, ["0, -, +", "-, 0, +", "+, 0, -"]),
        new("C.Fn I: Exposure", 1, 5, 0x0106, "Number of bracketed shots", 0, ["3 shots", "2 shots", "5 shots", "7 shots"]),
        new("C.Fn I: Exposure", 1, 6, 0x0108, "Safety shift", 0, ["Disable", "Shutter speed/Aperture", "ISO speed"]),
        // II-1..II-4 are sliders in the menu, and the manual names their positions but not their wire
        // values: these four lists are guesses (only "+1 reads 1" for II-1 was seen), so the operator's
        // answer is the measurement. II-1 tries 0xFFFFFFFF, i.e. -1 if the value is signed.
        new("C.Fn II: Autofocus", 3, 1, 0x0502, "Tracking sensitivity (GUESS)", 0, ["0", "Responsive +1", "Responsive +2"], Trial: 0xFFFFFFFF),
        new("C.Fn II: Autofocus", 3, 2, 0x0518, "Accel./decel. tracking (GUESS)", 0, ["0", "+1", "+2"]),
        new("C.Fn II: Autofocus", 3, 3, 0x0519, "AI Servo 1st image priority (GUESS)", 0, ["Equal priority", "Release priority", "Focus priority"]),
        new("C.Fn II: Autofocus", 3, 4, 0x051A, "AI Servo 2nd image priority (GUESS)", 0, ["Equal priority", "Shooting speed priority", "Focus priority"]),
        // From here on the option lists are the manual's (eos6d-im7-en, p.304-313), value = index.
        new("C.Fn II: Autofocus", 3, 5, 0x050E, "AF-assist beam firing", 0, ["Enable", "Disable", "IR AF assist beam only"]),
        new("C.Fn II: Autofocus", 3, 6, 0x0505, "Lens drive when AF impossible", 0, ["Continue focus search", "Stop focus search"]),
        new("C.Fn II: Autofocus", 3, 7, 0x0516, "Orientation linked AF point", 0, ["Same for both vertical/horizontal", "Select different AF points"]),
        new("C.Fn II: Autofocus", 3, 8, 0x0510, "Superimposed display", 0, ["On", "Off"]),
        new("C.Fn III: Operation/Others", 4, 1, 0x0706, "Dial direction during Tv/Av", 0, ["Normal", "Reverse direction"]),
        new("C.Fn III: Operation/Others", 4, 2, 0x080B, "Focusing screen", 0, ["Eg-A II", "Eg-D", "Eg-S"]),
        new("C.Fn III: Operation/Others", 4, 3, 0x070F, "Multi function lock", 1, null, ["Main Dial", "Quick Control Dial", "Multi-controller"]),
        new("C.Fn III: Operation/Others", 4, 4, 0x040A, "Warnings in viewfinder", 0, null,
            ["When monochrome set", "When WB is corrected", "When ISO expansion is used", "When spot meter is set"]),
    ];

    public static async Task<int> RunAsync(CanonCamera camera, Action<string> log, TimeProvider time, string transcriptPath)
    {
        if (Console.IsInputRedirected)
        {
            log("cfnmenu needs someone at a terminal to look at the camera; stdin is redirected.");
            return 1;
        }
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        var transcript = new List<string> { $"cfnmenu on {camera.Model} ({camera.TransportName})" };
        void Note(string line) { log(line); transcript.Add(line); }

        Note("For each item: open it on the camera, press Enter, look, answer. Leave the item's screen");
        Note("(back to the C.Fn list) and open it again after each write, so the camera redraws it.");
        int refused = 0;
        try
        {
            foreach (var item in Items)
            {
                var label = $"{item.Group.Split(':')[0]}-{item.Number} {item.Name} (0x{item.Id:X4})";
                Console.WriteLine();
                Note($"==== {label}");
                if (!Prompt($"Open {item.Group.Split(':')[0]}-{item.Number} \"{item.Name}\" on the camera, then press Enter (s skip, a abort)", out var go)) return Finish(1);
                if (go == "s") { Note("  skipped"); continue; }

                var original = await ReadAsync(item.Id);
                if (original is null) { Note("  not in the block: is that group showing on the camera? skipped"); continue; }
                Note($"  now: {string.Join(",", original)}");
                var current = original[item.Word];

                IEnumerable<uint> trials = item.Boxes is { } boxes
                    ? Enumerable.Range(0, boxes.Length).Select(b => 1u << b)
                    : [item.Trial ?? (current + 1) % (uint)item.Options!.Length];

                foreach (var trial in trials)
                {
                    var (ok, readBack) = await WriteAsync(item, trial);
                    Note($"  wrote {trial}: read back {(readBack is null ? "nothing" : string.Join(",", readBack))}{(ok ? "" : "  <== the camera kept its old value")}");
                    if (!ok) { refused++; break; }

                    Draw(item, trial);
                    var question = item.Boxes is { } b2
                        ? $"Which boxes are ticked on the camera? Numbers like 2 or 1,3, or 0 for none (a abort)"
                        : "Does the camera show this? y / n (then type what it shows) / a abort";
                    if (!Prompt("  Leave the item and reopen it. " + question, out var answer)) return Finish(1);
                    if (answer == "n")
                    {
                        Prompt("  What does the camera show?", out var shows);
                        Note($"  operator: NO, the camera shows \"{shows}\"");
                    }
                    else Note($"  operator: {(item.Boxes is null ? (answer == "y" ? "yes, matches" : answer) : $"ticked {answer} for bit value {trial}")}");
                }

                var (restored, after) = await WriteAsync(item, current);
                Note($"  restored {current}: read back {(after is null ? "nothing" : string.Join(",", after))}{(restored ? "" : "  <== NOT RESTORED, set it by hand")}");
            }
            return Finish(0);
        }
        finally
        {
            await File.WriteAllLinesAsync(transcriptPath, transcript);
            log($"Transcript: {transcriptPath}");
        }

        int Finish(int code)
        {
            Note(refused > 0 ? $"{refused} write(s) the camera did not keep." : "Every write was kept.");
            return code;
        }

        async Task<IReadOnlyList<uint>?> ReadAsync(uint id)
        {
            var (err, block) = await camera.GetCustomFunctionBlockAsync(refresh: true);
            return err is EdsError.OK && block is not null && block.Entries.Any(e => e.FunctionId == id)
                ? block.Entries.First(e => e.FunctionId == id).Values : null;
        }

        async Task<(bool Kept, IReadOnlyList<uint>? ReadBack)> WriteAsync(Item item, uint value)
        {
            var (err, block) = await camera.GetCustomFunctionBlockAsync(refresh: true);
            if (err is not EdsError.OK || block is null || !block.SetValue(item.Id, item.Word, value)) return (false, null);
            var writeErr = await camera.SetCustomFunctionBlockAsync(block);
            if (writeErr is not EdsError.OK) { Note($"  write answered {writeErr}"); return (false, null); }
            // A body echoes a written value before deciding whether to keep it (see the 450D notes),
            // so the read-back waits out that window.
            await Task.Delay(TimeSpan.FromMilliseconds(2500), time);
            var back = await ReadAsync(item.Id);
            return (back is not null && back[item.Word] == value, back);
        }
    }

    private static bool Prompt(string text, out string answer)
    {
        if (OperatingSystem.IsWindows()) Console.Beep(1200, 120);
        Console.WriteLine(text);
        answer = Console.ReadLine()?.Trim().ToLowerInvariant() ?? "a";
        return answer != "a";
    }

    /// <summary>The camera's menu page, as text: header, item, and the options with the chosen one marked.</summary>
    private static void Draw(Item item, uint value)
    {
        const int width = 46;
        string Row(string s) => "  │ " + (s.Length > width - 4 ? s[..(width - 5)] + "…" : s).PadRight(width - 4) + " │";
        var lines = new List<string>
        {
            "  ┌" + new string('─', width - 2) + "┐",
            Row($"{item.Group}".PadRight(width - 10) + $"< {item.Number} >"),
            Row(item.Name),
            "  ├" + new string('─', width - 2) + "┤",
        };
        if (item.Boxes is { } boxes)
            for (int b = 0; b < boxes.Length; b++)
                lines.Add(Row($"[{((value >> b & 1) != 0 ? "x" : " ")}] {boxes[b]}"));
        else if (value >= item.Options!.Length)
            lines.Add(Row($"> wire value 0x{value:X} ({(int)value} signed): what does it show?"));
        else
            for (int o = 0; o < item.Options.Length; o++)
                lines.Add(Row($"{(o == value ? ">" : " ")} {o}: {item.Options[o]}"));
        lines.Add("  └" + new string('─', width - 2) + "┘");
        foreach (var line in lines) Console.WriteLine(line);
    }
}
