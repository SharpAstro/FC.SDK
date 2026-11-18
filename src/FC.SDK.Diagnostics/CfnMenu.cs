using FC.SDK;
using FC.SDK.Canon;

/// <summary>
/// `cfnmenu`: writes a Custom Function remotely, draws the menu screen the camera should now show,
/// and asks the operator whether it does. Then writes the original back. Works on any body
/// <see cref="CanonCustomFunctionCatalog"/> has a table for.
/// </summary>
/// <remarks>
/// The other direction from `watch` and `cfnwalk`, where the operator changes the menu and the tool
/// reads. Here the tool changes and the operator reads, which checks two things at once: whether the
/// body takes a C.Fn write at all (a body can answer OK and keep its value, so every write is read
/// back), and whether a value means what the catalog says. Where the catalog is a guess, the
/// operator's "no, it shows ..." is the measurement.
/// </remarks>
internal static class CfnMenu
{
    // Values to try instead of the next option, where the next option would not answer the question.
    // A 6D's II-1 slider: 0xFFFFFFFF is -1 if the value is signed, a step the operator never set.
    private static readonly Dictionary<(string Model, uint Id), uint> Trials = new()
    {
        [("Canon EOS 6D", 0x0502)] = 0xFFFFFFFF,
    };

    public static async Task<int> RunAsync(CanonCamera camera, Action<string> log, TimeProvider time, string transcriptPath, int first, int rows, uint? value, int? forcedWord = null)
    {
        if (Console.IsInputRedirected)
        {
            log("cfnmenu needs someone at a terminal to look at the camera; stdin is redirected.");
            return 1;
        }
        var catalog = CanonCustomFunctionCatalog.For(camera.Model);
        if (catalog.Count == 0)
        {
            log($"No C.Fn table for {camera.Model}: map it first with `cfnwalk` or `watch`.");
            return 1;
        }

        // An entry with no options and no range (a 6D's Custom Controls) is not decoded: left out.
        var items = catalog
            .Select((info, row) => (Info: info, Row: row + 1))
            .Skip(Math.Max(0, first - 1)).Take(rows > 0 ? rows : int.MaxValue)
            .Where(i => i.Info.Range is not CanonCustomFunctionRange.None || i.Info.Options.Count > 0)
            .ToList();

        Console.OutputEncoding = System.Text.Encoding.UTF8;
        var transcript = new List<string> { $"cfnmenu on {camera.Model} ({camera.TransportName})" };
        void Note(string line) { log(line); transcript.Add(line); }

        Note("For each item: show it on the camera (the body sends that item's group), press Enter, look,");
        Note("answer. Leave the item's screen and open it again after each write, so the camera redraws it.");
        int refused = 0;
        var lists = new Dictionary<ushort, string>();
        if (value is { } v) Note($"--value {v} (0x{v:X}) is written{(forcedWord is { } w ? $" to word {w}" : "")} in place of each item's own trial.");
        try
        {
            foreach (var (info, row) in items)
            {
                var label = $"{info.MenuLabel} {info.Name} (0x{info.FunctionId:X4})";
                Console.WriteLine();
                Note($"==== row {row}: {label}");
                if (info.Note is { } note) Note($"  note: {note}");
                if (!Prompt($"Show {info.MenuLabel} \"{info.Name}\" on the camera, then press Enter (s skip, a abort)", out var go)) return Finish(1);
                if (go == "s") { Note("  skipped"); continue; }

                var original = await ReadAsync(info.FunctionId);
                if (original is null) { Note("  not in the block: is that item's group showing on the camera? skipped"); continue; }
                Note($"  now: {string.Join(",", original)}");
                if (info.Word >= original.Count) { Note($"  the entry has no word {info.Word}; skipped"); continue; }
                var current = original[info.Word];

                // --value replaces the trial, mask or not: a body that refuses a value can be asked
                // whether it takes another reading of it (a menu index where the read gave a count).
                // A range moves one limit at a time by a whole stop towards the other, word 0 then
                // word 1, and leaves words 2 and 3 alone: whether the body mirrors them is part of
                // what the read-back shows. A whole stop because an R6's range menus offer whole
                // stops only: a third-stop write was drawn as nonsense ("highest 15\"").
                IEnumerable<(int Word, uint Value)> trials = value is { } forced ? [(forcedWord ?? info.Word, forced)]
                    : info.Range is not CanonCustomFunctionRange.None
                        ? [(0, unchecked(original[0] - 768u)), (1, unchecked(original[1] + 768u))]
                    : info.IsMask ? Enumerable.Range(0, info.Options.Count).Select(b => (info.Word, 1u << b))
                    : [(info.Word, Trials.TryGetValue((camera.Model ?? "", info.FunctionId), out var t) ? t : NextValue(info, current))];
                lists = await ExposureLists.SnapshotAsync(camera);
                var written = new SortedSet<int>();

                foreach (var (word, trial) in trials)
                {
                    var (ok, readBack) = await WriteAsync(info, word, trial);
                    Note($"  wrote word {word} = {trial} ({unchecked((int)trial)}): read back {(readBack is null ? "nothing" : string.Join(",", readBack))}{(ok ? "" : "  <== the camera kept its old value")}");
                    if (!ok)
                    {
                        refused++;
                        // A range's two limits are separate questions: a refused one says nothing
                        // about the other.
                        if (info.Range is not CanonCustomFunctionRange.None) continue;
                        break;
                    }
                    written.Add(word);

                    Draw(info, trial, readBack!);
                    var question = info.IsMask
                        ? "Which boxes are ticked on the camera? Numbers like 2 or 1,3, or 0 for none (a abort)"
                        : "Does the camera show this? y / n (then type what it shows) / a abort";
                    if (!Prompt("  Leave the item and reopen it. " + question, out var answer)) return Finish(1);
                    if (answer == "n")
                    {
                        Prompt("  What does the camera show?", out var shows);
                        Note($"  operator: NO, the camera shows \"{shows}\"");
                    }
                    else Note($"  operator: {(info.IsMask ? $"ticked {answer} for bit value {trial}" : answer == "y" ? "yes, matches" : answer)}");
                }

                foreach (var word in written.Count > 0 ? written : [info.Word])
                {
                    var was = original[word];
                    var (restored, after) = await WriteAsync(info, word, was);
                    // The operator may have left the menu to check the body (dial clicks), and then
                    // the body no longer sends this item's group: ask for it back rather than give up.
                    while (!restored && await ReadAsync(info.FunctionId) is null)
                    {
                        if (!Prompt($"  To restore {was}, show {info.MenuLabel} on the camera again and press Enter (s skip)", out var again) || again == "s")
                            break;
                        (restored, after) = await WriteAsync(info, word, was);
                    }
                    Note($"  restored word {word} = {was}: read back {(after is null ? "nothing" : string.Join(",", after))}{(restored ? "" : "  <== NOT RESTORED, set it by hand")}");
                }
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

        async Task<(bool Kept, IReadOnlyList<uint>? ReadBack)> WriteAsync(CanonCustomFunctionInfo info, int word, uint value)
        {
            var (err, block) = await camera.GetCustomFunctionBlockAsync(refresh: true);
            if (err is not EdsError.OK || block is null || !block.SetValue(info.FunctionId, word, value)) return (false, null);
            var writeErr = await camera.SetCustomFunctionBlockAsync(block);
            if (writeErr is not EdsError.OK) { Note($"  write answered {writeErr}"); return (false, null); }
            // A body echoes a written value before deciding whether to keep it (see the 450D notes),
            // so the read-back waits out that window.
            await Task.Delay(TimeSpan.FromMilliseconds(2500), time);
            var back = await ReadAsync(info.FunctionId);
            // What an increment write really did shows here, not in the block (an R6's 0x0101).
            var now = await ExposureLists.SnapshotAsync(camera);
            foreach (var change in ExposureLists.Changes(lists, now)) Note("  " + change);
            lists = now;
            return (back is not null && back.Count > word && back[word] == value, back);
        }
    }

    /// <summary>The wire value of the option after the current one, in menu order; the first option when the current value is none of them.</summary>
    private static uint NextValue(CanonCustomFunctionInfo info, uint current)
    {
        var count = info.Options.Count;
        for (int i = 0; i < count; i++)
            if (info.ValueOf(i) == current) return info.ValueOf((i + 1) % count);
        return info.ValueOf(0);
    }

    private static bool Prompt(string text, out string answer)
    {
        if (OperatingSystem.IsWindows()) Console.Beep(1200, 120);
        Console.WriteLine(text);
        answer = Console.ReadLine()?.Trim().ToLowerInvariant() ?? "a";
        return answer != "a";
    }

    /// <summary>The camera's menu page, as text: header, item, and the options with the chosen one marked.</summary>
    private static void Draw(CanonCustomFunctionInfo info, uint value, IReadOnlyList<uint> readBack)
    {
        const int width = 46;
        string Row(string s) => "  │ " + (s.Length > width - 4 ? s[..(width - 5)] + "…" : s).PadRight(width - 4) + " │";
        var lines = new List<string>
        {
            "  ┌" + new string('─', width - 2) + "┐",
            Row(info.Group.PadRight(width - 10) + $"< {info.Number} >"),
            Row(info.Name + (info.Guess ? " (GUESS)" : "")),
            "  ├" + new string('─', width - 2) + "┤",
        };
        if (CanonCustomFunctionCatalog.FormatRange(info, readBack) is { } range)
            foreach (var part in range.Split(", "))
                lines.Add(Row(part));
        else if (info.IsMask)
            for (int b = 0; b < info.Options.Count; b++)
                lines.Add(Row($"[{((value >> b & 1) != 0 ? "x" : " ")}] {info.Options[b]}"));
        else if (info.OptionFor(value) is null)
            lines.Add(Row($"> wire value 0x{value:X} ({(int)value} signed): what does it show?"));
        else
            for (int o = 0; o < info.Options.Count; o++)
                lines.Add(Row($"{(info.ValueOf(o) == value ? ">" : " ")} {info.Options[o]}"));
        lines.Add("  └" + new string('─', width - 2) + "┘");
        foreach (var line in lines) Console.WriteLine(line);
    }
}
