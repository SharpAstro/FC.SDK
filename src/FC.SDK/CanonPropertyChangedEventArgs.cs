using FC.SDK.Canon;

namespace FC.SDK;

/// <summary>A property value announced on the event stream (<c>PropValueChanged</c>, 0xC189).</summary>
/// <remarks>
/// The record carries the Canon PTP property code, not an EDSDK id. <see cref="PropertyId"/> used to be that code cast
/// to <see cref="EdsPropertyId"/>, so no comparison against a named id could ever match (an EOS R6 announces ISO as
/// 0xD103, never as <see cref="EdsPropertyId.ISOSpeed"/>).
/// </remarks>
public sealed class CanonPropertyChangedEventArgs(ushort ptpCode, uint value) : EventArgs
{
    /// <summary>The Canon PTP property code as it came off the wire (0xD1xx/0xD2xx). Always meaningful.</summary>
    public ushort PtpCode { get; } = ptpCode;

    /// <summary>
    /// The EDSDK id mapped to <see cref="PtpCode"/> by <c>CanonPropertyMap</c>. For a code the map does not cover this
    /// is the raw code cast to the enum, as before — compare <see cref="PtpCode"/> for those, or check
    /// <see cref="IsMapped"/>.
    /// </summary>
    public EdsPropertyId PropertyId { get; } = CanonPropertyMap.TryGetPropertyId(ptpCode) ?? (EdsPropertyId)ptpCode;

    /// <summary>Whether <see cref="PropertyId"/> is a real EDSDK id rather than the raw code cast.</summary>
    public bool IsMapped => CanonPropertyMap.TryGetPropertyId(PtpCode) is not null;

    public uint Value { get; } = value;

    /// <summary>Kept for source compatibility: builds the arguments from an EDSDK id through <c>CanonPropertyMap</c>.</summary>
    public CanonPropertyChangedEventArgs(EdsPropertyId propertyId, uint value)
        : this(CanonPropertyMap.TryGetPtpCode(propertyId, out var ptpCode, out _) ? ptpCode : (ushort)propertyId, value)
    {
    }
}
