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
/// the manual names their positions but not their numbers. See <c>docs/canon-custom-functions.md</c>.
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
        new(Exposure, 5, 0x0106, "Number of bracketed shots", 0, ["3 shots", "2 shots", "5 shots", "7 shots"]),
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

    /// <summary>Every entry known for <paramref name="model"/>, in menu order; empty for an unmeasured body.</summary>
    public static IReadOnlyList<CanonCustomFunctionInfo> For(string? model) => model switch
    {
        "Canon EOS 6D" => Eos6D,
        not null when CanonCustomFunctionId.Is450DFamily(model) => Eos450D,
        _ => [],
    };

    /// <summary>One entry's meaning on <paramref name="model"/>, or null when it has not been measured.</summary>
    public static CanonCustomFunctionInfo? Describe(string? model, uint functionId) =>
        For(model).FirstOrDefault(i => i.FunctionId == functionId);
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
    /// The option list is inferred, not measured or taken from the manual: a reading shown through it
    /// needs confirming on the body.
    /// </summary>
    public bool Guess { get; init; }

    /// <summary>The menu counts items 1..n across every group (a 450D), not from 1 in each (a 6D).</summary>
    public bool NumberedAcrossGroups { get; init; }

    /// <summary>"C.Fn II-5" on a 6D, "C.Fn-9" on a 450D: the way the camera's menu labels it.</summary>
    public string MenuLabel => NumberedAcrossGroups ? $"C.Fn-{Number}" : $"{Group[..Group.IndexOf(':')]}-{Number}";
}
