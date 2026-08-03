using FC.SDK.Canon;

namespace FC.SDK.Viewer;

/// <summary>
/// One camera setting surfaced in the UI: how to name it, how to render its raw uint32 value, and
/// how to move to the next value.
/// </summary>
/// <param name="Label">Display name.</param>
/// <param name="PropertyId">The EDSDK property this control reads and writes.</param>
/// <param name="EnumType">
/// Enum whose names describe the value space, or null for a plain number. Doubles as the fallback
/// value list for cycling when the camera never described its allowed values.
/// </param>
/// <param name="Step">Increment for numeric controls. Zero means the control is enumerated.</param>
/// <param name="Min">Lower bound for numeric controls (inclusive).</param>
/// <param name="Max">Upper bound for numeric controls (inclusive); wraps back to <paramref name="Min"/>.</param>
/// <param name="Writable">
/// False for values the camera only reports. A read-only control is still shown — an unreadable
/// AvailableShots is exactly the symptom worth seeing.
/// </param>
/// <param name="Unit">Suffix appended to a numeric value, e.g. "s" or "K".</param>
public sealed record CameraControl(
    string Label,
    EdsPropertyId PropertyId,
    Type? EnumType = null,
    uint Step = 0,
    uint Min = 0,
    uint Max = 0,
    bool Writable = true,
    string Unit = "")
{
    /// <summary>Renders a raw value for display, naming it through <see cref="EnumType"/> when possible.</summary>
    public string Format(uint value)
    {
        if (EnumType is null)
            return Unit.Length > 0 ? $"{value} {Unit}" : value.ToString();

        var name = Enum.GetName(EnumType, value);
        return name ?? $"0x{value:X} (unnamed)";
    }

    /// <summary>
    /// The values to cycle through: what the camera says it accepts, falling back to the enum's own
    /// members. The camera's list is authoritative — bodies differ wildly in which ISOs or shutter
    /// speeds they expose.
    /// </summary>
    public uint[] CycleValues(uint[]? cameraAllowed)
    {
        if (cameraAllowed is { Length: > 0 }) return cameraAllowed;
        if (EnumType is null) return [];

        // GetValuesAsUnderlyingType rather than GetValues: the latter is RequiresDynamicCode
        // (IL3050) because it has to build an array of the enum type itself, which a NativeAOT
        // publish cannot do. The underlying-type overload returns the primitives directly.
        return [.. Enum.GetValuesAsUnderlyingType(EnumType).Cast<object>()
            .Select(Convert.ToUInt32)
            .Where(v => v != uint.MaxValue)   // skip sentinel "Unknown" members
            .Distinct()
            .Order()];
    }

    /// <summary>The value one step past <paramref name="current"/>, wrapping at the end.</summary>
    public uint Next(uint current, uint[]? cameraAllowed)
    {
        if (Step > 0)
        {
            var next = current + Step;
            return next > Max ? Min : next;
        }

        var values = CycleValues(cameraAllowed);
        if (values.Length == 0) return current;

        var index = Array.IndexOf(values, current);
        return values[(index + 1) % values.Length];
    }

    /// <summary>The value one step before <paramref name="current"/>, wrapping at the start.</summary>
    public uint Previous(uint current, uint[]? cameraAllowed)
    {
        if (Step > 0)
            return current <= Min || current - Step < Min ? Max : current - Step;

        var values = CycleValues(cameraAllowed);
        if (values.Length == 0) return current;

        var index = Array.IndexOf(values, current);
        if (index < 0) index = 0;
        return values[(index - 1 + values.Length) % values.Length];
    }
}

/// <summary>
/// Every setting the SDK can map, grouped the way a photographer thinks about them.
/// </summary>
/// <remarks>
/// Deliberately exhaustive rather than curated: this is a diagnostic tool, and a control that comes
/// back NotSupported on a given body is a finding, not a defect in the list.
/// </remarks>
public static class CameraControls
{
    public static readonly (string Group, CameraControl[] Controls)[] Groups =
    [
        ("Exposure",
        [
            new("ISO", EdsPropertyId.ISOSpeed, typeof(EdsISOSpeed)),
            new("Shutter (Tv)", EdsPropertyId.Tv, typeof(EdsTv)),
            new("Aperture (Av)", EdsPropertyId.Av, typeof(EdsAv)),
            new("Exp. comp.", EdsPropertyId.ExposureCompensation),
            new("Shoot mode", EdsPropertyId.AEMode, typeof(EdsAEMode)),
            new("Metering", EdsPropertyId.MeteringMode, typeof(EdsMeteringMode)),
            new("AE bracket", EdsPropertyId.AEBracket),
        ]),

        ("Image",
        [
            new("White balance", EdsPropertyId.WhiteBalance, typeof(EdsWhiteBalance)),
            new("Colour temp.", EdsPropertyId.ColorTemperature, Step: 100, Min: 2500, Max: 10000, Unit: "K"),
            new("Colour space", EdsPropertyId.ColorSpace),
            new("Picture style", EdsPropertyId.PictureStyle),
            new("Quality", EdsPropertyId.ImageQuality),
            new("High ISO NR", EdsPropertyId.NoiseReduction, typeof(EdsHighIsoNR)),
        ]),

        ("Drive / focus",
        [
            new("Drive mode", EdsPropertyId.DriveMode, typeof(EdsDriveMode)),
            new("AF mode", EdsPropertyId.AFMode, typeof(EdsAFMode)),
            new("Mirror lockup", EdsPropertyId.MirrorUpSetting, typeof(EdsMirrorUpSetting)),
            new("MLU state", EdsPropertyId.MirrorLockUpState, typeof(EdsMirrorLockupState), Writable: false),
        ]),

        ("Live view",
        [
            new("LV output", EdsPropertyId.Evf_OutputDevice, typeof(EdsEvfOutputDevice)),
            new("LV mode", EdsPropertyId.Evf_Mode),
            new("DoF preview", EdsPropertyId.Evf_DepthOfFieldPreview, typeof(EdsEvfDepthOfFieldPreview)),
        ]),

        ("Session / body",
        [
            new("Save to", EdsPropertyId.SaveTo, typeof(CanonCaptureDestination)),
            // Auto power-off is read-only here, and that is measured rather than assumed: a 6D
            // refuses every write to 0xD114 with DeviceBusy, with the event queue drained, under
            // UILock and in live view alike, writing a value that differed from the one held. It
            // announces no allowed values for the property either. It used to be a spinner with a
            // 0-1800s range, so every click was a no-op that reported an error. Use the
            // "Keep device on" button (0x911D) to hold a body awake; that one works, and live view
            // already feeds it automatically every ~8 s.
            new("Auto power-off", EdsPropertyId.AutoPowerOffSetting, Writable: false, Unit: "s"),
            new("Available shots", EdsPropertyId.AvailableShots, Writable: false),
            new("Temp. status", EdsPropertyId.TempStatus, Writable: false),
            new("Battery", EdsPropertyId.BatteryLevel, Writable: false),
            // No "Lens (raw)" here any more: this panel renders uint32s, and the lens name is a
            // string, so the control could only ever show the first four characters as a number.
            // The lens is in the device report instead, which is text.
        ]),
    ];

    /// <summary>Flat view of every control, for the "read everything" sweep.</summary>
    public static IEnumerable<CameraControl> All => Groups.SelectMany(g => g.Controls);
}
