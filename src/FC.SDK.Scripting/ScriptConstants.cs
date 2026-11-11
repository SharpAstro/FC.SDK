using System.Collections.Frozen;
using FC.SDK.Canon;

namespace FC.SDK.Scripting;

/// <summary>
/// The names a script can use without defining them: every member of the SDK's value enums, and
/// the property ids.
/// </summary>
/// <remarks>
/// <para>
/// An enum member is written <c>Type.Member</c>, with or without the type's <c>Eds</c> or
/// <c>Canon</c> prefix: <c>EdsEvfOutputDevice.PC</c> and <c>EvfOutputDevice.PC</c> are the same
/// constant. Names are case-sensitive, as in C#.
/// </para>
/// <para>
/// A property id resolves to its <b>PTP code</b>, not its EDSDK number, because the code is what
/// <c>read</c> and <c>set</c> take: <c>EdsPropertyId.Evf_OutputDevice</c>, <c>PropertyId.Evf_OutputDevice</c>
/// and the bare <c>Evf_OutputDevice</c> are all <c>0xD1B0</c>. Only ids the SDK has a verified code
/// for exist here; an unmapped one is an unknown name, and the script says so before it runs. A
/// raw code (<c>0xD1B0</c>) always works.
/// </para>
/// <para>
/// Built through the generic <c>Enum.GetNames</c>/<c>GetValues</c>, which need no reflection at run
/// time, so the table survives a NativeAOT publish.
/// </para>
/// </remarks>
public static class ScriptConstants
{
    private static readonly FrozenDictionary<string, uint> Table = Build();

    /// <summary>Every predefined name, for a help listing.</summary>
    public static IEnumerable<string> Names => Table.Keys;

    /// <summary>Looks a predefined name up.</summary>
    public static bool TryGet(string name, out uint value) => Table.TryGetValue(name, out value);

    private static FrozenDictionary<string, uint> Build()
    {
        var table = new Dictionary<string, uint>(StringComparer.Ordinal)
        {
            ["true"] = 1,
            ["false"] = 0,
        };

        Add<EdsEvfOutputDevice>(table);
        Add<EdsISOSpeed>(table);
        Add<EdsTv>(table);
        Add<EdsAv>(table);
        Add<EdsAEMode>(table);
        Add<EdsSaveTo>(table);
        Add<CanonCaptureDestination>(table);
        Add<EdsMeteringMode>(table);
        Add<EdsDriveMode>(table);
        Add<EdsAFMode>(table);
        Add<EdsWhiteBalance>(table);
        Add<CanonEvfAfSystem>(table);
        Add<CanonEvfZoom>(table);
        Add<EdsEvfDepthOfFieldPreview>(table);
        Add<EdsLongExposureNR>(table);
        Add<EdsHighIsoNR>(table);
        Add<EdsMirrorUpSetting>(table);
        Add<EdsMirrorLockupState>(table);
        Add<EdsDriveLensStep>(table);
        Add<EdsError>(table);

        foreach (var id in Enum.GetValues<EdsPropertyId>())
        {
            if (!CanonPropertyMap.TryGetPtpCode(id, out var code, out _)) continue;

            var name = id.ToString();
            table[$"EdsPropertyId.{name}"] = code;
            table[$"PropertyId.{name}"] = code;
            // The bare name last, and never over something already there.
            table.TryAdd(name, code);
        }

        return table.ToFrozenDictionary(StringComparer.Ordinal);
    }

    private static void Add<T>(Dictionary<string, uint> table) where T : struct, Enum
    {
        var type = typeof(T).Name;
        var shortType = type.StartsWith("Eds", StringComparison.Ordinal) ? type[3..]
            : type.StartsWith("Canon", StringComparison.Ordinal) ? type[5..]
            : null;

        var names = Enum.GetNames<T>();
        var values = Enum.GetValues<T>();
        for (var i = 0; i < names.Length; i++)
        {
            var value = Convert.ToUInt32(values[i]);
            table[$"{type}.{names[i]}"] = value;
            if (shortType is not null) table[$"{shortType}.{names[i]}"] = value;
        }
    }
}
