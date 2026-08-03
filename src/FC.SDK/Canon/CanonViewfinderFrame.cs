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

    /// <summary>Four uint32s — x, y, width, height — describing the magnified region in sensor pixels.</summary>
    private const uint ZoomRectRecord = 18;

    /// <summary>Two uint32s — the full sensor dimensions.</summary>
    private const uint SensorSizeRecord = 14;

    /// <summary>
    /// The magnified region the body is currently showing, or null when the envelope does not
    /// describe one.
    /// </summary>
    /// <remarks>
    /// Both record types were identified on an EOS 6D by driving the body through known zoom states
    /// and watching which records tracked: type 14 held 5472×3648 (that body's exact sensor size)
    /// and type 18 went from <c>(0,0) 5472×3648</c> at 1× to a centred <c>(2184,1456) 1104×736</c>
    /// at 5×, a 4.96× crop. libgphoto2 does not decode either — it logs every non-JPEG record
    /// without interpreting it — so this is not corroborated by a second implementation, and a body
    /// that numbers its records differently will simply get null rather than a wrong answer.
    /// </remarks>
    internal static CanonEvfZoomRect? TryGetZoomRect(List<CanonViewfinderRecord> records)
    {
        (uint X, uint Y, uint W, uint H)? rect = null;
        (uint W, uint H)? sensor = null;

        foreach (var r in records)
        {
            var s = r.Payload.Span;
            if (r.Type is ZoomRectRecord && s.Length >= 16)
                rect = (Read(s, 0), Read(s, 4), Read(s, 8), Read(s, 12));
            else if (r.Type is SensorSizeRecord && s.Length >= 8)
                sensor = (Read(s, 0), Read(s, 4));
        }

        if (rect is not { } v) return null;

        // Without the sensor record there is nothing to compare the crop against, so magnification
        // would be unanswerable — report the rect with zeroed bounds rather than invent them.
        var (sw, sh) = sensor ?? (0u, 0u);
        return new CanonEvfZoomRect(v.X, v.Y, v.W, v.H, sw, sh);

        static uint Read(ReadOnlySpan<byte> s, int at) => BinaryPrimitives.ReadUInt32LittleEndian(s[at..]);
    }

    /// <summary>
    /// Every record in the envelope, image and metadata alike.
    /// </summary>
    /// <remarks>
    /// Only the image record is understood so far. The rest carry focus points, histogram and the
    /// zoom rect, and are exposed undecoded because identifying them needs a body that can be driven
    /// through known states while the bytes are watched — which is how the image record itself was
    /// pinned. libgphoto2 is no help here: its <c>camera_capture_preview</c> dumps every non-JPEG
    /// record to a debug log without interpreting any of them.
    /// </remarks>
    internal static List<CanonViewfinderRecord> ParseRecords(byte[] payload)
    {
        var records = new List<CanonViewfinderRecord>();

        // A body that hands back a bare JPEG has no envelope to walk.
        if (payload.Length >= 2 && payload[0] == 0xFF && payload[1] == 0xD8)
            return records;

        var offset = 0;
        while (offset + RecordHeaderSize <= payload.Length)
        {
            var length = BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(offset));
            var type = BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(offset + 4));

            if (length < RecordHeaderSize || length > (uint)(payload.Length - offset))
                break;

            records.Add(new CanonViewfinderRecord(
                type,
                Array.IndexOf(JpegTypes, type) >= 0,
                new ReadOnlyMemory<byte>(payload, offset + RecordHeaderSize, (int)length - RecordHeaderSize)));

            offset += (int)length;
        }

        return records;
    }

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

/// <summary>
/// One record from a live-view frame envelope.
/// </summary>
/// <param name="Type">
/// Canon's record-type word. 1 and 11 are the still preview, 9 is movie mode; the rest are
/// undecoded metadata.
/// </param>
/// <param name="IsImage">Whether <paramref name="Payload"/> is JPEG data.</param>
/// <param name="Payload">The record body, with the length and type words already stripped.</param>
public readonly record struct CanonViewfinderRecord(uint Type, bool IsImage, ReadOnlyMemory<byte> Payload);

/// <summary>
/// The region of the sensor the live-view feed is currently showing, in sensor pixels.
/// </summary>
/// <remarks>
/// This is the body's own account of its magnification, and the only trustworthy one: a Canon
/// answers the zoom operation with OK whether or not it acts on it, so the response code cannot
/// distinguish "zoomed" from "ignored". Also the read-out for panning — the camera silently clamps a
/// zoom position to keep the crop on the sensor and then reports where it actually landed.
/// </remarks>
/// <param name="X">Left edge of the magnified region.</param>
/// <param name="Y">Top edge.</param>
/// <param name="Width">Width of the magnified region; equals <paramref name="SensorWidth"/> at 1×.</param>
/// <param name="Height">Height of the magnified region.</param>
/// <param name="SensorWidth">Full sensor width, or 0 if the body did not report it.</param>
/// <param name="SensorHeight">Full sensor height, or 0 if the body did not report it.</param>
public readonly record struct CanonEvfZoomRect(
    uint X, uint Y, uint Width, uint Height, uint SensorWidth, uint SensorHeight)
{
    /// <summary>True when the feed is a crop rather than the whole frame.</summary>
    public bool IsMagnified => Width > 0 && SensorWidth > 0 && Width < SensorWidth;

    /// <summary>
    /// Actual magnification, sensor width over crop width — 4.96 for a "5×" on a 6D, because the
    /// crop is a whole number of pixels rather than an exact fifth.
    /// </summary>
    public double Factor => Width > 0 && SensorWidth > 0 ? (double)SensorWidth / Width : 1.0;

    public override string ToString() => $"({X},{Y}) {Width}x{Height} of {SensorWidth}x{SensorHeight} [{Factor:F2}x]";
}
