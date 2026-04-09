using System.Buffers.Binary;

namespace FC.SDK.Canon;

/// <summary>
/// Represents the packed Custom Function data block from a Canon EOS camera.
/// On older bodies, settings like long exposure NR and mirror lockup live here
/// rather than as direct PTP properties (0xD1xx).
/// </summary>
/// <remarks>
/// Canon C.Fn block format (as sent/received via GetDevicePropValue/SetPropValue on 0xD1A0..0xD1AF):
/// <code>
/// [total_size:u32]
/// For each function (12 bytes each):
///   [fn_size:u32]        — always 12
///   [fn_id:u32]          — C.Fn identifier (camera-specific numbering)
///   [current_value:u32]  — current setting
/// </code>
/// </remarks>
public sealed class CanonCustomFunctionBlock
{
    private readonly Dictionary<uint, uint> _functions = new();

    /// <summary>All C.Fn entries: function ID → current value.</summary>
    public IReadOnlyDictionary<uint, uint> Functions => _functions;

    /// <summary>The raw data as received from the camera.</summary>
    public byte[] RawData { get; private set; } = [];

    /// <summary>Gets a C.Fn value by ID. Returns null if not present.</summary>
    public uint? GetValue(uint functionId) =>
        _functions.TryGetValue(functionId, out var v) ? v : null;

    /// <summary>Sets a C.Fn value by ID. The function must already exist in the block.</summary>
    /// <returns>True if the function was found and updated.</returns>
    public bool SetValue(uint functionId, uint value)
    {
        if (!_functions.ContainsKey(functionId))
            return false;

        _functions[functionId] = value;

        // Patch the raw data in-place so it can be written back
        int offset = 4; // skip total_size
        while (offset + 12 <= RawData.Length)
        {
            uint fnSize = BinaryPrimitives.ReadUInt32LittleEndian(RawData.AsSpan(offset));
            uint fnId = BinaryPrimitives.ReadUInt32LittleEndian(RawData.AsSpan(offset + 4));
            if (fnId == functionId)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(RawData.AsSpan(offset + 8), value);
                return true;
            }
            offset += (int)(fnSize > 0 ? fnSize : 12);
        }
        return false;
    }

    /// <summary>Parses a C.Fn data block received from the camera.</summary>
    internal static CanonCustomFunctionBlock Parse(byte[] data)
    {
        var block = new CanonCustomFunctionBlock { RawData = (byte[])data.Clone() };

        if (data.Length < 4)
            return block;

        uint totalSize = BinaryPrimitives.ReadUInt32LittleEndian(data);
        int offset = 4;

        while (offset + 12 <= data.Length && offset < (int)totalSize)
        {
            uint fnSize = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset));
            uint fnId = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset + 4));
            uint fnValue = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset + 8));

            block._functions[fnId] = fnValue;
            offset += (int)(fnSize > 0 ? fnSize : 12);
        }

        return block;
    }
}

/// <summary>
/// Well-known Canon Custom Function IDs. These are camera-specific — the same setting
/// may have different IDs on different bodies. Values here are for common EOS models.
/// </summary>
public static class CanonCustomFunctionId
{
    // --- EOS 6D C.Fn IDs (also common on 5D2, 5D3, 7D) ---
    /// <summary>C.Fn II-1: Long exposure noise reduction. 0=Off, 1=Auto, 2=On.</summary>
    public const uint LongExposureNR_6D = 0x0102;

    /// <summary>C.Fn II-2: High ISO speed noise reduction. 0=Standard, 1=Low, 2=Strong, 3=Disable.</summary>
    public const uint HighIsoNR_6D = 0x0103;

    // --- EOS 450D / 1000D / Rebel-era C.Fn IDs ---
    /// <summary>C.Fn II-1: Long exposure noise reduction. 0=Off, 1=Auto, 2=On.</summary>
    public const uint LongExposureNR_Rebel = 1;

    /// <summary>C.Fn II-2: High ISO speed noise reduction. 0=Standard, 1=Low, 2=Strong, 3=Disable.</summary>
    public const uint HighIsoNR_Rebel = 2;

    /// <summary>C.Fn III-6 / III-7: Mirror lockup. 0=Disable, 1=Enable.</summary>
    public const uint MirrorLockup_Rebel = 7;
}
