using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using System.Runtime.Versioning;

namespace FC.SDK.Transport;

[SupportedOSPlatform("windows")]
internal static partial class WpdInterop
{
    // CoClass CLSIDs
    internal static readonly Guid CLSID_PortableDeviceManager = new("0af10cec-2ecd-4b92-9581-34f6ae0637f3");
    internal static readonly Guid CLSID_PortableDevice = new("728a21c5-3d9e-48d7-9810-864848f0f404");
    internal static readonly Guid CLSID_PortableDeviceValues = new("0c15d503-d017-47ce-9016-7b3f978721cc");
    internal static readonly Guid CLSID_PortableDevicePropVariantCollection = new("08a99e2f-6d6d-4b80-af5a-baf2bcbe4cb9");

    // WPD command GUIDs
    internal static readonly Guid WPD_COMMAND_COMMON = new("F0422A9C-5DC8-4440-B5BD-5DF28835658A");
    internal static readonly Guid WPD_COMMAND_MTP_EXT = new("4d545058-1a2e-4106-a357-771e0819fc56");

    // Common property PIDs
    internal const uint PID_COMMAND_CATEGORY = 1001;
    internal const uint PID_COMMAND_ID = 1002;
    internal const uint PID_HRESULT = 1003;

    // MTP EXT command PIDs
    internal const uint PID_EXECUTE_NO_DATA = 12;
    internal const uint PID_EXECUTE_DATA_READ = 13;
    internal const uint PID_EXECUTE_DATA_WRITE = 14;
    internal const uint PID_READ_DATA = 15;
    internal const uint PID_WRITE_DATA = 16;
    internal const uint PID_END_DATA_TRANSFER = 17;

    // MTP EXT property PIDs
    internal const uint PID_OPERATION_CODE = 1001;
    internal const uint PID_OPERATION_PARAMS = 1002;
    internal const uint PID_RESPONSE_CODE = 1003;
    internal const uint PID_RESPONSE_PARAMS = 1004;
    internal const uint PID_TRANSFER_CONTEXT = 1006;
    internal const uint PID_TRANSFER_TOTAL_SIZE = 1007;
    internal const uint PID_TRANSFER_NUM_BYTES_TO_READ = 1008;
    internal const uint PID_TRANSFER_NUM_BYTES_TO_WRITE = 1010;
    internal const uint PID_TRANSFER_DATA = 1012;

    internal static PropertyKey CommonKey(uint pid) => new() { fmtid = WPD_COMMAND_COMMON, pid = pid };
    internal static PropertyKey MtpExtKey(uint pid) => new() { fmtid = WPD_COMMAND_MTP_EXT, pid = pid };

    [LibraryImport("ole32.dll")]
    internal static partial int CoCreateInstance(
        in Guid rclsid, nint pUnkOuter, uint dwClsContext, in Guid riid, out nint ppv);

    internal const uint CLSCTX_INPROC_SERVER = 1;

    private static readonly StrategyBasedComWrappers s_comWrappers = new();

    internal static T CreateInstance<T>(in Guid clsid) where T : class
    {
        Guid iid = typeof(T).GUID;
        int hr = CoCreateInstance(in clsid, 0, CLSCTX_INPROC_SERVER, in iid, out nint ptr);
        Marshal.ThrowExceptionForHR(hr);
        return (T)s_comWrappers.GetOrCreateObjectForComInstance(ptr, CreateObjectFlags.None);
    }
}

[StructLayout(LayoutKind.Sequential)]
internal struct PropertyKey
{
    public Guid fmtid;
    public uint pid;
}

[StructLayout(LayoutKind.Sequential)]
internal struct PropVariant
{
    public ushort vt;
    public ushort wReserved1;
    public ushort wReserved2;
    public ushort wReserved3;
    public long data;

    public static PropVariant FromUInt32(uint value) => new() { vt = 19 /* VT_UI4 */, data = value };
    public uint AsUInt32 => (uint)data;
}

// IPortableDeviceManager — {a1567595-4c2f-4574-a6fa-ecef917b9a40}
// vtable after IUnknown: GetDevices, RefreshDeviceList, GetDeviceFriendlyName, GetDeviceDescription, GetDeviceManufacturer
[GeneratedComInterface]
[Guid("a1567595-4c2f-4574-a6fa-ecef917b9a40")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal partial interface IWpdDeviceManager
{
    [PreserveSig]
    int GetDevices(nint pPnPDeviceIDs, ref uint pcPnPDeviceIDs);

    [PreserveSig]
    int RefreshDeviceList();

    [PreserveSig]
    int GetDeviceFriendlyName(
        [MarshalAs(UnmanagedType.LPWStr)] string pszPnPDeviceID,
        nint pDeviceFriendlyName,
        ref uint pcchDeviceFriendlyName);

    [PreserveSig]
    int GetDeviceDescription(
        [MarshalAs(UnmanagedType.LPWStr)] string pszPnPDeviceID,
        nint pDeviceDescription,
        ref uint pcchDeviceDescription);

    [PreserveSig]
    int GetDeviceManufacturer(
        [MarshalAs(UnmanagedType.LPWStr)] string pszPnPDeviceID,
        nint pDeviceManufacturer,
        ref uint pcchDeviceManufacturer);
}

// IPortableDeviceValues — {6848f6f2-3155-4f86-b6f5-263eeeab3143}
// Full vtable order (43 methods after IUnknown)
[GeneratedComInterface]
[Guid("6848f6f2-3155-4f86-b6f5-263eeeab3143")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal partial interface IWpdValues
{
    // 0: GetCount
    [PreserveSig] int GetCount(out uint pcelt);
    // 1: GetAt
    [PreserveSig] int GetAt(uint index, out PropertyKey pKey, out PropVariant pValue);
    // 2: SetValue
    [PreserveSig] int SetValue(in PropertyKey key, in PropVariant pValue);
    // 3: GetValue
    [PreserveSig] int GetValue(in PropertyKey key, out PropVariant pValue);
    // 4: SetStringValue
    [PreserveSig] int SetStringValue(in PropertyKey key, [MarshalAs(UnmanagedType.LPWStr)] string value);
    // 5: GetStringValue
    [PreserveSig] int GetStringValue(in PropertyKey key, [MarshalAs(UnmanagedType.LPWStr)] out string value);
    // 6: SetUnsignedIntegerValue
    [PreserveSig] int SetUnsignedIntegerValue(in PropertyKey key, uint value);
    // 7: GetUnsignedIntegerValue
    [PreserveSig] int GetUnsignedIntegerValue(in PropertyKey key, out uint value);
    // 8: SetSignedIntegerValue
    [PreserveSig] int SetSignedIntegerValue(in PropertyKey key, int value);
    // 9: GetSignedIntegerValue
    [PreserveSig] int GetSignedIntegerValue(in PropertyKey key, out int value);
    // 10: SetUnsignedLargeIntegerValue
    [PreserveSig] int SetUnsignedLargeIntegerValue(in PropertyKey key, ulong value);
    // 11: GetUnsignedLargeIntegerValue
    [PreserveSig] int GetUnsignedLargeIntegerValue(in PropertyKey key, out ulong value);
    // 12: SetSignedLargeIntegerValue
    [PreserveSig] int SetSignedLargeIntegerValue(in PropertyKey key, long value);
    // 13: GetSignedLargeIntegerValue
    [PreserveSig] int GetSignedLargeIntegerValue(in PropertyKey key, out long value);
    // 14: SetFloatValue
    [PreserveSig] int SetFloatValue(in PropertyKey key, float value);
    // 15: GetFloatValue
    [PreserveSig] int GetFloatValue(in PropertyKey key, out float value);
    // 16: SetErrorValue
    [PreserveSig] int SetErrorValue(in PropertyKey key, int value);
    // 17: GetErrorValue
    [PreserveSig] int GetErrorValue(in PropertyKey key, out int value);
    // 18: SetKeyValue
    [PreserveSig] int SetKeyValue(in PropertyKey key, in PropertyKey value);
    // 19: GetKeyValue
    [PreserveSig] int GetKeyValue(in PropertyKey key, out PropertyKey value);
    // 20: SetBoolValue
    [PreserveSig] int SetBoolValue(in PropertyKey key, int value);
    // 21: GetBoolValue
    [PreserveSig] int GetBoolValue(in PropertyKey key, out int value);
    // 22: SetIUnknownValue
    [PreserveSig] int SetIUnknownValue(in PropertyKey key, [MarshalAs(UnmanagedType.Interface)] object value);
    // 23: GetIUnknownValue
    [PreserveSig] int GetIUnknownValue(in PropertyKey key, [MarshalAs(UnmanagedType.Interface)] out object? value);
    // 24: SetGuidValue
    [PreserveSig] int SetGuidValue(in PropertyKey key, in Guid value);
    // 25: GetGuidValue
    [PreserveSig] int GetGuidValue(in PropertyKey key, out Guid value);
    // 26: SetBufferValue
    [PreserveSig] int SetBufferValue(in PropertyKey key, [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 2)] byte[] value, uint cbValue);
    // 27: GetBufferValue
    [PreserveSig] int GetBufferValue(in PropertyKey key, out nint ppValue, out uint pcbValue);
    // 28: SetIPortableDeviceValuesValue
    [PreserveSig] int SetIPortableDeviceValuesValue(in PropertyKey key, IWpdValues value);
    // 29: GetIPortableDeviceValuesValue
    [PreserveSig] int GetIPortableDeviceValuesValue(in PropertyKey key, out IWpdValues? value);
    // 30: SetIPortableDevicePropVariantCollectionValue
    [PreserveSig] int SetIPortableDevicePropVariantCollectionValue(in PropertyKey key, IWpdPropVariantCollection value);
    // 31: GetIPortableDevicePropVariantCollectionValue
    [PreserveSig] int GetIPortableDevicePropVariantCollectionValue(in PropertyKey key, out IWpdPropVariantCollection? value);
    // 32: SetIPortableDeviceKeyCollectionValue
    [PreserveSig] int SetIPortableDeviceKeyCollectionValue(in PropertyKey key, nint value);
    // 33: GetIPortableDeviceKeyCollectionValue
    [PreserveSig] int GetIPortableDeviceKeyCollectionValue(in PropertyKey key, out nint value);
    // 34: SetIPortableDeviceValuesCollectionValue
    [PreserveSig] int SetIPortableDeviceValuesCollectionValue(in PropertyKey key, nint value);
    // 35: GetIPortableDeviceValuesCollectionValue
    [PreserveSig] int GetIPortableDeviceValuesCollectionValue(in PropertyKey key, out nint value);
    // 36: RemoveValue
    [PreserveSig] int RemoveValue(in PropertyKey key);
    // 37: CopyValuesFromPropertyStore
    [PreserveSig] int CopyValuesFromPropertyStore(nint pStore);
    // 38: CopyValuesToPropertyStore
    [PreserveSig] int CopyValuesToPropertyStore(nint pStore);
    // 39: Clear
    [PreserveSig] int Clear();
}

// IPortableDevicePropVariantCollection — {89b2e422-4f1b-4316-bcef-a44afea83eb3}
[GeneratedComInterface]
[Guid("89b2e422-4f1b-4316-bcef-a44afea83eb3")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal partial interface IWpdPropVariantCollection
{
    // 0: GetCount
    [PreserveSig] int GetCount(out uint pcElems);
    // 1: GetAt
    [PreserveSig] int GetAt(uint dwIndex, out PropVariant pValue);
    // 2: Add
    [PreserveSig] int Add(in PropVariant pValue);
    // 3: GetType
    [PreserveSig] int GetType(out ushort pvt);
    // 4: ChangeType
    [PreserveSig] int ChangeType(ushort vt);
    // 5: Clear
    [PreserveSig] int Clear();
    // 6: RemoveAt
    [PreserveSig] int RemoveAt(uint dwIndex);
}

// IPortableDevice — {625e2df8-6392-4cf0-9ad1-3cfa5f17775c}
[GeneratedComInterface]
[Guid("625e2df8-6392-4cf0-9ad1-3cfa5f17775c")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal partial interface IWpdDevice
{
    // 0: Open
    [PreserveSig] int Open([MarshalAs(UnmanagedType.LPWStr)] string pszPnPDeviceID, IWpdValues pClientInfo);
    // 1: SendCommand
    [PreserveSig] int SendCommand(uint dwFlags, IWpdValues pParameters, out IWpdValues? ppResults);
    // 2: Content
    [PreserveSig] int Content(out nint ppContent);
    // 3: Capabilities
    [PreserveSig] int Capabilities(out nint ppCapabilities);
    // 4: Cancel
    [PreserveSig] int Cancel();
    // 5: Close
    [PreserveSig] int Close();
    // 6: Advise
    [PreserveSig] int Advise(uint dwFlags, nint pCallback, nint pParameters, [MarshalAs(UnmanagedType.LPWStr)] out string ppszCookie);
    // 7: Unadvise
    [PreserveSig] int Unadvise([MarshalAs(UnmanagedType.LPWStr)] string pszCookie);
    // 8: GetPnPDeviceID
    [PreserveSig] int GetPnPDeviceID([MarshalAs(UnmanagedType.LPWStr)] out string ppszPnPDeviceID);
}
