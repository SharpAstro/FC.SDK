using System.Text;
using FC.SDK.Canon;
using FC.SDK.Protocol;

namespace FC.SDK;

/// <summary>
/// Builds a self-contained description of what a specific camera body actually is — model, the
/// operations it advertises, every property it announces, and its decoded Custom Function block.
/// </summary>
/// <remarks>
/// <para>
/// This exists because the things that differ per body cannot be derived, guessed, or read out of
/// Canon's manuals. Manuals give menu numbers; the protocol uses wire ids, and the mapping between
/// them is per-model. EDSDK is no help either — it holds no per-model C.Fn table at all, taking the
/// id from its caller and linearly searching the block. So the only way to support a body nobody
/// here owns is for someone who owns one to send this.
/// </para>
/// <para>
/// Written for pasting into a GitHub issue: Markdown, no secrets beyond the serial number (which a
/// reporter can redact), and every raw value preserved next to its interpretation so a wrong guess
/// on our side is still recoverable from the file.
/// </para>
/// </remarks>
public static class CanonDeviceReport
{
    /// <summary>
    /// Collects a device report. Drains pending events first, so the property section reflects what
    /// the camera has announced up to now — call it after the event pump has been running, or after
    /// reading properties, or the picture will be thin.
    /// </summary>
    public static async Task<string> CreateAsync(CanonCamera camera, CancellationToken ct = default)
    {
        var report = new StringBuilder();

        report.AppendLine("# FC.SDK device report");
        report.AppendLine();
        report.AppendLine($"- Generated: {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}");
        report.AppendLine($"- FC.SDK: {typeof(CanonDeviceReport).Assembly.GetName().Version}");
        report.AppendLine($"- OS: {Environment.OSVersion} ({System.Runtime.InteropServices.RuntimeInformation.OSArchitecture})");
        report.AppendLine($"- Model: `{camera.Model ?? "(unknown)"}`");
        report.AppendLine($"- Serial: `{camera.SerialNumber ?? "(unknown)"}`");
        report.AppendLine($"- Transport: {camera.TransportName}");
        report.AppendLine($"- Battery: {(camera.BatteryLevelPercent is { } b ? $"{b}%" : "not reported")}");
        report.AppendLine();

        AppendOperations(report, camera);
        var properties = await AppendPropertiesAsync(report, camera, ct).ConfigureAwait(false);
        AppendBatteryWarning(report, properties);
        await AppendCustomFunctionsAsync(report, camera, ct).ConfigureAwait(false);

        return report.ToString();
    }

    /// <summary>
    /// The operations the body advertises. Several SDK decisions branch on this set, so it is worth
    /// having in full rather than as a yes/no per feature.
    /// </summary>
    private static void AppendOperations(StringBuilder report, CanonCamera camera)
    {
        report.AppendLine("## Supported operations");
        report.AppendLine();

        if (camera.SupportedOperations.Count is 0)
        {
            report.AppendLine("_The camera reported no operation list._");
            report.AppendLine();
            return;
        }

        report.AppendLine("| Opcode | Name |");
        report.AppendLine("|---|---|");
        foreach (ushort code in camera.SupportedOperations.Order())
        {
            string name = Enum.IsDefined(typeof(PtpOperationCode), code)
                ? ((PtpOperationCode)code).ToString()
                : "(unidentified)";
            report.AppendLine($"| `0x{code:X4}` | {name} |");
        }
        report.AppendLine();

        // Called out because each one changes what the SDK does, and because their absence is the
        // usual explanation for a body behaving unlike the one this was developed against.
        report.AppendLine("Notable:");
        report.AppendLine();
        Note(report, camera, PtpOperationCode.GetDevicePropValue,
            "standard property get. Its presence does NOT mean 0xD1xx values can be read with it — "
            + "EOS bodies answer OperationNotSupported for vendor codes either way, and values arrive "
            + "through the event stream. A 450D advertises it; newer bodies often do not");
        Note(report, camera, PtpOperationCode.CanonRemoteReleaseOn,
            "two-stage release (AF then shutter); without it the SDK falls back to RemoteRelease 0x910F");
        Note(report, camera, PtpOperationCode.CanonInitiateViewfinder,
            "explicit live-view start/stop; without it, setting EVFOutputDevice to PC is the whole sequence");
        Note(report, camera, PtpOperationCode.CanonSetRequestOLCInfoGroup,
            "required on newer bodies before Tv/Av/ISO and AF state are reported");
        Note(report, camera, PtpOperationCode.CanonBulbStart, "bulb exposure");
        report.AppendLine();
    }

    private static void Note(StringBuilder report, CanonCamera camera, PtpOperationCode code, string why)
    {
        bool present = camera.SupportedOperations.Contains((ushort)code);
        report.AppendLine($"- `0x{(ushort)code:X4}` {code} — **{(present ? "present" : "absent")}**: {why}");
    }

    /// <summary>
    /// Canon's own battery property, which several bodies report when the standard PTP one is
    /// missing.
    /// </summary>
    private const ushort BatteryLevelCode = 0xD111;

    /// <summary>
    /// Flags a nearly flat battery, loudly. A low battery does not announce itself as an error: an
    /// EOS 450D at level 1 has been observed answering OK to a release that never exposes, dropping
    /// live view after streaming happily for a while, and announcing dial movements nobody made.
    /// Every one of those reads as an SDK bug, and none of them are — so a report that shows this
    /// should be re-taken on a charged battery before anyone spends time on it.
    /// </summary>
    private static void AppendBatteryWarning(StringBuilder report, IReadOnlyList<CanonPropertySnapshot> properties)
    {
        if (properties.FirstOrDefault(p => p.PtpCode == BatteryLevelCode) is not { } battery) return;

        report.AppendLine($"### Battery (Canon `0x{BatteryLevelCode:X4}`): level {battery.Value}");
        report.AppendLine();
        if (battery.Value <= 1)
        {
            report.AppendLine(
                "> **Warning — this battery is nearly flat, and that invalidates most of what follows.** "
                + "Bodies at the lowest level answer OK to commands they then ignore, drop live view "
                + "mid-stream, and report property changes that never happened. Please recharge and "
                + "re-take this report before drawing any conclusions from it.");
            report.AppendLine();
        }
    }

    private static async Task<IReadOnlyList<CanonPropertySnapshot>> AppendPropertiesAsync(
        StringBuilder report, CanonCamera camera, CancellationToken ct)
    {
        report.AppendLine("## Announced properties");
        report.AppendLine();
        report.AppendLine(
            "Everything the camera has pushed through the event stream, including codes this SDK has "
            + "no name for. A property missing here is a property the body does not have — which is "
            + "what sends a setting to the Custom Function block below.");
        report.AppendLine();

        var properties = await camera.DumpPropertiesAsync(ct).ConfigureAwait(false);
        if (properties.Count is 0)
        {
            report.AppendLine("_Nothing announced. Run the event pump, or read some properties, before reporting._");
            report.AppendLine();
            return properties;
        }

        report.AppendLine("| PTP code | Name | Value | Allowed values |");
        report.AppendLine("|---|---|---|---|");
        foreach (var property in properties.OrderBy(p => p.PtpCode))
        {
            string allowed = property.AllowedValues is { Length: > 0 } values
                ? string.Join(", ", values.Select(v => $"0x{v:X}"))
                : "—";
            report.AppendLine(
                $"| `0x{property.PtpCode:X4}` | {property.PropertyId?.ToString() ?? "(unmapped)"} "
                + $"| `0x{property.Value:X8}` ({property.Value}) | {allowed} |");
        }
        report.AppendLine();

        // The three that decide whether a body needs the C.Fn fallback at all.
        report.AppendLine("Settings that some bodies keep as properties and others as Custom Functions:");
        report.AppendLine();
        foreach (var (code, name) in new (ushort, string)[]
                 {
                     (0xD178, "NoiseReduction (high-ISO NR)"),
                     (0xD13A, "MirrorUpSetting"),
                     (0xD1BF, "MirrorLockUpState"),
                 })
        {
            bool announced = properties.Any(p => p.PtpCode == code);
            report.AppendLine($"- `0x{code:X4}` {name} — **{(announced ? "announced" : "not announced")}**");
        }
        report.AppendLine();

        return properties;
    }

    private static async Task AppendCustomFunctionsAsync(StringBuilder report, CanonCamera camera, CancellationToken ct)
    {
        report.AppendLine("## Custom Function block");
        report.AppendLine();

        var (err, block) = await camera.GetCustomFunctionBlockAsync(ct).ConfigureAwait(false);
        if (err is not EdsError.OK || block is null)
        {
            report.AppendLine($"_Could not read the block: {err}._");
            report.AppendLine();
            return;
        }

        report.AppendLine(
            "**Menu #** counts entries in wire order across all groups. On every body checked so far "
            + "that is the same number the camera's own C.Fn menu shows beside the setting, and "
            + "groups map to C.Fn I..IV in order.");
        report.AppendLine();
        report.AppendLine(
            "> If you are filing an issue: please say what each menu number is called on YOUR camera "
            + "(e.g. \"5 = Mirror lockup\"). That is the missing half — the wire ids are here, their "
            + "meanings are per-model and cannot be guessed.");
        report.AppendLine();

        report.AppendLine("| Menu # | Group | Wire id | Value |");
        report.AppendLine("|---|---|---|---|");
        foreach (var entry in block.Entries)
        {
            report.AppendLine(
                $"| {entry.MenuNumber} | C.Fn {entry.GroupId} | `0x{entry.FunctionId:X4}` | {entry.Value} |");
        }
        report.AppendLine();

        // The parsed view above is an interpretation; this is the evidence. Keep both, so a layout
        // that turns out to be wrong on some body is still recoverable from an already-filed report.
        report.AppendLine("Raw block, as received:");
        report.AppendLine();
        report.AppendLine("```");
        report.AppendLine(Convert.ToHexString(block.RawData));
        report.AppendLine("```");
        report.AppendLine();
    }
}
