using System.Collections.Frozen;

namespace FC.SDK.Canon;

/// <summary>
/// How a property's value bytes are laid out on the wire.
/// </summary>
/// <remarks>
/// This replaced a <c>Size</c> field that every call site discarded — dead weight that also encoded
/// a wrong claim, since the four string properties were all declared as 4 bytes and so read back as
/// the first four characters reinterpreted as an integer.
/// </remarks>
internal enum CanonPropertyType
{
    /// <summary>A scalar. Canon pads these to four bytes; a handful report narrower.</summary>
    UInt32,

    /// <summary>
    /// Plain null-terminated ASCII — <b>not</b> the PTP length-prefixed UTF-16 string form.
    /// libgphoto2 implemented it the PTP way first and reverted it, leaving the reason in a comment
    /// next to the disabled code: <i>"5D MII and 400D aktually store plain ASCII in their string
    /// properties"</i> (<c>ptp-pack.c</c>, <c>PTP_DTC_STR</c> case).
    /// </summary>
    String,

    /// <summary>
    /// A packed structure with no meaningful uint32 surface — the Custom Function block and the
    /// AF-point descriptors. Read it with the byte accessor.
    /// </summary>
    Blob,
}

internal static class CanonPropertyMap
{
    // PTP codes verified against libgphoto2 ptp.h (PTP_DPC_CANON_EOS_*)
    private static readonly FrozenDictionary<EdsPropertyId, (ushort PtpCode, CanonPropertyType Type)> _map =
        new Dictionary<EdsPropertyId, (ushort, CanonPropertyType)>
        {
            [EdsPropertyId.Av] = (0xD101, CanonPropertyType.UInt32),
            [EdsPropertyId.Tv] = (0xD102, CanonPropertyType.UInt32),
            [EdsPropertyId.ISOSpeed] = (0xD103, CanonPropertyType.UInt32),
            [EdsPropertyId.ExposureCompensation] = (0xD104, CanonPropertyType.UInt32),
            [EdsPropertyId.AEMode] = (0xD105, CanonPropertyType.UInt32),
            [EdsPropertyId.DriveMode] = (0xD106, CanonPropertyType.UInt32),
            [EdsPropertyId.MeteringMode] = (0xD107, CanonPropertyType.UInt32),
            [EdsPropertyId.AFMode] = (0xD108, CanonPropertyType.UInt32),
            [EdsPropertyId.WhiteBalance] = (0xD109, CanonPropertyType.UInt32),
            [EdsPropertyId.ColorTemperature] = (0xD10A, CanonPropertyType.UInt32),
            [EdsPropertyId.ColorSpace] = (0xD10F, CanonPropertyType.UInt32),
            [EdsPropertyId.PictureStyle] = (0xD110, CanonPropertyType.UInt32),
            [EdsPropertyId.BatteryLevel] = (0xD111, CanonPropertyType.UInt32),
            [EdsPropertyId.DateTime] = (0xD113, CanonPropertyType.UInt32),
            [EdsPropertyId.AutoPowerOffSetting] = (0xD114, CanonPropertyType.UInt32),
            [EdsPropertyId.OwnerName] = (0xD115, CanonPropertyType.String),
            [EdsPropertyId.AvailableShots] = (0xD11B, CanonPropertyType.UInt32),
            [EdsPropertyId.SaveTo] = (0xD11C, CanonPropertyType.UInt32),
            [EdsPropertyId.AEBracket] = (0xD11D, CanonPropertyType.UInt32),   // BracketMode in libgphoto2
            [EdsPropertyId.ImageQuality] = (0xD120, CanonPropertyType.UInt32),
            [EdsPropertyId.MirrorUpSetting] = (0xD13A, CanonPropertyType.UInt32),
            [EdsPropertyId.NoiseReduction] = (0xD178, CanonPropertyType.UInt32),   // HighISONoiseReduction
            [EdsPropertyId.TempStatus] = (0xD1AB, CanonPropertyType.UInt32),

            // EDSDK calls the body serial BodyIDEx; libgphoto2 calls the same wire code SerialNumber.
            [EdsPropertyId.BodyIDEx] = (0xD1AF, CanonPropertyType.String),

            [EdsPropertyId.Evf_OutputDevice] = (0xD1B0, CanonPropertyType.UInt32),
            [EdsPropertyId.Evf_Mode] = (0xD1B1, CanonPropertyType.UInt32),
            [EdsPropertyId.Evf_DepthOfFieldPreview] = (0xD1B2, CanonPropertyType.UInt32),

            // libgphoto2 calls 0xD1BA LvAfSystem and exposes it as "AF Method"; EDSDK's
            // Evf_AFMode is the same setting. It gates magnification — see CanonEvfAfSystem.
            [EdsPropertyId.Evf_AFMode] = (0xD1BA, CanonPropertyType.UInt32),

            [EdsPropertyId.MirrorLockUpState] = (0xD1BF, CanonPropertyType.UInt32),
            [EdsPropertyId.Artist] = (0xD1D0, CanonPropertyType.String),
            [EdsPropertyId.Copyright] = (0xD1D1, CanonPropertyType.String),
            [EdsPropertyId.LensName] = (0xD1D8, CanonPropertyType.String),
        }.ToFrozenDictionary();

    private static readonly FrozenDictionary<ushort, EdsPropertyId> _reverseMap =
        _map.ToDictionary(kv => kv.Value.PtpCode, kv => kv.Key).ToFrozenDictionary();

    /// <summary>All EDSDK property IDs that have a known Canon PTP mapping.</summary>
    internal static IEnumerable<EdsPropertyId> MappedProperties => _map.Keys;

    /// <summary>
    /// Best-effort name for a raw PTP property code — the mapped EDSDK ID when known,
    /// otherwise null. Used for diagnostics dumps of the event-fed property cache, which
    /// contains far more codes than <see cref="EdsPropertyId"/> covers.
    /// </summary>
    internal static EdsPropertyId? TryGetPropertyId(ushort ptpCode) =>
        _reverseMap.TryGetValue(ptpCode, out var id) ? id : null;

    internal static bool TryGetPtpCode(EdsPropertyId id, out ushort ptpCode, out CanonPropertyType type)
    {
        if (_map.TryGetValue(id, out var entry))
        {
            ptpCode = entry.PtpCode;
            type = entry.Type;
            return true;
        }
        ptpCode = 0;
        type = CanonPropertyType.UInt32;
        return false;
    }

    /// <summary>
    /// The wire layout of a mapped property, or <see cref="CanonPropertyType.UInt32"/> for anything
    /// unmapped — the raw accessors take a bare PTP code and have no id to look up.
    /// </summary>
    internal static CanonPropertyType TypeOf(EdsPropertyId id) =>
        _map.TryGetValue(id, out var entry) ? entry.Type : CanonPropertyType.UInt32;

    internal static ushort GetPtpCodeOrThrow(EdsPropertyId id)
    {
        if (!TryGetPtpCode(id, out ushort ptpCode, out _))
            throw new NotSupportedException($"Property {id} has no known Canon PTP mapping.");
        return ptpCode;
    }
}
