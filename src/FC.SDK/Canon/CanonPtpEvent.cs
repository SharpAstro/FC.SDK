namespace FC.SDK.Canon;

internal readonly struct CanonPtpEvent
{
    public CanonEventType Type { get; init; }
    public uint Param1 { get; init; }
    public uint Param2 { get; init; }
    public uint Param3 { get; init; }

    /// <summary>
    /// The record bytes following the 8-byte <c>{length, type}</c> header, i.e. the same bytes
    /// <see cref="Param1"/>..<see cref="Param3"/> are decoded from. Needed for records carrying
    /// more than three words (AllowedValuesChanged, OLCInfoChanged) or non-uint32 payloads.
    /// </summary>
    public ReadOnlyMemory<byte> Payload { get; init; }
}
