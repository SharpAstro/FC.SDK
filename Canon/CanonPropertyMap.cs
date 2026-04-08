using System.Collections.Frozen;

namespace FC.SDK.Canon;

internal static class CanonPropertyMap
{
    private static readonly FrozenDictionary<EdsPropertyId, (ushort PtpCode, int Size)> _map =
        new Dictionary<EdsPropertyId, (ushort, int)>
        {
            [EdsPropertyId.Av] = (0xD101, 4),
            [EdsPropertyId.Tv] = (0xD102, 4),
            [EdsPropertyId.ISOSpeed] = (0xD103, 4),
            [EdsPropertyId.ExposureCompensation] = (0xD104, 4),
            [EdsPropertyId.AEMode] = (0xD105, 4),
            [EdsPropertyId.DriveMode] = (0xD106, 4),
            [EdsPropertyId.MeteringMode] = (0xD107, 4),
            [EdsPropertyId.AFMode] = (0xD108, 4),
            [EdsPropertyId.WhiteBalance] = (0xD109, 4),
            [EdsPropertyId.ColorSpace] = (0xD10F, 4),
            [EdsPropertyId.PictureStyle] = (0xD110, 4),
            [EdsPropertyId.BatteryLevel] = (0xD111, 4),
            [EdsPropertyId.DateTime] = (0xD113, 4),
            [EdsPropertyId.OwnerName] = (0xD115, 4),
            [EdsPropertyId.SaveTo] = (0xD11C, 4),
            [EdsPropertyId.ImageQuality] = (0xD120, 4),
            [EdsPropertyId.Evf_OutputDevice] = (0xD1B0, 4),
            [EdsPropertyId.Evf_Mode] = (0xD1B1, 4),
            [EdsPropertyId.MirrorLockUpState] = (0xD1BF, 4),
            [EdsPropertyId.MirrorUpSetting] = (0xD1C1, 4),
            [EdsPropertyId.Artist] = (0xD1D0, 4),
            [EdsPropertyId.Copyright] = (0xD1D1, 4),
        }.ToFrozenDictionary();

    internal static bool TryGetPtpCode(EdsPropertyId id, out ushort ptpCode, out int size)
    {
        if (_map.TryGetValue(id, out var entry))
        {
            ptpCode = entry.PtpCode;
            size = entry.Size;
            return true;
        }
        ptpCode = 0;
        size = 0;
        return false;
    }

    internal static ushort GetPtpCodeOrThrow(EdsPropertyId id)
    {
        if (!TryGetPtpCode(id, out ushort ptpCode, out _))
            throw new NotSupportedException($"Property {id} has no known Canon PTP mapping.");
        return ptpCode;
    }
}
