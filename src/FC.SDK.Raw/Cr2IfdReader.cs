using DIR.Lib.Tiff;
using System;
using System.Buffers.Binary;
using System.Collections.Generic;

namespace FC.SDK.Raw;

/// <summary>
/// Minimal TIFF IFD walker for CR2 parsing. Unlike <see cref="DIR.Lib.Tiff.TiffReader"/>
/// (which decodes pixel data and rejects unsupported compression), this walker just
/// returns the raw tag → value map per IFD so the CR2 decoder can locate strip offsets,
/// the Canon CR2Slice tag, the MakerNote IFD, etc. Pixel decode happens via the
/// SharpAstro.StbImage lossless-JPEG path, not through DIR.Lib's TiffReader.
///
/// Intentionally duplicates ~50 lines from DIR.Lib.Exif.ExifReader's private IFD walker
/// — the same comment there ("this is the boundary where EXIF parsing becomes a
/// standalone concern") applies here. Lifting it into a shared low-level IFD helper
/// in DIR.Lib would couple three concerns (TIFF pixel decode, EXIF, CR2 parsing) that
/// are intentionally separate.
/// </summary>
internal static class Cr2IfdReader
{
    /// <summary>One IFD entry's raw bytes — preserved in file byte order. Caller
    /// uses <see cref="ReadShort"/> / <see cref="ReadLong"/> with the file's
    /// endianness when interpreting numeric values.</summary>
    internal readonly record struct Entry(TiffFieldType Type, int Count, byte[] Bytes);

    /// <summary>Read TIFF header → (file-is-LE, IFD0 offset).</summary>
    internal static (bool FileIsLE, int Ifd0Offset) ReadHeader(ReadOnlySpan<byte> tiff)
    {
        if (tiff.Length < 8) throw new InvalidDataException("TIFF too short for header");
        bool fileIsLE;
        if (tiff[0] == 'I' && tiff[1] == 'I') fileIsLE = true;
        else if (tiff[0] == 'M' && tiff[1] == 'M') fileIsLE = false;
        else throw new InvalidDataException($"Bad TIFF byte-order mark: 0x{tiff[0]:X2}{tiff[1]:X2}");
        if (ReadShort(tiff.Slice(2, 2), fileIsLE) != 42)
            throw new InvalidDataException("TIFF magic number is not 42");
        var off = (int)ReadLong(tiff.Slice(4, 4), fileIsLE);
        return (fileIsLE, off);
    }

    /// <summary>Parse one IFD at <paramref name="ifdOffset"/>, returning its tag map
    /// and the offset of the next IFD in the chain (0 = end).</summary>
    internal static Dictionary<ushort, Entry> ParseIfd(ReadOnlySpan<byte> tiff, int ifdOffset, bool fileIsLE, out int nextIfdOffset)
    {
        var result = new Dictionary<ushort, Entry>();
        nextIfdOffset = 0;
        if (ifdOffset + 2 > tiff.Length) return result;

        var entryCount = ReadShort(tiff.Slice(ifdOffset, 2), fileIsLE);
        const int entrySize = 12;
        var dirEnd = ifdOffset + 2 + entryCount * entrySize;
        if (dirEnd + 4 > tiff.Length) return result;

        for (var i = 0; i < entryCount; i++)
        {
            var entryStart = ifdOffset + 2 + i * entrySize;
            var tag = ReadShort(tiff.Slice(entryStart, 2), fileIsLE);
            var type = (TiffFieldType)ReadShort(tiff.Slice(entryStart + 2, 2), fileIsLE);
            var count = (int)ReadLong(tiff.Slice(entryStart + 4, 4), fileIsLE);
            var typeSize = FieldTypeSize(type);
            if (typeSize == 0) continue; // unknown type — skip gracefully

            var totalBytes = typeSize * count;
            var valSlot = tiff.Slice(entryStart + 8, 4);
            ReadOnlySpan<byte> data;
            if (totalBytes <= 4)
            {
                data = valSlot[..totalBytes];
            }
            else
            {
                var off = (int)ReadLong(valSlot, fileIsLE);
                if (off < 0 || off + totalBytes > tiff.Length) continue; // OOB — skip
                data = tiff.Slice(off, totalBytes);
            }
            result[tag] = new Entry(type, count, data.ToArray());
        }
        nextIfdOffset = (int)ReadLong(tiff.Slice(dirEnd, 4), fileIsLE);
        return result;
    }

    /// <summary>Decode a SHORT or LONG-typed scalar entry into its numeric value.
    /// Returns 0 for non-numeric / multi-value entries — callers gate on
    /// <see cref="Entry.Count"/> when that matters.</summary>
    internal static uint ScalarValue(Entry entry, bool fileIsLE) => entry.Type switch
    {
        TiffFieldType.Byte => entry.Bytes[0],
        TiffFieldType.Short => ReadShort(entry.Bytes, fileIsLE),
        TiffFieldType.Long => ReadLong(entry.Bytes, fileIsLE),
        _ => 0u,
    };

    private static int FieldTypeSize(TiffFieldType type) => type switch
    {
        TiffFieldType.Byte or TiffFieldType.Ascii or TiffFieldType.SByte or TiffFieldType.Undefined => 1,
        TiffFieldType.Short or TiffFieldType.SShort => 2,
        TiffFieldType.Long or TiffFieldType.SLong or TiffFieldType.Float => 4,
        TiffFieldType.Rational or TiffFieldType.SRational or TiffFieldType.Double => 8,
        _ => 0,
    };

    internal static ushort ReadShort(ReadOnlySpan<byte> b, bool fileIsLE) => fileIsLE
        ? BinaryPrimitives.ReadUInt16LittleEndian(b)
        : BinaryPrimitives.ReadUInt16BigEndian(b);

    internal static uint ReadLong(ReadOnlySpan<byte> b, bool fileIsLE) => fileIsLE
        ? BinaryPrimitives.ReadUInt32LittleEndian(b)
        : BinaryPrimitives.ReadUInt32BigEndian(b);
}
