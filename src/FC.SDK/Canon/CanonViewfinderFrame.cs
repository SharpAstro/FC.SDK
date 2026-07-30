using System.Buffers.Binary;

namespace FC.SDK.Canon;

/// <summary>
/// Unwraps the blob envelope Canon EOS bodies return from GetViewFinderData (0x9153).
/// </summary>
/// <remarks>
/// <para>
/// The payload is NOT a bare JPEG — it is a sequence of records, each
/// <c>[length:u32][type:u32][payload…]</c> where <c>length</c> covers the whole record including
/// those two words. The image lives in a record whose type is one of <see cref="JpegTypes"/>; the
/// others carry focus points, histogram and zoom metadata we do not use yet. Feeding the envelope
/// straight to a JPEG decoder fails with "no SOI", since the first bytes are the length word rather
/// than <c>FF D8</c>.
/// </para>
/// <para>
/// Record types match libgphoto2 <c>camera_capture_preview</c> (camlibs/ptp2/library.c): type 1 is
/// the regular JPEG preview, 11 likewise, and 9 is what movie mode returns. Verified on an EOS 450D,
/// whose frames arrive as ~268 KB envelopes.
/// </para>
/// </remarks>
internal static class CanonViewfinderFrame
{
    /// <summary>Record types whose payload is a JPEG image.</summary>
    private static readonly uint[] JpegTypes = [1, 9, 11];

    private const int RecordHeaderSize = 8;

    /// <summary>
    /// Returns the JPEG bytes from a viewfinder payload, or an empty array when the envelope carries
    /// no image record (which happens for the metadata-only frames a body emits while it settles).
    /// </summary>
    internal static byte[] ExtractJpeg(byte[] payload)
    {
        // A body that already hands back a bare JPEG needs no unwrapping — cheap to check, and it
        // keeps this from corrupting a future model that does.
        if (payload.Length >= 2 && payload[0] == 0xFF && payload[1] == 0xD8)
            return payload;

        var offset = 0;
        while (offset + RecordHeaderSize <= payload.Length)
        {
            var length = BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(offset));
            var type = BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(offset + 4));

            // A zero or absurd length would spin the loop forever; treat it as a truncated envelope.
            if (length < RecordHeaderSize || length > (uint)(payload.Length - offset))
                break;

            if (Array.IndexOf(JpegTypes, type) >= 0)
            {
                return payload[(offset + RecordHeaderSize)..(offset + (int)length)];
            }

            offset += (int)length;
        }

        return [];
    }
}
