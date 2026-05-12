using System;
using System.Collections.Generic;

namespace FC.SDK.Raw;

/// <summary>
/// Per-camera colour-pipeline data for Canon DSLRs and select PowerShots,
/// transcribed from dcraw's <c>adobe_coeff</c> table (public domain, Dave
/// Coffin's dcraw.c). The matrices and saturation levels are the canonical
/// reference values used by libraw, RawTherapee, darktable, and most other
/// FOSS raw processors.
///
/// <para>Lookup is by exact EXIF <c>Model</c> string (case-sensitive) — Canon
/// is consistent about this across firmware versions, so model strings like
/// "Canon EOS 6D" match what comes out of <see cref="SharpAstro.Exif.ExifMetadata.Model"/>.
/// Returns <c>null</c> for unknown models — callers should fall back to no
/// matrix (camera RGB passed through) and a default saturation of
/// <c>(1 &lt;&lt; bitDepth) - 1</c>.</para>
/// </summary>
public static class CanonCameraProfiles
{
    /// <summary>Look up the colour profile for a Canon camera by EXIF Model
    /// string. Returns <c>null</c> if the model isn't in the table.</summary>
    public static CanonCameraProfile? ResolveProfile(string? model)
    {
        if (string.IsNullOrEmpty(model)) return null;
        return _byModel.TryGetValue(model, out var p) ? p : null;
    }

    /// <summary>All profiles in the table, in dcraw's original ordering.</summary>
    public static IReadOnlyList<CanonCameraProfile> All => _all;

    private static readonly CanonCameraProfile[] _all =
    [
        new(Model: "Canon EOS D2000", BlackLevel: 0, MaxRaw: 0x0, CamToXyz: [24542, -10860, -3401, -1490, 11370, -297, 2858, -605, 3225]),
        new(Model: "Canon EOS D6000", BlackLevel: 0, MaxRaw: 0x0, CamToXyz: [20482, -7172, -3125, -1033, 10410, -285, 2542, 226, 3136]),
        new(Model: "Canon EOS D30", BlackLevel: 0, MaxRaw: 0x0, CamToXyz: [9805, -2689, -1312, -5803, 13064, 3068, -2438, 3075, 8775]),
        new(Model: "Canon EOS D60", BlackLevel: 0, MaxRaw: 0xFA0, CamToXyz: [6188, -1341, -890, -7168, 14489, 2937, -2640, 3228, 8483]),
        new(Model: "Canon EOS 5DS", BlackLevel: 0, MaxRaw: 0x3C96, CamToXyz: [6250, -711, -808, -5153, 12794, 2636, -1249, 2198, 5610]),
        new(Model: "Canon EOS 5D Mark III", BlackLevel: 0, MaxRaw: 0x3C80, CamToXyz: [6722, -635, -963, -4287, 12460, 2028, -908, 2162, 5668]),
        new(Model: "Canon EOS 5D Mark II", BlackLevel: 0, MaxRaw: 0x3CF0, CamToXyz: [4716, 603, -830, -7798, 15474, 2480, -1496, 1937, 6651]),
        new(Model: "Canon EOS 5D", BlackLevel: 0, MaxRaw: 0xE6C, CamToXyz: [6347, -479, -972, -8297, 15954, 2480, -1968, 2131, 7649]),
        new(Model: "Canon EOS 6D", BlackLevel: 0, MaxRaw: 0x3C82, CamToXyz: [7034, -804, -1014, -4420, 12564, 2058, -851, 1994, 5758]),
        new(Model: "Canon EOS 7D Mark II", BlackLevel: 0, MaxRaw: 0x3510, CamToXyz: [7268, -1082, -969, -4186, 11839, 2663, -825, 2029, 5839]),
        new(Model: "Canon EOS 7D", BlackLevel: 0, MaxRaw: 0x3510, CamToXyz: [6844, -996, -856, -3876, 11761, 2396, -593, 1772, 6198]),
        new(Model: "Canon EOS 10D", BlackLevel: 0, MaxRaw: 0xFA0, CamToXyz: [8197, -2000, -1118, -6714, 14335, 2592, -2536, 3178, 8266]),
        new(Model: "Canon EOS 20Da", BlackLevel: 0, MaxRaw: 0x0, CamToXyz: [14155, -5065, -1382, -6550, 14633, 2039, -1623, 1824, 6561]),
        new(Model: "Canon EOS 20D", BlackLevel: 0, MaxRaw: 0xFFF, CamToXyz: [6599, -537, -891, -8071, 15783, 2424, -1983, 2234, 7462]),
        new(Model: "Canon EOS 30D", BlackLevel: 0, MaxRaw: 0x0, CamToXyz: [6257, -303, -1000, -7880, 15621, 2396, -1714, 1904, 7046]),
        new(Model: "Canon EOS 40D", BlackLevel: 0, MaxRaw: 0x3F60, CamToXyz: [6071, -747, -856, -7653, 15365, 2441, -2025, 2553, 7315]),
        new(Model: "Canon EOS 50D", BlackLevel: 0, MaxRaw: 0x3D93, CamToXyz: [4920, 616, -593, -6493, 13964, 2784, -1774, 3178, 7005]),
        new(Model: "Canon EOS 60D", BlackLevel: 0, MaxRaw: 0x2FF7, CamToXyz: [6719, -994, -925, -4408, 12426, 2211, -887, 2129, 6051]),
        new(Model: "Canon EOS 70D", BlackLevel: 0, MaxRaw: 0x3BC7, CamToXyz: [7034, -804, -1014, -4420, 12564, 2058, -851, 1994, 5758]),
        new(Model: "Canon EOS 100D", BlackLevel: 0, MaxRaw: 0x350F, CamToXyz: [6602, -841, -939, -4472, 12458, 2247, -975, 2039, 6148]),
        new(Model: "Canon EOS 300D", BlackLevel: 0, MaxRaw: 0xFA0, CamToXyz: [8197, -2000, -1118, -6714, 14335, 2592, -2536, 3178, 8266]),
        new(Model: "Canon EOS 350D", BlackLevel: 0, MaxRaw: 0xFFF, CamToXyz: [6018, -617, -965, -8645, 15881, 2975, -1530, 1719, 7642]),
        new(Model: "Canon EOS 400D", BlackLevel: 0, MaxRaw: 0xE8E, CamToXyz: [7054, -1501, -990, -8156, 15544, 2812, -1278, 1414, 7796]),
        new(Model: "Canon EOS 450D", BlackLevel: 0, MaxRaw: 0x390D, CamToXyz: [5784, -262, -821, -7539, 15064, 2672, -1982, 2681, 7427]),
        new(Model: "Canon EOS 500D", BlackLevel: 0, MaxRaw: 0x3479, CamToXyz: [4763, 712, -646, -6821, 14399, 2640, -1921, 3276, 6561]),
        new(Model: "Canon EOS 550D", BlackLevel: 0, MaxRaw: 0x3DD7, CamToXyz: [6941, -1164, -857, -3825, 11597, 2534, -416, 1540, 6039]),
        new(Model: "Canon EOS 600D", BlackLevel: 0, MaxRaw: 0x3510, CamToXyz: [6461, -907, -882, -4300, 12184, 2378, -819, 1944, 5931]),
        new(Model: "Canon EOS 650D", BlackLevel: 0, MaxRaw: 0x354D, CamToXyz: [6602, -841, -939, -4472, 12458, 2247, -975, 2039, 6148]),
        new(Model: "Canon EOS 700D", BlackLevel: 0, MaxRaw: 0x3C00, CamToXyz: [6602, -841, -939, -4472, 12458, 2247, -975, 2039, 6148]),
        new(Model: "Canon EOS 750D", BlackLevel: 0, MaxRaw: 0x368E, CamToXyz: [6362, -823, -847, -4426, 12109, 2616, -743, 1857, 5635]),
        new(Model: "Canon EOS 760D", BlackLevel: 0, MaxRaw: 0x350F, CamToXyz: [6362, -823, -847, -4426, 12109, 2616, -743, 1857, 5635]),
        new(Model: "Canon EOS 1000D", BlackLevel: 0, MaxRaw: 0xE43, CamToXyz: [6771, -1139, -977, -7818, 15123, 2928, -1244, 1437, 7533]),
        new(Model: "Canon EOS 1100D", BlackLevel: 0, MaxRaw: 0x3510, CamToXyz: [6444, -904, -893, -4563, 12308, 2535, -903, 2016, 6728]),
        new(Model: "Canon EOS 1200D", BlackLevel: 0, MaxRaw: 0x37C2, CamToXyz: [6461, -907, -882, -4300, 12184, 2378, -819, 1944, 5931]),
        new(Model: "Canon EOS M3", BlackLevel: 0, MaxRaw: 0x0, CamToXyz: [6362, -823, -847, -4426, 12109, 2616, -743, 1857, 5635]),
        new(Model: "Canon EOS M", BlackLevel: 0, MaxRaw: 0x0, CamToXyz: [6602, -841, -939, -4472, 12458, 2247, -975, 2039, 6148]),
        new(Model: "Canon EOS-1Ds Mark III", BlackLevel: 0, MaxRaw: 0x3BB0, CamToXyz: [5859, -211, -930, -8255, 16017, 2353, -1732, 1887, 7448]),
        new(Model: "Canon EOS-1Ds Mark II", BlackLevel: 0, MaxRaw: 0xE80, CamToXyz: [6517, -602, -867, -8180, 15926, 2378, -1618, 1771, 7633]),
        new(Model: "Canon EOS-1D Mark IV", BlackLevel: 0, MaxRaw: 0x3BB0, CamToXyz: [6014, -220, -795, -4109, 12014, 2361, -561, 1824, 5787]),
        new(Model: "Canon EOS-1D Mark III", BlackLevel: 0, MaxRaw: 0x3BB0, CamToXyz: [6291, -540, -976, -8350, 16145, 2311, -1714, 1858, 7326]),
        new(Model: "Canon EOS-1D Mark II N", BlackLevel: 0, MaxRaw: 0xE80, CamToXyz: [6240, -466, -822, -8180, 15825, 2500, -1801, 1938, 8042]),
        new(Model: "Canon EOS-1D Mark II", BlackLevel: 0, MaxRaw: 0xE80, CamToXyz: [6264, -582, -724, -8312, 15948, 2504, -1744, 1919, 8664]),
        new(Model: "Canon EOS-1DS", BlackLevel: 0, MaxRaw: 0xE20, CamToXyz: [4374, 3631, -1743, -7520, 15212, 2472, -2892, 3632, 8161]),
        new(Model: "Canon EOS-1D C", BlackLevel: 0, MaxRaw: 0x3C4E, CamToXyz: [6847, -614, -1014, -4669, 12737, 2139, -1197, 2488, 6846]),
        new(Model: "Canon EOS-1D X", BlackLevel: 0, MaxRaw: 0x3C4E, CamToXyz: [6847, -614, -1014, -4669, 12737, 2139, -1197, 2488, 6846]),
        new(Model: "Canon EOS-1D", BlackLevel: 0, MaxRaw: 0xE20, CamToXyz: [6806, -179, -1020, -8097, 16415, 1687, -3267, 4236, 7690]),
        new(Model: "Canon PowerShot G10", BlackLevel: 0, MaxRaw: 0x0, CamToXyz: [11093, -3906, -1028, -5047, 12492, 2879, -1003, 1750, 5561]),
        new(Model: "Canon PowerShot G11", BlackLevel: 0, MaxRaw: 0x0, CamToXyz: [12177, -4817, -1069, -1612, 9864, 2049, -98, 850, 4471]),
        new(Model: "Canon PowerShot G12", BlackLevel: 0, MaxRaw: 0x0, CamToXyz: [13244, -5501, -1248, -1508, 9858, 1935, -270, 1083, 4366]),
        new(Model: "Canon PowerShot G15", BlackLevel: 0, MaxRaw: 0x0, CamToXyz: [7474, -2301, -567, -4056, 11456, 2975, -222, 716, 4181]),
        new(Model: "Canon PowerShot G16", BlackLevel: 0, MaxRaw: 0x0, CamToXyz: [8020, -2687, -682, -3704, 11879, 2052, -965, 1921, 5556]),
        new(Model: "Canon PowerShot G1 X", BlackLevel: 0, MaxRaw: 0x0, CamToXyz: [7378, -1255, -1043, -4088, 12251, 2048, -876, 1946, 5805]),
        new(Model: "Canon PowerShot G2", BlackLevel: 0, MaxRaw: 0x0, CamToXyz: [9087, -2693, -1049, -6715, 14382, 2537, -2291, 2819, 7790]),
        new(Model: "Canon PowerShot G3", BlackLevel: 0, MaxRaw: 0x0, CamToXyz: [9212, -2781, -1073, -6573, 14189, 2605, -2300, 2844, 7664]),
        new(Model: "Canon PowerShot G5", BlackLevel: 0, MaxRaw: 0x0, CamToXyz: [9757, -2872, -933, -5972, 13861, 2301, -1622, 2328, 7212]),
        new(Model: "Canon PowerShot G6", BlackLevel: 0, MaxRaw: 0x0, CamToXyz: [9877, -3775, -871, -7613, 14807, 3072, -1448, 1305, 7485]),
        new(Model: "Canon PowerShot G7 X", BlackLevel: 0, MaxRaw: 0x0, CamToXyz: [9602, -3823, -937, -2984, 11495, 1675, -407, 1415, 5049]),
        new(Model: "Canon PowerShot G9", BlackLevel: 0, MaxRaw: 0x0, CamToXyz: [7368, -2141, -598, -5621, 13254, 2625, -1418, 1696, 5743]),
        new(Model: "Canon PowerShot Pro1", BlackLevel: 0, MaxRaw: 0x0, CamToXyz: [10062, -3522, -999, -7643, 15117, 2730, -765, 817, 7323]),
        new(Model: "Canon PowerShot S30", BlackLevel: 0, MaxRaw: 0x0, CamToXyz: [10566, -3652, -1129, -6552, 14662, 2006, -2197, 2581, 7670]),
        new(Model: "Canon PowerShot S40", BlackLevel: 0, MaxRaw: 0x0, CamToXyz: [8510, -2487, -940, -6869, 14231, 2900, -2318, 2829, 9013]),
        new(Model: "Canon PowerShot S45", BlackLevel: 0, MaxRaw: 0x0, CamToXyz: [8163, -2333, -955, -6682, 14174, 2751, -2077, 2597, 8041]),
        new(Model: "Canon PowerShot S50", BlackLevel: 0, MaxRaw: 0x0, CamToXyz: [8882, -2571, -863, -6348, 14234, 2288, -1516, 2172, 6569]),
        new(Model: "Canon PowerShot S60", BlackLevel: 0, MaxRaw: 0x0, CamToXyz: [8795, -2482, -797, -7804, 15403, 2573, -1422, 1996, 7082]),
        new(Model: "Canon PowerShot S70", BlackLevel: 0, MaxRaw: 0x0, CamToXyz: [9976, -3810, -832, -7115, 14463, 2906, -901, 989, 7889]),
        new(Model: "Canon PowerShot S90", BlackLevel: 0, MaxRaw: 0x0, CamToXyz: [12374, -5016, -1049, -1677, 9902, 2078, -83, 852, 4683]),
        new(Model: "Canon PowerShot S95", BlackLevel: 0, MaxRaw: 0x0, CamToXyz: [13440, -5896, -1279, -1236, 9598, 1931, -180, 1001, 4651]),
        new(Model: "Canon PowerShot S100", BlackLevel: 0, MaxRaw: 0x0, CamToXyz: [7968, -2565, -636, -2873, 10697, 2513, 180, 667, 4211]),
        new(Model: "Canon PowerShot S110", BlackLevel: 0, MaxRaw: 0x0, CamToXyz: [8039, -2643, -654, -3783, 11230, 2930, -206, 690, 4194]),
        new(Model: "Canon PowerShot S120", BlackLevel: 0, MaxRaw: 0x0, CamToXyz: [6961, -1685, -695, -4625, 12945, 1836, -1114, 2152, 5518]),
        new(Model: "Canon PowerShot SX1 IS", BlackLevel: 0, MaxRaw: 0x0, CamToXyz: [6578, -259, -502, -5974, 13030, 3309, -308, 1058, 4970]),
        new(Model: "Canon PowerShot SX50 HS", BlackLevel: 0, MaxRaw: 0x0, CamToXyz: [12432, -4753, -1247, -2110, 10691, 1629, -412, 1623, 4926]),
        new(Model: "Canon PowerShot SX60 HS", BlackLevel: 0, MaxRaw: 0x0, CamToXyz: [13161, -5451, -1344, -1989, 10654, 1531, -47, 1271, 4955]),
    ];

    private static readonly Dictionary<string, CanonCameraProfile> _byModel = BuildIndex(_all);

    private static Dictionary<string, CanonCameraProfile> BuildIndex(CanonCameraProfile[] all)
    {
        var d = new Dictionary<string, CanonCameraProfile>(all.Length, StringComparer.Ordinal);
        foreach (var p in all) d[p.Model] = p;
        return d;
    }
}
