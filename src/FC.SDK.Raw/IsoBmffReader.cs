using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;

namespace FC.SDK.Raw;

/// <summary>
/// Minimal ISO BMFF (ISO/IEC 14496-12) box-tree walker for CR3 / HEIF /
/// MP4 family containers. Pure-managed C#, zero alloc per box read (returns
/// a lightweight record struct). The CR3 decoder builds on this; nothing in
/// FC.SDK.Raw outside the CR3 path consumes it today.
///
/// <para>The parser is intentionally **lazy** — <see cref="ParseTopLevel"/>
/// returns the list of immediate children, and the caller re-invokes
/// <see cref="ParseChildren"/> on each container box's payload as it
/// descends. This keeps per-decode allocations bounded to the number of
/// boxes we actually inspect, not the entire (potentially deep) tree. The
/// alternative — eager full-tree parse — was rejected for Phase A as
/// overkill for the ~10 boxes we touch per CR3.</para>
///
/// <para>Box-size conventions per ISO/IEC 14496-12 §4.2:</para>
/// <list type="bullet">
/// <item>4-byte big-endian size + 4-byte ASCII type (8-byte basic header)</item>
/// <item>Size = 1 → next 8 bytes are a 64-bit "largesize"</item>
/// <item>Size = 0 → box extends to end of file</item>
/// <item>Type = <c>uuid</c> → 16-byte UUID follows immediately after the
///   size/type header</item>
/// </list>
/// </summary>
internal static class IsoBmffReader
{
    /// <summary>A single ISO BMFF box's location + size in the source byte span.
    /// <see cref="PayloadOffset"/> points past the 8-byte (or 16-byte for
    /// largesize) header, AND past the 16-byte UUID for <c>uuid</c> boxes —
    /// callers don't need to step around either.</summary>
    public readonly record struct Box(
        string Type,
        int Offset,
        int Size,
        int PayloadOffset,
        int PayloadLength,
        Guid? UuidGuid);

    /// <summary>Parses the top-level box list from byte 0 to the end of
    /// <paramref name="bytes"/>. Returns an empty list (not throws) on
    /// truncated containers — the CR3 decoder treats missing top-level
    /// boxes as a format error one layer up with a clearer message.</summary>
    public static ImmutableArray<Box> ParseTopLevel(ReadOnlySpan<byte> bytes)
        => ParseRange(bytes, 0, bytes.Length);

    /// <summary>Parses the immediate child boxes of <paramref name="parent"/>'s
    /// payload, optionally skipping <paramref name="skipLeadingBytes"/> of
    /// preamble that some Canon-specific UUID boxes carry between the UUID
    /// and their first child box (the <c>eaf42b5e-..</c> preview-container UUID
    /// has 8 such bytes — looks like a version+flags counter, undocumented
    /// in the BMFF spec).</summary>
    public static ImmutableArray<Box> ParseChildren(
        ReadOnlySpan<byte> bytes, Box parent, int skipLeadingBytes = 0)
    {
        var start = parent.PayloadOffset + skipLeadingBytes;
        var end = parent.PayloadOffset + parent.PayloadLength;
        if (start > end || end > bytes.Length) return ImmutableArray<Box>.Empty;
        return ParseRange(bytes, start, end);
    }

    /// <summary>Find the first immediate child of <paramref name="parent"/>
    /// whose type matches <paramref name="type"/>. Convenience over
    /// <see cref="ParseChildren"/> + LINQ for the dominant "find one specific
    /// box" use case.</summary>
    public static Box? FindChild(ReadOnlySpan<byte> bytes, Box parent, string type, int skipLeadingBytes = 0)
    {
        foreach (var child in ParseChildren(bytes, parent, skipLeadingBytes))
        {
            if (child.Type == type) return child;
        }
        return null;
    }

    /// <summary>Number of bytes to skip when descending into a "FullBox"
    /// container (4-byte version+flags + 4-byte entry_count before the
    /// first child box). ISO/IEC 14496-12 §4.2: <c>stsd</c>, <c>dref</c>,
    /// <c>iref</c>, etc. — boxes that carry both a version stamp and an
    /// entry count before their child entries.</summary>
    public const int FullBoxEntryListHeader = 8;

    /// <summary>Number of bytes to skip when descending into Canon's
    /// <c>CRAW</c> sample-entry box before the first child codec-config box
    /// (<c>CMP1</c>, <c>CDI1</c>, etc.). Value is 0x52 = 82 — that's the
    /// standard VisualSampleEntry preamble (ISO/IEC 14496-12 §8.5.2.2: 6
    /// bytes reserved + 2 data_reference_index + 16 bytes pre_defined/reserved
    /// + 2 width + 2 height + 4 horizres + 4 vertres + 4 reserved + 2
    /// frame_count + 32 compressorname + 2 depth + 2 pre_defined = 78 bytes)
    /// PLUS 4 bytes of Canon-specific extension (matches Laurent Clévy's
    /// <c>parse_cr3.py</c> reference: <c>innerOffsets = { b'CRAW': 0x52, ... }</c>).</summary>
    public const int VisualSampleEntryHeader = 82;

    /// <summary>Descend into a FullBox container (<c>stsd</c> and friends),
    /// stepping past the version + entry-count header.</summary>
    public static ImmutableArray<Box> ParseFullBoxChildren(ReadOnlySpan<byte> bytes, Box parent)
        => ParseChildren(bytes, parent, FullBoxEntryListHeader);

    /// <summary>Descend into a VisualSampleEntry container (<c>CRAW</c> in
    /// CR3), stepping past the 78-byte sample-entry preamble.</summary>
    public static ImmutableArray<Box> ParseVisualSampleEntryChildren(ReadOnlySpan<byte> bytes, Box parent)
        => ParseChildren(bytes, parent, VisualSampleEntryHeader);

    /// <summary>Find the first top-level box of the given type. Returns null
    /// when the file is missing that box (which for required CR3 top-level
    /// boxes like <c>ftyp</c> / <c>moov</c> / <c>mdat</c> means the file is
    /// malformed — caller's responsibility to surface a useful error).</summary>
    public static Box? FindTopLevel(ReadOnlySpan<byte> bytes, string type)
    {
        foreach (var box in ParseTopLevel(bytes))
        {
            if (box.Type == type) return box;
        }
        return null;
    }

    private static ImmutableArray<Box> ParseRange(ReadOnlySpan<byte> bytes, int start, int end)
    {
        var builder = ImmutableArray.CreateBuilder<Box>();
        var pos = start;
        while (pos + 8 <= end)
        {
            var size = (int)BinaryPrimitives.ReadUInt32BigEndian(bytes.Slice(pos, 4));
            var type = ReadFourCc(bytes.Slice(pos + 4, 4));
            var headerSize = 8;
            if (size == 1)
            {
                // 64-bit largesize variant — used for >4 GB boxes. CR3 uses
                // this for the mdat box on big cameras (R5 at 45 MP, etc.).
                if (pos + 16 > end) break;
                var large = (long)BinaryPrimitives.ReadUInt64BigEndian(bytes.Slice(pos + 8, 8));
                if (large < 16 || large > int.MaxValue) break; // bail on absurd sizes
                size = (int)large;
                headerSize = 16;
            }
            else if (size == 0)
            {
                // Box extends to end of enclosing range.
                size = end - pos;
            }
            else if (size < 8)
            {
                // Malformed — would loop forever.
                break;
            }

            Guid? uuidGuid = null;
            var payloadOffset = pos + headerSize;
            if (type == "uuid" && payloadOffset + 16 <= pos + size)
            {
                uuidGuid = ReadUuid(bytes.Slice(payloadOffset, 16));
                payloadOffset += 16;
            }

            builder.Add(new Box(
                Type: type,
                Offset: pos,
                Size: size,
                PayloadOffset: payloadOffset,
                PayloadLength: (pos + size) - payloadOffset,
                UuidGuid: uuidGuid));

            pos += size;
        }
        return builder.ToImmutable();
    }

    private static string ReadFourCc(ReadOnlySpan<byte> bytes)
    {
        // 4CC codes are always ASCII per the spec. We allow non-printable
        // characters through (replace bad bytes) so a corrupt box doesn't
        // hard-fail the walk — caller's type lookup will simply miss it.
        Span<char> chars = stackalloc char[4];
        for (var i = 0; i < 4; i++) chars[i] = (char)bytes[i];
        return new string(chars);
    }

    /// <summary>Read a 16-byte UUID per the BMFF convention (RFC 4122 big-endian
    /// network order). .NET's <see cref="Guid"/> constructor that takes a span
    /// expects little-endian for the first three components, so we swap.</summary>
    private static Guid ReadUuid(ReadOnlySpan<byte> bytes)
    {
        var d1 = BinaryPrimitives.ReadUInt32BigEndian(bytes[..4]);
        var d2 = BinaryPrimitives.ReadUInt16BigEndian(bytes.Slice(4, 2));
        var d3 = BinaryPrimitives.ReadUInt16BigEndian(bytes.Slice(6, 2));
        return new Guid((int)d1, (short)d2, (short)d3,
            bytes[8], bytes[9], bytes[10], bytes[11],
            bytes[12], bytes[13], bytes[14], bytes[15]);
    }
}
