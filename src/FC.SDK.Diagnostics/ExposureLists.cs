using FC.SDK;
using FC.SDK.Canon;

/// <summary>
/// The Av, Tv, ISO and exposure-compensation allowed lists, read as one snapshot. An increment C.Fn
/// shows in them (a 1/2-stop Tv list steps by 4, a 1-stop ISO list by 8), and on an R6 they are the
/// only witness: C.Fn1-1 and 1-2 share 0x0101, which holds whichever was set last.
/// </summary>
internal static class ExposureLists
{
    public static readonly (ushort Code, EdsPropertyId Id)[] Watched =
    [
        (0xD101, EdsPropertyId.Av), (0xD102, EdsPropertyId.Tv),
        (0xD103, EdsPropertyId.ISOSpeed), (0xD104, EdsPropertyId.ExposureCompensation),
    ];

    public static string Format(uint[]? values) =>
        values is null ? "none" : $"{values.Length}: {string.Join(",", values.Select(v => $"0x{v:X}"))}";

    /// <summary>Each list as <see cref="Format"/> prints it, keyed by PTP code.</summary>
    public static async Task<Dictionary<ushort, string>> SnapshotAsync(CanonCamera camera)
    {
        var lists = new Dictionary<ushort, string>();
        foreach (var (code, id) in Watched) lists[code] = Format(await camera.GetAllowedValuesAsync(id));
        return lists;
    }

    /// <summary>One line per list that differs between two snapshots.</summary>
    public static IEnumerable<string> Changes(Dictionary<ushort, string> before, Dictionary<ushort, string> after) =>
        Watched.Where(w => before[w.Code] != after[w.Code])
            .Select(w => $"allowed 0x{w.Code:X4} {w.Id}: {before[w.Code]}  ->  {after[w.Code]}");
}
