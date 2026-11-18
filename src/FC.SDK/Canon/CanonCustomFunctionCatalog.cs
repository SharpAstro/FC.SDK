namespace FC.SDK.Canon;

/// <summary>
/// What a Custom Function entry means on a body that has been measured: its menu place, its name,
/// and what each value shows. Only verified bodies are listed; for any other the answer is null,
/// under the same never-guess rule as <see cref="CanonCustomFunctionId"/>.
/// </summary>
/// <remarks>
/// The 6D table comes from changing each item on the body with <c>FC.SDK.Diagnostics watch</c>
/// running (2026-11-16) and from the 6D manual's option lists (eos6d-im7-en, p.304-313), which give
/// a value's meaning as its index. The four AF sliders are marked <see cref="CanonCustomFunctionInfo.Guess"/>:
/// the manual names their positions but not their numbers. The EOS R6 table comes from setting every
/// value of every item on the body with <c>FC.SDK.Diagnostics cfnwalk</c> running (2026-11-17). See
/// <c>docs/canon-custom-functions.md</c>.
/// </remarks>
public static class CanonCustomFunctionCatalog
{
    private const string Exposure = "C.Fn I: Exposure";
    private const string Autofocus = "C.Fn II: Autofocus";
    private const string Operation = "C.Fn III: Operation/Others";

    private static readonly CanonCustomFunctionInfo[] Eos6D =
    [
        new(Exposure, 1, 0x0101, "Exposure level increments", 0, ["1/3-stop", "1/2-stop"]),
        new(Exposure, 2, 0x0102, "ISO speed setting increments", 0, ["1/3-stop", "1-stop"]),
        new(Exposure, 3, 0x0104, "Bracketing auto cancel", 0, ["On", "Off"]),
        new(Exposure, 4, 0x0105, "Bracketing sequence", 0, ["0, -, +", "-, 0, +", "+, 0, -"]),
        // Measured 2026-11-17 with `watch`: 3,0 -> 2,1 -> 5,2 -> 7,3. The menu's own index is word 1.
        new(Exposure, 5, 0x0106, "Number of bracketed shots", 0, ["3 shots", "2 shots", "5 shots", "7 shots"])
        {
            OptionValues = [3, 2, 5, 7],
            Note = "Two words: the number of shots, then its place in the menu (0..3). A write that changes only one of them has not been tried.",
        },
        new(Exposure, 6, 0x0108, "Safety shift", 0, ["Disable", "Shutter speed/Aperture", "ISO speed"]),
        new(Autofocus, 1, 0x0502, "Tracking sensitivity", 0, ["0", "+1", "+2"]) { Guess = true },
        new(Autofocus, 2, 0x0518, "Acceleration/deceleration tracking", 0, ["0", "+1", "+2"]) { Guess = true },
        new(Autofocus, 3, 0x0519, "AI Servo 1st image priority", 0, ["Equal priority", "Release priority", "Focus priority"]) { Guess = true },
        new(Autofocus, 4, 0x051A, "AI Servo 2nd image priority", 0, ["Equal priority", "Shooting speed priority", "Focus priority"]) { Guess = true },
        new(Autofocus, 5, 0x050E, "AF-assist beam firing", 0, ["Enable", "Disable", "IR AF assist beam only"]),
        new(Autofocus, 6, 0x0505, "Lens drive when AF impossible", 0, ["Continue focus search", "Stop focus search"]),
        new(Autofocus, 7, 0x0516, "Orientation linked AF point", 0, ["Same for both vertical/horizontal", "Select different AF points"]),
        new(Autofocus, 8, 0x0510, "Superimposed display", 0, ["On", "Off"]),
        new(Autofocus, 9, 0x0507, "AF Microadjustment", 0, ["Disable", "All by same amount", "Adjust by lens"]) { Guess = true },
        new(Operation, 1, 0x0706, "Dial direction during Tv/Av", 0, ["Normal", "Reverse direction"]),
        new(Operation, 2, 0x080B, "Focusing screen", 0, ["Eg-A II", "Eg-D", "Eg-S"]),
        new(Operation, 3, 0x070F, "Multi function lock", 1, ["Main Dial", "Quick Control Dial", "Multi-controller"]) { IsMask = true },
        new(Operation, 4, 0x040A, "Warnings in viewfinder", 0,
            ["When monochrome set", "When WB is corrected", "When ISO expansion is used", "When spot metering is set"]) { IsMask = true, Guess = true },
        new(Operation, 5, 0x070C, "Custom Controls", 0, []),
    ];

    // The 450D numbers its items 1..13 across all four groups ("C.Fn-9"), not per group. Ids from a
    // real dump (docs/canon-custom-functions.md), options from its manual (CUG_EOS450D, p.154-159).
    // Same id as on a 6D does not mean same options: 0x050E's third value differs.
    private static readonly CanonCustomFunctionInfo[] Eos450D =
    [
        new("C.Fn I: Exposure", 1, 0x0101, "Exposure level increments", 0, ["1/3-stop", "1/2-stop"]) { NumberedAcrossGroups = true },
        new("C.Fn I: Exposure", 2, 0x010F, "Flash sync. speed in Av mode", 0, ["Auto", "1/200 sec. (fixed)"]) { NumberedAcrossGroups = true },
        new("C.Fn II: Image", 3, 0x0201, "Long exposure noise reduction", 0, ["Off", "Auto", "On"]) { NumberedAcrossGroups = true },
        new("C.Fn II: Image", 4, 0x0202, "High ISO speed noise reduction", 0, ["Off", "On"]) { NumberedAcrossGroups = true },
        new("C.Fn II: Image", 5, 0x0203, "Highlight tone priority", 0, ["Disable", "Enable"]) { NumberedAcrossGroups = true },
        new("C.Fn II: Image", 6, 0x0204, "Auto Lighting Optimizer", 0, ["Enable", "Disable"]) { NumberedAcrossGroups = true },
        new("C.Fn III: Auto focus/Drive", 7, 0x050E, "AF-assist beam firing", 0, ["Enable", "Disable", "Only external flash emits"]) { NumberedAcrossGroups = true },
        new("C.Fn III: Auto focus/Drive", 8, 0x0511, "AF during Live View shooting", 0, ["Disable", "Quick mode", "Live mode"]) { NumberedAcrossGroups = true },
        new("C.Fn III: Auto focus/Drive", 9, 0x060F, "Mirror lockup", 0, ["Disable", "Enable"]) { NumberedAcrossGroups = true },
        new("C.Fn IV: Operation/Others", 10, 0x0701, "Shutter/AE lock button", 0,
            ["AF/AE lock", "AE lock/AF", "AF/AF lock, no AE lock", "AE/AF, no AE lock"]) { NumberedAcrossGroups = true },
        new("C.Fn IV: Operation/Others", 11, 0x0704, "SET button when shooting", 0,
            ["Normal (disabled)", "Change quality", "Flash exposure compensation", "LCD monitor On/Off", "Menu display"]) { NumberedAcrossGroups = true },
        new("C.Fn IV: Operation/Others", 12, 0x0811, "LCD display when power ON", 0, ["Display", "Retain power OFF status"]) { NumberedAcrossGroups = true },
        new("C.Fn IV: Operation/Others", 13, 0x080F, "Add original decision data", 0, ["Off", "On"]) { NumberedAcrossGroups = true },
    ];

    // The R6's menu calls its groups C.Fn1..C.Fn4; no heading is added, since none was read off the
    // body. Every value below was set on the body and seen on the wire. Left out: 1-3 "Speed from
    // metering/ISO Auto" and 4-2 "Audio compression", which change nothing in 0xD1A0, and 3-3/3-4
    // (Customize buttons/dials), which send nothing either. The wire groups do not follow the menu's:
    // 1-1, 1-7 and 2-1 come in group 1, 1-4..1-6 in group 2, 2-2..2-4 in group 3, all of C.Fn3 and
    // C.Fn4 in group 5, each group's entries in ascending id order.
    private const string Shared0101 =
        "Shared with the other increment item: 0x0101 holds whichever of the two was set last, so it says "
        + "nothing about the other one. The Tv and ISO allowed lists do (a 1/2-stop Tv list steps by 4, a "
        + "1-stop ISO list by 8), until something writes 0x0101. Do not write it: a write of 1 moved the "
        + "Av/Tv lists to 1/2-stop while the body stayed at 1/3 (its menu, and 3 dial clicks per stop), "
        + "and the ISO increment never followed any write.";

    private static readonly CanonCustomFunctionInfo[] EosR6 =
    [
        new("C.Fn1", 1, 0x0101, "Exposure level increments", 0, ["1/3-stop", "1/2-stop"]) { OptionValues = [0, 1], Note = Shared0101 },
        new("C.Fn1", 2, 0x0101, "ISO speed setting increments", 0, ["1/3-stop", "1-stop"]) { OptionValues = [0, 2], Note = Shared0101 },
        new("C.Fn1", 4, 0x0104, "Bracketing auto cancel", 0, ["On", "Off"]),
        new("C.Fn1", 5, 0x0105, "Bracketing sequence", 0, ["0, -, +", "-, 0, +", "+, 0, -"]),
        new("C.Fn1", 6, 0x0106, "Number of bracketed shots", 0, ["3 shots", "2 shots", "5 shots", "7 shots"])
        {
            OptionValues = [3, 2, 5, 7],
            Note = "The first word is the number of shots itself, not its place in the menu; the second read 0 throughout.",
        },
        new("C.Fn1", 7, 0x0108, "Safety shift", 0, ["Disable", "Shutter speed/Aperture", "ISO speed"]),
        // Bit 0 ISO, bit 1 Tv: the menu lists ISO/Tv third and Tv fourth, the wire the other way round.
        new("C.Fn2", 1, 0x0112, "Same expo. for new aperture", 0, ["Disable", "ISO speed", "Shutter speed", "ISO speed/Shutter speed"]),
        new("C.Fn2", 2, 0x0114, "AE lock meter. mode after focus", 0,
            ["Evaluative metering", "Partial metering", "Spot metering", "Center-weighted average"]) { IsMask = true },
        new("C.Fn2", 3, 0x010C, "Set shutter speed range", 0, [])
        {
            Range = CanonCustomFunctionRange.ShutterSpeed,
            Note = "Words 2 and 3 repeat words 0 and 1; the body copies a write to either itself. The menu offers "
                + "whole stops only, at either exposure increment; a third-stop value written to it was stored and drawn as nonsense.",
        },
        new("C.Fn2", 4, 0x010D, "Set aperture range", 0, [])
        {
            Range = CanonCustomFunctionRange.Aperture,
            Note = "Words 2 and 3 are the set range clamped to the lens on the body (the set range itself with no "
                + "lens), on the same scale; measured with two lenses and none. The menu offers whole "
                + "stops only, at either exposure increment; a third-stop value written to it was stored and drawn as nonsense.",
        },
        new("C.Fn3", 1, 0x0706, "Dial direction during Tv/Av", 0, ["Normal", "Reverse direction"]),
        new("C.Fn3", 2, 0x0712, "Control ring rotation", 0, ["Normal", "Reverse direction"]),
        new("C.Fn4", 1, 0x080E, "Add cropping information", 0,
            ["Disable", "Aspect ratio 6:6", "Aspect ratio 3:4", "Aspect ratio 4:5", "Aspect ratio 6:7", "Aspect ratio 5:6", "Aspect ratio 5:7"]),
        new("C.Fn4", 3, 0x0813, "Default Erase option", 0, ["[Cancel] selected", "[Erase] selected", "[Erase RAW] selected", "[Erase non-RAW] selected"]),
        new("C.Fn4", 4, 0x0711, "Release shutter w/o lens", 0, ["Disable", "Enable"]),
        new("C.Fn4", 5, 0x0814, "Retract lens on power off", 0, ["Enable", "Disable"]),
        new("C.Fn4", 6, 0x0815, "Add IPTC information", 0, ["Disable", "Enable"]),
    ];

    /// <summary>Every entry known for <paramref name="model"/>, in menu order; empty for an unmeasured body.</summary>
    public static IReadOnlyList<CanonCustomFunctionInfo> For(string? model) => model switch
    {
        "Canon EOS 6D" => Eos6D,
        "Canon EOS R6" => EosR6,
        not null when CanonCustomFunctionId.Is450DFamily(model) => Eos450D,
        _ => [],
    };

    /// <summary>One entry's meaning on <paramref name="model"/>, or null when it has not been measured.</summary>
    /// <remarks>Where two menu items share an id (an R6's <c>0x0101</c>), this is the first of them.</remarks>
    public static CanonCustomFunctionInfo? Describe(string? model, uint functionId) =>
        For(model).FirstOrDefault(i => i.FunctionId == functionId);

    /// <summary>
    /// The entry's meaning given the value it holds, where two menu items share an id: the one item that
    /// has an option for <paramref name="value"/>; null when both have one, since the value then cannot
    /// say which item was set; the first of them when neither has.
    /// </summary>
    /// <remarks>
    /// An R6's <c>0x0101</c>: 1 can only be the exposure item at 1/2-stop and 2 only the ISO item at 1-stop,
    /// but 0 is 1/3-stop for either.
    /// </remarks>
    public static CanonCustomFunctionInfo? Describe(string? model, uint functionId, uint value)
    {
        CanonCustomFunctionInfo? first = null, match = null;
        var matches = 0;
        foreach (var info in For(model))
        {
            if (info.FunctionId != functionId) continue;
            first ??= info;
            if (info.OptionFor(value) is null) continue;
            match ??= info;
            matches++;
        }
        return matches switch { 0 => first, 1 => match, _ => null };
    }

    // One per stop, from 30 s (Tv -5) to 1/8000 (Tv 13), as a Canon menu prints them.
    private static readonly string[] ShutterStops =
    [
        "30\"", "15\"", "8\"", "4\"", "2\"", "1\"", "1/2", "1/4", "1/8", "1/15",
        "1/30", "1/60", "1/125", "1/250", "1/500", "1/1000", "1/2000", "1/4000", "1/8000",
    ];

    private const int SlowestShutterStop = -5;

    // One per stop, from f/1.0 (Av 0) to f/91 (Av 13).
    private static readonly string[] ApertureStops =
    [
        "1.0", "1.4", "2.0", "2.8", "4.0", "5.6", "8", "11", "16", "22", "32", "45", "64", "91",
    ];

    /// <summary>
    /// One word of a range entry as the menu shows it (<c>1/4000</c>, <c>15"</c>, <c>f/1.4</c>), or null when it is
    /// not a whole stop between 30 s and 1/8000, or between f/1.0 and f/91.
    /// </summary>
    /// <remarks>
    /// A word is the APEX value (Tv or Av) times 768, signed. Fitted on an R6 to every limit set on the
    /// body (1/8000, 1/4000, 1/2000, 30 s, 15 s, f/91, f/64, f/22, f/1.0, f/1.4), all exact. The R6's
    /// menu offers whole stops only for both ranges, at either exposure increment, so a word between
    /// stops never came from the body and how it would read one is unknown: it is left unnamed.
    /// </remarks>
    public static string? FormatRangeWord(CanonCustomFunctionRange range, uint word)
    {
        var raw = unchecked((int)word);
        if (raw % 768 != 0) return null;
        var stop = raw / 768;
        return range switch
        {
            CanonCustomFunctionRange.ShutterSpeed when stop - SlowestShutterStop is var i && i >= 0 && i < ShutterStops.Length
                => ShutterStops[i],
            CanonCustomFunctionRange.Aperture when stop >= 0 && stop < ApertureStops.Length
                => "f/" + ApertureStops[stop],
            _ => null,
        };
    }

    /// <summary>
    /// A range entry's two limits in the menu's words, e.g. <c>highest 1/4000, lowest 15"</c> or
    /// <c>min. aperture f/64, max. aperture f/1.4</c>; null for an entry that is not a range. A word that is not
    /// a whole stop on the menu's scale is shown raw.
    /// </summary>
    /// <remarks>Word 0 is the end with the larger APEX value (the fastest speed, the smallest aperture), word 1 the other.</remarks>
    public static string? FormatRange(CanonCustomFunctionInfo info, IReadOnlyList<uint> values)
    {
        if (info.Range is CanonCustomFunctionRange.None || values.Count < 2) return null;
        string Word(int i) => FormatRangeWord(info.Range, values[i]) ?? $"raw {unchecked((int)values[i])} (not a menu value)";
        return info.Range is CanonCustomFunctionRange.ShutterSpeed
            ? $"highest {Word(0)}, lowest {Word(1)}"
            : $"min. aperture {Word(0)}, max. aperture {Word(1)}";
    }
}

/// <summary>Which scale a range entry's words are on.</summary>
public enum CanonCustomFunctionRange
{
    /// <summary>Not a range.</summary>
    None,

    /// <summary>Two shutter speeds, each Tv APEX times 768.</summary>
    ShutterSpeed,

    /// <summary>Two apertures, each Av APEX times 768.</summary>
    Aperture,
}

/// <summary>A Custom Function entry's meaning on one body.</summary>
/// <param name="Group">The camera menu's group heading, e.g. "C.Fn II: Autofocus".</param>
/// <param name="Number">The item's number inside that group, as the menu shows it.</param>
/// <param name="FunctionId">The wire id.</param>
/// <param name="Name">The item's name in the menu.</param>
/// <param name="Word">Which value of the entry holds the setting (0 for most).</param>
/// <param name="Options">
/// What each value shows, indexed by value; for a mask (<see cref="IsMask"/>) the checkbox each bit
/// ticks, bit 0 first. Empty when the values are not decoded.
/// </param>
public sealed record CanonCustomFunctionInfo(
    string Group, int Number, uint FunctionId, string Name, int Word, IReadOnlyList<string> Options)
{
    /// <summary>The value is a checkbox mask, not an index into <see cref="Options"/>.</summary>
    public bool IsMask { get; init; }

    /// <summary>
    /// The wire value of each option, where it is not the option's index (an R6 sends the number of
    /// bracketed shots itself). Null when option <c>i</c> is value <c>i</c>.
    /// </summary>
    public IReadOnlyList<uint>? OptionValues { get; init; }

    /// <summary>
    /// The entry is a pair of limits rather than a choice; <see cref="CanonCustomFunctionCatalog.FormatRange"/>
    /// reads it.
    /// </summary>
    public CanonCustomFunctionRange Range { get; init; }

    /// <summary>What is known about the entry that its options do not say, or null.</summary>
    public string? Note { get; init; }

    /// <summary>
    /// The option list is inferred, not measured or taken from the manual: a reading shown through it
    /// needs confirming on the body.
    /// </summary>
    public bool Guess { get; init; }

    /// <summary>The menu counts items 1..n across every group (a 450D), not from 1 in each (a 6D).</summary>
    public bool NumberedAcrossGroups { get; init; }

    /// <summary>"C.Fn II-5" on a 6D, "C.Fn-9" on a 450D, "C.Fn2-1" on an R6: the way the camera's menu labels it.</summary>
    public string MenuLabel => NumberedAcrossGroups ? $"C.Fn-{Number}"
        : $"{(Group.IndexOf(':') is var colon and >= 0 ? Group[..colon] : Group)}-{Number}";

    /// <summary>The wire value of option <paramref name="index"/>.</summary>
    public uint ValueOf(int index) => OptionValues is { } values ? values[index] : (uint)index;

    /// <summary>The option a wire value selects, or null when none does (a mask or a range has no options to select).</summary>
    public string? OptionFor(uint value)
    {
        if (IsMask || Range is not CanonCustomFunctionRange.None) return null;
        if (OptionValues is null) return value < (uint)Options.Count ? Options[(int)value] : null;
        for (int i = 0; i < OptionValues.Count; i++)
            if (OptionValues[i] == value) return Options[i];
        return null;
    }
}
