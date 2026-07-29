namespace FC.SDK.Canon;

/// <summary>
/// Mirror of the camera's device-property state, fed exclusively by the GetEvent (0x9116) stream.
/// </summary>
/// <remarks>
/// EOS bodies expose no "read property" operation: standard PTP GetDevicePropValue (0x1015) is not
/// in their supported-operations list, and Canon's 0x9127 only *requests* that a value be pushed
/// into the event stream. So the only way to read an EOS property is to keep this mirror up to date
/// from PropertyChanged / AllowedValuesChanged events and answer reads out of it — the same design
/// EDSDK and libgphoto2 use.
/// </remarks>
internal sealed class CanonPropertyCache
{
    private readonly Lock _gate = new();
    private readonly Dictionary<ushort, uint> _values = [];
    private readonly Dictionary<ushort, byte[]> _rawValues = [];
    private readonly Dictionary<ushort, uint[]> _allowedValues = [];

    /// <summary>PTP property codes in the EOS vendor range, which only the event stream can supply.</summary>
    internal static bool IsEosVendorProperty(ushort ptpCode) =>
        (ptpCode & 0xFF00) is 0xD100 or 0xD200;

    internal bool TryGetValue(ushort ptpCode, out uint value)
    {
        lock (_gate)
        {
            return _values.TryGetValue(ptpCode, out value);
        }
    }

    internal void SetValue(ushort ptpCode, uint value)
    {
        lock (_gate)
        {
            _values[ptpCode] = value;
        }
    }

    /// <summary>
    /// The property's full value bytes. Composite properties — the packed custom-function block,
    /// owner/artist strings, AF-point structures — carry more than the uint32 surface can express.
    /// </summary>
    internal byte[]? GetRawValue(ushort ptpCode)
    {
        lock (_gate)
        {
            return _rawValues.TryGetValue(ptpCode, out var raw) ? raw : null;
        }
    }

    internal void SetValue(ushort ptpCode, uint value, byte[] rawValue)
    {
        lock (_gate)
        {
            _values[ptpCode] = value;
            _rawValues[ptpCode] = rawValue;
        }
    }

    /// <summary>The values the camera currently accepts for this property, or null if it never said.</summary>
    internal uint[]? GetAllowedValues(ushort ptpCode)
    {
        lock (_gate)
        {
            return _allowedValues.TryGetValue(ptpCode, out var values) ? values : null;
        }
    }

    internal void SetAllowedValues(ushort ptpCode, uint[] values)
    {
        lock (_gate)
        {
            _allowedValues[ptpCode] = values;
        }
    }

    internal bool Contains(ushort ptpCode)
    {
        lock (_gate)
        {
            return _values.ContainsKey(ptpCode);
        }
    }

    /// <summary>Every known property, ordered by PTP code — for diagnostics dumps.</summary>
    internal IReadOnlyList<(ushort PtpCode, uint Value, uint[]? AllowedValues)> Snapshot()
    {
        lock (_gate)
        {
            return [.. _values
                .OrderBy(kv => kv.Key)
                .Select(kv => (kv.Key, kv.Value, _allowedValues.TryGetValue(kv.Key, out var a) ? a : null))];
        }
    }

    internal int Count
    {
        get { lock (_gate) { return _values.Count; } }
    }

    internal void Clear()
    {
        lock (_gate)
        {
            _values.Clear();
            _rawValues.Clear();
            _allowedValues.Clear();
        }
    }
}
