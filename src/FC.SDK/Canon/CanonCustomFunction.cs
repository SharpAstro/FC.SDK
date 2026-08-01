using System.Buffers.Binary;

namespace FC.SDK.Canon;

/// <summary>
/// Represents the packed Custom Function data block from a Canon EOS camera.
/// On older bodies, settings like long exposure NR and mirror lockup live here
/// rather than as direct PTP properties (0xD1xx).
/// </summary>
/// <remarks>
/// <para>
/// libgphoto2 does not decode this block — <c>ptp_unpack_EOS_CustomFuncEx</c> reads the leading size
/// word and renders the rest as comma-separated hex uint32s — so the layout below was derived from a
/// real EOS 450D dump rather than taken from upstream:
/// </para>
/// <code>
/// [total_size:u32]           whole block, including this word
/// [group_count:u32]
/// per group:
///   [group_id:u32]           1..4, matching the camera menu's C.Fn I..IV groups
///   [group_size:u32]         bytes of the group EXCLUDING this word (= 8 + Σ entry sizes)
///   [entry_count:u32]
///   per entry:
///     [func_id:u32]          e.g. 0x0201 = Long exposure NR on a 450D
///     [value_count:u32]      1 for every entry seen so far
///     [value:u32 × value_count]
/// </code>
/// <para>
/// The group layer is what an earlier flat reading missed, which is why it produced nonsense ids such
/// as 0x2000000. Confirmed on a 450D: group entry counts came out 2/4/3/4, exactly matching that
/// body's C.Fn menu (I has 2 items, II has 4, III has 3, IV has 4), and two values cross-checked
/// against the on-camera menu — C.Fn 3 "Long exposure noise reduction" = Off = <c>0x0201 → 0</c>, and
/// C.Fn 9 "Mirror lockup" = disabled = <c>0x060F → 0</c>.
/// </para>
/// <para>
/// <see cref="RawData"/> remains the authoritative record; dump it when a body disagrees.
/// </para>
/// </remarks>
public sealed class CanonCustomFunctionBlock
{
    private readonly Dictionary<uint, uint> _functions = new();
    private readonly Dictionary<uint, uint> _groupOfFunction = new();
    private readonly Dictionary<uint, int> _valueOffset = new();
    private readonly List<CanonCustomFunctionEntry> _entries = [];

    /// <summary>All C.Fn entries: function ID → current value.</summary>
    public IReadOnlyDictionary<uint, uint> Functions => _functions;

    /// <summary>
    /// Every entry in the order the camera sent it.
    /// </summary>
    /// <remarks>
    /// Order is the whole point of this list, and the reason it exists alongside
    /// <see cref="Functions"/>: group order and entry-within-group order match the body's own C.Fn
    /// menu, item for item (verified on a 450D). That is what lets a reporter who cannot know wire
    /// ids say "menu C.Fn 5 is Mirror lockup" and have the id fall out — see
    /// <c>docs/canon-custom-functions.md</c>. A dictionary cannot promise that ordering.
    /// </remarks>
    public IReadOnlyList<CanonCustomFunctionEntry> Entries => _entries;

    /// <summary>
    /// Function ID → the C.Fn group it belongs to (1..4, matching the camera menu's C.Fn I..IV).
    /// </summary>
    public IReadOnlyDictionary<uint, uint> FunctionGroups => _groupOfFunction;

    /// <summary>The raw data as received from the camera.</summary>
    public byte[] RawData { get; private set; } = [];

    /// <summary>Gets a C.Fn value by ID. Returns null if not present.</summary>
    public uint? GetValue(uint functionId) =>
        _functions.TryGetValue(functionId, out var v) ? v : null;

    /// <summary>Sets a C.Fn value by ID. The function must already exist in the block.</summary>
    /// <returns>True if the function was found and updated.</returns>
    public bool SetValue(uint functionId, uint value)
    {
        // Offsets are recorded during Parse, so writing back does not re-walk (and cannot re-derive)
        // the structure. Sizes never change: every entry seen carries exactly one uint32 value.
        if (!_valueOffset.TryGetValue(functionId, out var offset))
            return false;

        _functions[functionId] = value;
        BinaryPrimitives.WriteUInt32LittleEndian(RawData.AsSpan(offset), value);
        return true;
    }

    /// <summary>Parses a C.Fn data block received from the camera. See the type remarks for the layout.</summary>
    internal static CanonCustomFunctionBlock Parse(byte[] data)
    {
        var block = new CanonCustomFunctionBlock { RawData = (byte[])data.Clone() };

        if (data.Length < 8)
            return block;

        uint totalSize = BinaryPrimitives.ReadUInt32LittleEndian(data);
        int limit = Math.Min(data.Length, totalSize > 0 ? (int)totalSize : data.Length);
        uint groupCount = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(4));

        int offset = 8;
        for (uint g = 0; g < groupCount; g++)
        {
            // group header: [group_id][group_size][entry_count]
            if (offset + 12 > limit) break;

            uint groupId = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset));
            uint groupSize = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset + 4));
            uint entryCount = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset + 8));

            // group_size excludes its own word, so the next group starts 4 bytes further on.
            int nextGroup = offset + 4 + (int)groupSize;
            int entry = offset + 12;

            for (uint e = 0; e < entryCount; e++)
            {
                if (entry + 12 > limit) break;

                uint funcId = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(entry));
                uint valueCount = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(entry + 4));

                uint value = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(entry + 8));
                block._functions[funcId] = value;
                block._groupOfFunction[funcId] = groupId;
                block._valueOffset[funcId] = entry + 8;
                block._entries.Add(new CanonCustomFunctionEntry(groupId, funcId, value, block._entries.Count + 1));

                // Honour value_count even though every entry observed so far carries exactly one.
                entry += 8 + (int)Math.Max(1u, valueCount) * 4;
            }

            // Trust group_size for the stride when it is sane, so one malformed entry cannot
            // desynchronise the whole walk; fall back to where the entries actually ended.
            offset = nextGroup > offset && nextGroup <= limit ? nextGroup : entry;
        }

        return block;
    }
}

/// <summary>
/// One Custom Function entry, as it appeared on the wire.
/// </summary>
/// <param name="GroupId">The C.Fn group, 1..4 — the camera menu's C.Fn I..IV.</param>
/// <param name="FunctionId">The wire id, e.g. <c>0x060F</c>. Per-model; never guess one.</param>
/// <param name="Value">The current value.</param>
/// <param name="MenuNumber">
/// Position in the block counting from 1 across all groups. On a 450D this is exactly the number
/// beside the setting in the camera's own C.Fn menu, which is the one thing a reporter can read off
/// their body without knowing anything about the protocol.
/// </param>
public readonly record struct CanonCustomFunctionEntry(
    uint GroupId, uint FunctionId, uint Value, int MenuNumber);

/// <summary>
/// Well-known Canon Custom Function IDs. These are camera-specific — the same setting
/// may have different IDs on different bodies. Values here are for common EOS models.
/// </summary>
public static class CanonCustomFunctionId
{
    // No 6D constants here on purpose. Both noise-reduction settings and mirror lockup were checked
    // against the EOS 6D manual (im7): "Long exp. noise reduction" and "High ISO speed NR" live under
    // the [z4] shooting-menu tab (p.302), and "Mirror lockup" is under [z2] (p.164) — none of the
    // three are listed in the manual's C.Fn I/II/III tables (p.302-303). On the 6D these are plain
    // properties, not Custom Functions, so a wire id for them here would be guessing at something
    // that does not exist. See docs/canon-custom-functions.md.

    // --- EOS 450D C.Fn IDs — read off a real body, cross-checked against its on-camera menu ---
    //
    // The menu numbers items 1..13 continuously while grouping them as C.Fn I..IV; the block's
    // group_id matches that grouping, and entry order within a group matches menu order. Earlier
    // values here (1, 2, 7) were the menu's item numbers, which are NOT the wire ids.

    /// <summary>Menu C.Fn 1 (group I, "Exposure"): exposure level increments. 0=1/3-stop, 1=1/2-stop.</summary>
    public const uint ExposureLevelIncrements_450D = 0x0101;

    /// <summary>Menu C.Fn 2 (group I): flash sync speed in Av mode. 0=Auto, 1=1/200 fixed.</summary>
    public const uint FlashSyncSpeedInAvMode_450D = 0x010F;

    /// <summary>Menu C.Fn 3 (group II, "Image"): long exposure noise reduction. 0=Off, 1=Auto, 2=On.</summary>
    public const uint LongExposureNR_450D = 0x0201;

    /// <summary>Menu C.Fn 4 (group II): high ISO speed noise reduction. 0=Standard, 1=Low, 2=Strong, 3=Disable.</summary>
    public const uint HighIsoNR_450D = 0x0202;

    /// <summary>Menu C.Fn 5 (group II): highlight tone priority. 0=Disable, 1=Enable.</summary>
    public const uint HighlightTonePriority_450D = 0x0203;

    /// <summary>Menu C.Fn 6 (group II): Auto Lighting Optimizer.</summary>
    public const uint AutoLightingOptimizer_450D = 0x0204;

    /// <summary>Menu C.Fn 7 (group III, "Autofocus/Drive"): AF-assist beam firing.</summary>
    public const uint AfAssistBeamFiring_450D = 0x050E;

    /// <summary>Menu C.Fn 8 (group III): AF during Live View shooting. 0=Disable, 1=Enable.</summary>
    public const uint AfDuringLiveView_450D = 0x0511;

    /// <summary>Menu C.Fn 9 (group III, "Autofocus/Drive"): mirror lockup. 0=Disable, 1=Enable.</summary>
    public const uint MirrorLockup_450D = 0x060F;

    /// <summary>Menu C.Fn 10 (group IV, "Operation/Others"): shutter / AE lock button behaviour.</summary>
    public const uint ShutterAeLockButton_450D = 0x0701;

    /// <summary>Menu C.Fn 11 (group IV): SET button function when shooting.</summary>
    public const uint SetButtonWhenShooting_450D = 0x0704;

    /// <summary>Menu C.Fn 12 (group IV): LCD display when power ON.</summary>
    public const uint LcdDisplayWhenPowerOn_450D = 0x0811;

    /// <summary>Menu C.Fn 13 (group IV): add original decision data. 0=Off, 1=On.</summary>
    public const uint AddOriginalDecisionData_450D = 0x080F;

    /// <summary>
    /// The C.Fn id for mirror lockup on <paramref name="model"/>, or null when the body exposes MLU
    /// as the 0xD13A property instead — or when its id here is simply unverified. Only bodies whose
    /// block was read off real hardware are listed; ids must not be guessed (same rule as
    /// <see cref="CanonPropertyMap"/>). The 450D's aliases are Rebel XSi (Americas) and Kiss X2 (Japan).
    /// </summary>
    public static uint? MirrorLockupIdFor(string? model) => model switch
    {
        not null when Is450DFamily(model) => MirrorLockup_450D,
        _ => null,
    };

    /// <summary>
    /// The C.Fn id for high-ISO noise reduction, or null when the body has the 0xD178 property.
    /// Same verified-bodies-only rule as <see cref="MirrorLockupIdFor"/>. Note the 450D's C.Fn is a
    /// two-state Off/On, not the four-level scheme newer bodies use.
    /// </summary>
    public static uint? HighIsoNrIdFor(string? model) => model switch
    {
        not null when Is450DFamily(model) => HighIsoNR_450D,
        _ => null,
    };

    private static bool Is450DFamily(string model) =>
        model.Contains("450D") || model.Contains("XSi") || model.Contains("Kiss X2");
}
