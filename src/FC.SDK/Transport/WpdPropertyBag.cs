using System.Buffers.Binary;
using System.Runtime.Versioning;
using System.Text;

namespace FC.SDK.Transport;

/// <summary>
/// OLE variant types used by the WPD ioctl property-bag encoding. Real VARENUM values — the format
/// is Microsoft's own serialization of <c>IPortableDeviceValues</c>, not anything Canon-specific.
/// </summary>
internal static class WpdVarType
{
    internal const uint Error = 10;    // VT_ERROR — an HRESULT
    internal const uint Unknown = 13;  // VT_UNKNOWN — a nested COM object, identified by its CLSID
    internal const uint UI1 = 17;      // only ever seen vectored, as the data-phase payload
    internal const uint UI4 = 19;
    internal const uint UI8 = 21;
    internal const uint LPWSTR = 31;
    internal const uint CLSID = 72;
    internal const uint Vector = 0x1000;
}

/// <summary>
/// Writes the property bag an <c>IOCTL_WPD_MESSAGE_*</c> call takes as its input buffer — the same
/// bytes <c>PortableDeviceApi.dll</c> would have serialized had the call gone through
/// <c>IPortableDevice::SendCommand</c>. Decoded and verified byte-exact against captured EDSDK
/// traffic; see <c>docs/wpd-ioctl-wire-format.md</c>.
/// </summary>
/// <remarks>
/// A struct over a caller-owned array so the transport can keep one buffer for the whole session
/// rather than allocating per command. It grows by doubling and hands the (possibly reallocated)
/// array back through <see cref="Buffer"/>, so a caller that caches must re-read that field after
/// building. Nothing here is thread-safe; the transport's command gate serialises use.
/// </remarks>
[SupportedOSPlatform("windows")]
internal struct WpdBagWriter(byte[] buffer)
{
    private byte[] _buffer = buffer;
    private int _length;

    /// <summary>The bytes written so far — what goes in the ioctl input buffer.</summary>
    internal readonly ReadOnlySpan<byte> Written => _buffer.AsSpan(0, _length);

    internal readonly int Length => _length;

    /// <summary>
    /// The backing array, which is NOT necessarily the one passed in: a grow replaces it. Callers
    /// that cache the buffer must read this back after building a command.
    /// </summary>
    internal readonly byte[] Buffer => _buffer;

    private Span<byte> Advance(int count)
    {
        if (_buffer.Length - _length < count)
            Array.Resize(ref _buffer, Math.Max(_buffer.Length * 2, _length + count));

        var span = _buffer.AsSpan(_length, count);
        _length += count;
        return span;
    }

    private void WriteUInt32(uint value) => BinaryPrimitives.WriteUInt32LittleEndian(Advance(4), value);

    private void WriteGuid(Guid value) => value.TryWriteBytes(Advance(16));

    /// <summary>
    /// Opens an <c>IPortableDeviceValues</c> bag with a fixed entry count. The count is written up
    /// front and never patched, so callers state it rather than discovering it — every command shape
    /// here is known in advance.
    /// </summary>
    internal void BeginValues(int entryCount)
    {
        WriteUInt32(WpdVarType.Unknown);
        WriteGuid(WpdInterop.CLSID_PortableDeviceValues);
        WriteUInt32((uint)entryCount);
    }

    /// <summary>Writes the PROPERTYKEY that precedes each entry's value inside a values bag.</summary>
    internal void Key(Guid fmtid, uint pid)
    {
        WriteGuid(fmtid);
        WriteUInt32(pid);
    }

    /// <summary>Opens an <c>IPortableDevicePropVariantCollection</c> of <paramref name="count"/> items.</summary>
    internal void BeginCollection(int count)
    {
        WriteUInt32(WpdVarType.Unknown);
        WriteGuid(WpdInterop.CLSID_PortableDevicePropVariantCollection);
        WriteUInt32((uint)count);
    }

    internal void UInt32Value(uint value)
    {
        WriteUInt32(WpdVarType.UI4);
        WriteUInt32(value);
    }

    internal void UInt64Value(ulong value)
    {
        WriteUInt32(WpdVarType.UI8);
        BinaryPrimitives.WriteUInt64LittleEndian(Advance(8), value);
    }

    internal void GuidValue(Guid value)
    {
        WriteUInt32(WpdVarType.CLSID);
        WriteGuid(value);
    }

    /// <summary>Writes a string as a byte-counted, NUL-terminated UTF-16 run.</summary>
    internal void StringValue(string value)
    {
        int bytes = Encoding.Unicode.GetByteCount(value) + 2; // + the terminator
        WriteUInt32(WpdVarType.LPWSTR);
        WriteUInt32((uint)bytes);
        var span = Advance(bytes);
        Encoding.Unicode.GetBytes(value, span);
        span[^2] = 0;
        span[^1] = 0;
    }

    /// <summary>Writes a byte vector, copying <paramref name="data"/> in.</summary>
    internal void BytesValue(ReadOnlySpan<byte> data)
    {
        WriteUInt32(WpdVarType.Vector | WpdVarType.UI1);
        WriteUInt32((uint)data.Length);
        data.CopyTo(Advance(data.Length));
    }

    /// <summary>
    /// Reserves a byte vector of <paramref name="length"/> without writing its contents — the landing
    /// area a READ_DATA call hands the driver to fill.
    /// </summary>
    /// <remarks>
    /// Deliberately not cleared. It is scratch space the driver overwrites, and zeroing 2 MiB per
    /// live-view frame is exactly the kind of per-frame cost this transport exists to avoid. Whatever
    /// stale bytes it carries came from this process's own previous request.
    /// </remarks>
    internal void ReserveBytesValue(int length)
    {
        WriteUInt32(WpdVarType.Vector | WpdVarType.UI1);
        WriteUInt32((uint)length);
        Advance(length);
    }
}

/// <summary>
/// Reads the property bag an <c>IOCTL_WPD_MESSAGE_*</c> call writes back, by seeking the one key the
/// caller wants rather than materialising a dictionary.
/// </summary>
/// <remarks>
/// A ref struct over the raw output buffer: a live-view frame's payload is found and copied exactly
/// once, straight out of the driver's own bytes. The response side of the grammar is inferred by
/// symmetry with the request side, which is capture-confirmed; every field this transport reads has
/// since been exercised against a real body.
/// </remarks>
[SupportedOSPlatform("windows")]
internal readonly ref struct WpdBagReader(ReadOnlySpan<byte> buffer)
{
    private readonly ReadOnlySpan<byte> _buffer = buffer;

    internal bool IsEmpty => _buffer.Length < 24;

    /// <summary>Finds an entry's raw value, positioned at its variant type word.</summary>
    private bool TryFind(Guid fmtid, uint pid, out ReadOnlySpan<byte> value)
    {
        value = default;
        if (IsEmpty) return false;

        int offset = 0;
        if (ReadUInt32(_buffer, ref offset) is not WpdVarType.Unknown) return false;
        if (ReadGuid(_buffer, ref offset) != WpdInterop.CLSID_PortableDeviceValues) return false;

        uint count = ReadUInt32(_buffer, ref offset);
        for (uint i = 0; i < count; i++)
        {
            var entryFmtid = ReadGuid(_buffer, ref offset);
            uint entryPid = ReadUInt32(_buffer, ref offset);
            int valueAt = offset;
            offset = SkipValue(_buffer, offset);

            if (entryPid == pid && entryFmtid == fmtid)
            {
                value = _buffer[valueAt..offset];
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Reads an unsigned integer, accepting either width. The driver answers TRANSFER_TOTAL_SIZE as
    /// VT_UI4 on a read and VT_UI8 on a write, and a caller has no reason to care which.
    /// </summary>
    internal bool TryGetUInt32(Guid fmtid, uint pid, out uint value)
    {
        value = 0;
        if (!TryFind(fmtid, pid, out var raw)) return false;

        int offset = 0;
        switch (ReadUInt32(raw, ref offset))
        {
            case WpdVarType.UI4:
                value = ReadUInt32(raw, ref offset);
                return true;
            case WpdVarType.UI8:
                value = (uint)BinaryPrimitives.ReadUInt64LittleEndian(raw[offset..]);
                return true;
            default:
                return false;
        }
    }

    /// <summary>Reads a VT_ERROR HRESULT — how the driver reports failure of the command itself.</summary>
    internal bool TryGetHResult(Guid fmtid, uint pid, out int value)
    {
        value = 0;
        if (!TryFind(fmtid, pid, out var raw)) return false;

        int offset = 0;
        if (ReadUInt32(raw, ref offset) is not WpdVarType.Error) return false;
        value = BinaryPrimitives.ReadInt32LittleEndian(raw[offset..]);
        return true;
    }

    internal bool TryGetString(Guid fmtid, uint pid, out string value)
    {
        value = string.Empty;
        if (!TryFind(fmtid, pid, out var raw)) return false;

        int offset = 0;
        if (ReadUInt32(raw, ref offset) is not WpdVarType.LPWSTR) return false;
        int bytes = (int)ReadUInt32(raw, ref offset);
        value = Encoding.Unicode.GetString(raw.Slice(offset, bytes)).TrimEnd('\0');
        return true;
    }

    /// <summary>Borrows the data-phase payload in place. Valid only while the output buffer is.</summary>
    internal bool TryGetBytes(Guid fmtid, uint pid, out ReadOnlySpan<byte> value)
    {
        value = default;
        if (!TryFind(fmtid, pid, out var raw)) return false;

        int offset = 0;
        if (ReadUInt32(raw, ref offset) is not (WpdVarType.Vector | WpdVarType.UI1)) return false;
        int length = (int)ReadUInt32(raw, ref offset);
        value = raw.Slice(offset, length);
        return true;
    }

    /// <summary>
    /// Reads a collection of VT_UI4 — how PTP response parameters come back. Empty when the key is
    /// absent, which is the common case: most operations answer with a code and nothing else.
    /// </summary>
    internal uint[] GetUInt32Collection(Guid fmtid, uint pid)
    {
        if (!TryFind(fmtid, pid, out var raw)) return [];

        int offset = 0;
        if (ReadUInt32(raw, ref offset) is not WpdVarType.Unknown) return [];
        if (ReadGuid(raw, ref offset) != WpdInterop.CLSID_PortableDevicePropVariantCollection) return [];

        uint count = ReadUInt32(raw, ref offset);
        if (count == 0) return [];

        var values = new uint[count];
        for (uint i = 0; i < count; i++)
        {
            if (ReadUInt32(raw, ref offset) is not WpdVarType.UI4) return [];
            values[i] = ReadUInt32(raw, ref offset);
        }
        return values;
    }

    private static uint ReadUInt32(ReadOnlySpan<byte> buffer, ref int offset)
    {
        uint value = BinaryPrimitives.ReadUInt32LittleEndian(buffer[offset..]);
        offset += 4;
        return value;
    }

    private static Guid ReadGuid(ReadOnlySpan<byte> buffer, ref int offset)
    {
        var value = new Guid(buffer.Slice(offset, 16));
        offset += 16;
        return value;
    }

    /// <summary>
    /// Steps over one value, whatever its shape, and answers where the next one starts. Every
    /// variant is self-describing, so an unknown key can be skipped without understanding it — which
    /// is what lets <see cref="TryFind"/> seek a single key through a bag it has never seen before.
    /// </summary>
    private static int SkipValue(ReadOnlySpan<byte> buffer, int offset)
    {
        uint varType = ReadUInt32(buffer, ref offset);
        switch (varType)
        {
            case WpdVarType.Unknown:
                // A collection's items are bare variants; a values bag's are key/value pairs.
                bool keyed = ReadGuid(buffer, ref offset) != WpdInterop.CLSID_PortableDevicePropVariantCollection;
                uint count = ReadUInt32(buffer, ref offset);
                for (uint i = 0; i < count; i++)
                {
                    if (keyed) offset += 20;
                    offset = SkipValue(buffer, offset);
                }
                return offset;

            case WpdVarType.UI4:
            case WpdVarType.Error:
                return offset + 4;

            case WpdVarType.UI8:
                return offset + 8;

            case WpdVarType.CLSID:
                return offset + 16;

            case WpdVarType.LPWSTR:
            case WpdVarType.Vector | WpdVarType.UI1:
                return offset + 4 + (int)BinaryPrimitives.ReadUInt32LittleEndian(buffer[offset..]);

            case WpdVarType.Vector | WpdVarType.UI4:
                return offset + 4 + (4 * (int)BinaryPrimitives.ReadUInt32LittleEndian(buffer[offset..]));

            default:
                throw new InvalidDataException(
                    $"Unrecognised WPD variant type 0x{varType:X} at offset {offset - 4}.");
        }
    }
}

/// <summary>
/// Builds the six WPD MTP extension commands as ioctl input buffers. One place so the request
/// grammar stays in step with <c>WpdPtpTransport</c>'s COM equivalents — every property set here has
/// a <c>SetXxxValue</c> counterpart there, on the same PROPERTYKEY.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class WpdCommands
{
    /// <summary>
    /// WPD_COMMAND_COMMON_SAVE_CLIENT_INFORMATION. Sent once per handle, before anything else; the
    /// driver refuses MTP extension commands from a client it has not been introduced to.
    /// </summary>
    internal static WpdBagWriter SaveClientInformation(byte[] buffer, string clientName)
    {
        var writer = new WpdBagWriter(buffer);
        writer.BeginValues(3);

        writer.Key(WpdInterop.WPD_COMMAND_COMMON, WpdInterop.PID_COMMAND_CATEGORY);
        writer.GuidValue(WpdInterop.WPD_COMMAND_COMMON);

        writer.Key(WpdInterop.WPD_COMMAND_COMMON, WpdInterop.PID_COMMAND_ID);
        writer.UInt32Value(WpdInterop.PID_COMMAND_SAVE_CLIENT_INFORMATION);

        writer.Key(WpdInterop.WPD_COMMAND_COMMON, WpdInterop.PID_CLIENT_INFORMATION);
        writer.BeginValues(4);
        writer.Key(WpdInterop.WPD_CLIENT_INFO, WpdInterop.PID_CLIENT_NAME);
        writer.StringValue(clientName);
        writer.Key(WpdInterop.WPD_CLIENT_INFO, WpdInterop.PID_CLIENT_MAJOR_VERSION);
        writer.UInt32Value(1);
        writer.Key(WpdInterop.WPD_CLIENT_INFO, WpdInterop.PID_CLIENT_MINOR_VERSION);
        writer.UInt32Value(0);
        writer.Key(WpdInterop.WPD_CLIENT_INFO, WpdInterop.PID_CLIENT_REVISION);
        writer.UInt32Value(0);

        return writer;
    }

    /// <summary>
    /// Phase 1 of any operation: <paramref name="commandId"/> selects no-data (12), data-to-read (13)
    /// or data-to-write (14).
    /// </summary>
    /// <remarks>
    /// The operation-parameters collection is written even when empty, for the same reason
    /// <c>WpdPtpTransport.SetOperationParams</c> always attaches one: without it the driver fails
    /// vendor data-reads with ELEMENT_NOT_FOUND. Same driver, one layer down.
    /// </remarks>
    internal static WpdBagWriter Execute(
        byte[] buffer, string context, uint commandId, ushort opCode, uint[] parameters, long? writeSize = null)
    {
        var writer = new WpdBagWriter(buffer);
        writer.BeginValues(writeSize is null ? 5 : 6);

        WriteHeader(ref writer, commandId);

        writer.Key(WpdInterop.WPD_COMMAND_MTP_EXT, WpdInterop.PID_OPERATION_CODE);
        writer.UInt32Value(opCode);

        writer.Key(WpdInterop.WPD_COMMAND_MTP_EXT, WpdInterop.PID_OPERATION_PARAMS);
        writer.BeginCollection(parameters.Length);
        foreach (uint parameter in parameters) writer.UInt32Value(parameter);

        if (writeSize is { } size)
        {
            writer.Key(WpdInterop.WPD_COMMAND_MTP_EXT, WpdInterop.PID_TRANSFER_TOTAL_SIZE);
            writer.UInt64Value((ulong)size);
        }

        WriteContext(ref writer, context);
        return writer;
    }

    /// <summary>
    /// Phase 2 of a data-read. TRANSFER_DATA is documented as an output, but it is really in/out —
    /// the caller supplies the landing buffer and the driver fills it, exactly as in the COM path.
    /// </summary>
    internal static WpdBagWriter ReadData(byte[] buffer, string context, string transferContext, int wantBytes)
    {
        var writer = new WpdBagWriter(buffer);
        writer.BeginValues(6);

        WriteHeader(ref writer, WpdInterop.PID_READ_DATA);

        writer.Key(WpdInterop.WPD_COMMAND_MTP_EXT, WpdInterop.PID_TRANSFER_CONTEXT);
        writer.StringValue(transferContext);

        writer.Key(WpdInterop.WPD_COMMAND_MTP_EXT, WpdInterop.PID_TRANSFER_NUM_BYTES_TO_READ);
        writer.UInt32Value((uint)wantBytes);

        writer.Key(WpdInterop.WPD_COMMAND_MTP_EXT, WpdInterop.PID_TRANSFER_DATA);
        writer.ReserveBytesValue(wantBytes);

        WriteContext(ref writer, context);
        return writer;
    }

    /// <summary>Phase 2 of a data-write.</summary>
    internal static WpdBagWriter WriteData(byte[] buffer, string context, string transferContext, ReadOnlySpan<byte> data)
    {
        var writer = new WpdBagWriter(buffer);
        writer.BeginValues(6);

        WriteHeader(ref writer, WpdInterop.PID_WRITE_DATA);

        writer.Key(WpdInterop.WPD_COMMAND_MTP_EXT, WpdInterop.PID_TRANSFER_CONTEXT);
        writer.StringValue(transferContext);

        writer.Key(WpdInterop.WPD_COMMAND_MTP_EXT, WpdInterop.PID_TRANSFER_NUM_BYTES_TO_WRITE);
        writer.UInt32Value((uint)data.Length);

        writer.Key(WpdInterop.WPD_COMMAND_MTP_EXT, WpdInterop.PID_TRANSFER_DATA);
        writer.BytesValue(data);

        WriteContext(ref writer, context);
        return writer;
    }

    /// <summary>Phase 3: closes the transfer and carries the PTP response back.</summary>
    internal static WpdBagWriter EndDataTransfer(byte[] buffer, string context, string transferContext)
    {
        var writer = new WpdBagWriter(buffer);
        writer.BeginValues(4);

        WriteHeader(ref writer, WpdInterop.PID_END_DATA_TRANSFER);

        writer.Key(WpdInterop.WPD_COMMAND_MTP_EXT, WpdInterop.PID_TRANSFER_CONTEXT);
        writer.StringValue(transferContext);

        WriteContext(ref writer, context);
        return writer;
    }

    /// <summary>Which command, in which category — the first two entries of every command bag.</summary>
    private static void WriteHeader(ref WpdBagWriter writer, uint commandId)
    {
        writer.Key(WpdInterop.WPD_COMMAND_COMMON, WpdInterop.PID_COMMAND_ID);
        writer.UInt32Value(commandId);

        writer.Key(WpdInterop.WPD_COMMAND_COMMON, WpdInterop.PID_COMMAND_CATEGORY);
        writer.GuidValue(WpdInterop.WPD_COMMAND_MTP_EXT);
    }

    /// <summary>
    /// Identifies the client, and closes every command bag.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The value is what the handshake handed back. Captures show EDSDK sending a different GUID
    /// string on every command and the driver accepting values it was never given, so it does not
    /// appear to be validated — but echoing the real one is what the COM layer does, and costs
    /// nothing.
    /// </para>
    /// <para>
    /// Last, not first, because that is where the captured bags put it. A property bag has no
    /// meaningful order and the driver plainly does not care, but matching the reference byte for
    /// byte is what lets <c>WpdPropertyBagTests</c> pin this encoder against traffic that is known
    /// to work.
    /// </para>
    /// </remarks>
    private static void WriteContext(ref WpdBagWriter writer, string context)
    {
        writer.Key(WpdInterop.WPD_COMMAND_COMMON, WpdInterop.PID_CLIENT_INFORMATION_CONTEXT);
        writer.StringValue(context);
    }
}
