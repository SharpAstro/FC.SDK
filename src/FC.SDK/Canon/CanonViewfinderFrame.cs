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
/// others carry the zoom, magnifier, AF and histogram metadata that <see cref="Decode"/> and
/// <see cref="TryGetHistogram"/> read, and a few still unidentified. Feeding the envelope
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
    /// Four uint32s in sensor pixels: the origin of the image sent, then the size of the area shown.
    /// At 10× that is the 5× image's origin with the 10× size.
    /// </summary>
    private const uint ZoomRectRecord = 18;

    /// <summary>Two uint32s — the full sensor dimensions.</summary>
    private const uint SensorSizeRecord = 14;

    /// <summary>Four 256-bin uint32 histograms, 4096 bytes in total.</summary>
    private const uint HistogramRecord = 17;

    /// <summary>One uint32: the magnification, 1, 5 or 10.</summary>
    private const uint MagnificationRecord = 4;

    /// <summary>One uint32: 2 while the body drives autofocus, 1 otherwise.</summary>
    private const uint AfBusyRecord = 7;

    /// <summary>The AF frames: a count, then (for one frame) state, 1, x, y, w, h.</summary>
    private const uint AfFramesRecord = 8;

    /// <summary>Four uint32s: the magnifier rect, x, y, width, height.</summary>
    private const uint MagnifierRecord = 13;

    /// <summary>Record 8's length when it carries exactly one frame: seven uint32s.</summary>
    private const int OneAfFrameLength = 7 * sizeof(uint);

    /// <summary>
    /// The JPEG and what the body says about it, from one walk over the envelope.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The JPEG is a slice of <paramref name="payload"/>, not a copy, so it lives as long as the
    /// caller keeps the envelope. Empty when the envelope carries no image record, which happens for
    /// the metadata-only frames a body emits while it settles. A bare JPEG comes back whole, with no
    /// information.
    /// </para>
    /// <para>
    /// Records 4, 7, 8 and 13 were identified on an EOS R6 and an EOS 6D (issue #21) by driving the
    /// bodies through known zoom, pan and focus states while the bytes were watched. A record that is
    /// missing, or shorter than the layout read here, leaves its field null rather than guessed.
    /// </para>
    /// </remarks>
    internal static ReadOnlyMemory<byte> Decode(byte[] payload, out CanonEvfFrameInfo info)
    {
        info = default;
        if (payload.Length >= 2 && payload[0] == 0xFF && payload[1] == 0xD8)
            return payload;

        var jpeg = ReadOnlyMemory<byte>.Empty;
        uint? magnification = null;
        CanonEvfRect? magnifier = null;
        CanonEvfPoint? imageOrigin = null;
        CanonEvfSize? shownSize = null;
        CanonEvfSize? sensor = null;
        bool? afBusy = null;
        CanonEvfAfFrame? afFrame = null;
        var afFrames = ReadOnlyMemory<byte>.Empty;

        var offset = 0;
        while (offset + RecordHeaderSize <= payload.Length)
        {
            var length = BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(offset));
            var type = BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(offset + 4));
            if (length < RecordHeaderSize || length > (uint)(payload.Length - offset))
                break;

            var body = new ReadOnlyMemory<byte>(payload, offset + RecordHeaderSize, (int)length - RecordHeaderSize);
            var s = body.Span;
            switch (type)
            {
                case var _ when jpeg.IsEmpty && Array.IndexOf(JpegTypes, type) >= 0:
                    jpeg = body;
                    break;
                case MagnificationRecord when s.Length >= 4:
                    magnification = Read(s, 0);
                    break;
                case AfBusyRecord when s.Length >= 4:
                    // Only 1 and 2 have been seen; anything else is reported as unknown.
                    afBusy = Read(s, 0) switch { 1 => false, 2 => true, _ => null };
                    break;
                case AfFramesRecord when s.Length >= 4:
                    afFrames = body;
                    if (Read(s, 0) == 1 && s.Length == OneAfFrameLength)
                        afFrame = new CanonEvfAfFrame(Read(s, 4), Read(s, 12), Read(s, 16), Read(s, 20), Read(s, 24));
                    break;
                case MagnifierRecord when s.Length >= 16:
                    magnifier = new CanonEvfRect(Read(s, 0), Read(s, 4), Read(s, 8), Read(s, 12));
                    break;
                case ZoomRectRecord when s.Length >= 16:
                    imageOrigin = new CanonEvfPoint(Read(s, 0), Read(s, 4));
                    shownSize = new CanonEvfSize(Read(s, 8), Read(s, 12));
                    break;
                case SensorSizeRecord when s.Length >= 8:
                    sensor = new CanonEvfSize(Read(s, 0), Read(s, 4));
                    break;
            }

            offset += (int)length;
        }

        info = new CanonEvfFrameInfo(magnification, magnifier, imageOrigin, shownSize, sensor, afBusy, afFrame, afFrames);
        return jpeg;

        static uint Read(ReadOnlySpan<byte> s, int at) => BinaryPrimitives.ReadUInt32LittleEndian(s[at..]);
    }

    /// <summary>The zoom rect (records 18 and 14) as <see cref="TryGetZoomRect"/> reads it, or null without record 18.</summary>
    internal static CanonEvfZoomRect? ZoomRectOf(CanonEvfFrameInfo info) =>
        info is { ImageOrigin: { } o, ShownSize: { } s }
            ? new CanonEvfZoomRect(o.X, o.Y, s.Width, s.Height, info.Sensor?.Width ?? 0, info.Sensor?.Height ?? 0)
            : null;

    /// <summary>
    /// Whether a pan to (<paramref name="x"/>, <paramref name="y"/>) has shown up in a frame.
    /// </summary>
    /// <remarks>
    /// Judged on the magnifier rect (record 13) when both frames carry it, because that is the record
    /// a pan moves at every magnification. Record 18 does not move at 1× (it stays the whole sensor),
    /// so judging on it there waited out the whole settle timeout for a pan that had landed (3.1 s on
    /// an R6 and a 6D, issue #21). Without record 13 the zoom rect is the only account left. Settled
    /// means the exact position asked for, or any origin other than the one it started from, since
    /// the body clamps silently.
    /// </remarks>
    internal static bool PanSettled(CanonEvfFrameInfo before, CanonEvfFrameInfo now, uint x, uint y)
    {
        if (before.Magnifier is { } from && now.Magnifier is { } to)
            return (to.X == x && to.Y == y) || to.X != from.X || to.Y != from.Y;

        if (now.ImageOrigin is not { } origin) return false;
        return (origin.X == x && origin.Y == y)
               || before.ImageOrigin is not { } start
               || origin.X != start.X || origin.Y != start.Y;
    }

    private const int HistogramBins = 256;
    private const int HistogramChannels = 4;

    /// <summary>
    /// The live exposure histogram the body computes for its own display, or null when the envelope
    /// carries no histogram record.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The one piece of live metering an EOS volunteers over PTP. There is no "metered exposure
    /// value" operation, so on a body in Manual this is the only way to ask whether a frame is
    /// exposed before spending a shutter actuation on it.
    /// </para>
    /// <para>
    /// The 4096-byte payload is four 256-bin uint32 histograms. That grouping is not assumed: each
    /// group's bins sum to the same pixel count, which four unrelated arrays would not do, and the
    /// decode rejects a payload where they disagree.
    /// </para>
    /// <para>
    /// <b>Confirmed on an EOS 6D</b> by <c>FC.SDK.Diagnostics meter</c>, three ways. Structure: all
    /// four groups counted 345,600 pixels, so the histogram is computed on a 720x480 reduction rather
    /// than the streamed JPEG's own size. Channel order: only one assignment of the four groups
    /// satisfies <c>Y = 0.299R + 0.587G + 0.114B</c>, and the winner was Y,R,G,B with a residual of
    /// 0.03% against 1.49% for the runner-up. That was corroborated physically, the means reading
    /// R 71.8% / G 47.6% / B 18.0% under a warm LED. Liveness: an ISO sweep moved the mean 2.49% to
    /// 70.82% monotonically, and the first step gave 3.96x for a 4x light increase, so the histogram
    /// looks <b>linear in light rather than gamma encoded</b>. Later steps compress only because the
    /// 99th percentile approaches clipping.
    /// </para>
    /// </remarks>
    internal static CanonEvfHistogram? TryGetHistogram(List<CanonViewfinderRecord> records)
    {
        foreach (var r in records)
        {
            if (r.Type is not HistogramRecord) continue;

            var s = r.Payload.Span;
            if (s.Length < HistogramChannels * HistogramBins * sizeof(uint)) continue;

            var channels = new uint[HistogramChannels][];
            var totals = new long[HistogramChannels];
            for (var c = 0; c < HistogramChannels; c++)
            {
                var bins = new uint[HistogramBins];
                for (var b = 0; b < HistogramBins; b++)
                {
                    bins[b] = BinaryPrimitives.ReadUInt32LittleEndian(s[((c * HistogramBins + b) * sizeof(uint))..]);
                    totals[c] += bins[b];
                }
                channels[c] = bins;
            }

            // Four histograms of one image count the same pixels. If the groups disagree the layout
            // is not what this reads it as, and a wrong exposure readout is worse than none.
            if (totals[0] is 0 || totals.Any(t => t != totals[0])) return null;

            return new CanonEvfHistogram(channels[0], channels[1], channels[2], channels[3]);
        }

        return null;
    }

    /// <summary>
    /// The zoom rect (record 18, with the sensor size from record 14), or null when the envelope does
    /// not carry one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Both record types were identified on an EOS 6D by driving the body through known zoom states
    /// and watching which records tracked: type 14 held 5472×3648 (that body's exact sensor size)
    /// and type 18 went from <c>(0,0) 5472×3648</c> at 1× to a centred <c>(2184,1456) 1104×736</c>
    /// at 5×, a 4.96× crop. libgphoto2 does not decode either — it logs every non-JPEG record
    /// without interpreting it — so this is not corroborated by a second implementation, and a body
    /// that numbers its records differently will simply get null rather than a wrong answer.
    /// </para>
    /// <para>
    /// <b>This corrects what was written here before: record 18 is not the area shown at 10×.</b> It
    /// is the origin of the image sent, then the size of the area shown. At 5× the two coincide. At
    /// 10× the JPEG is still the 1104×736 5× image, so 18 reads <c>(2184,1456) 552×368</c>: the 5×
    /// origin with the 10× size. The 10× area itself is the magnifier rect, record 13
    /// (<c>(2460,1640) 552×368</c>). Measured on an EOS 6D and an EOS R6 over WPD raw ioctl
    /// (issue #21, 2026-11-13); see <see cref="CanonEvfFrameInfo"/>.
    /// </para>
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
    /// Exposed undecoded, for identifying records: that needs a body driven through known states
    /// while the bytes are watched, which is how 4, 7, 8, 13, 14, 17 and 18 were pinned (see
    /// <see cref="Decode"/> and <see cref="TryGetHistogram"/>). libgphoto2 is no help here: its
    /// <c>camera_capture_preview</c> dumps every non-JPEG record to a debug log without
    /// interpreting any of them.
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
/// The zoom rect the live-view envelope reports (record 18): where the image sent starts on the
/// sensor, and the size of the area shown, in sensor pixels.
/// </summary>
/// <remarks>
/// <para>
/// This is the body's own account of its magnification: a Canon answers the zoom operation with OK
/// whether or not it acts on it, so the response code cannot distinguish "zoomed" from "ignored".
/// </para>
/// <para>
/// <b>At 5× it is the area shown; at 10× it is not.</b> The 10× JPEG is still the 5× image, so this
/// holds the 5× origin with the 10× size, <c>(2184,1456) 552×368</c> centred on a 6D and an R6. For
/// where a pan landed, read the magnifier rect (record 13, <see cref="CanonEvfFrameInfo.Magnifier"/>):
/// it moves at every magnification, where this one stays the whole sensor at 1×. This corrects the
/// earlier description of this type as "the region the feed is currently showing" (issue #21).
/// </para>
/// </remarks>
/// <param name="X">Left edge of the image sent.</param>
/// <param name="Y">Top edge of the image sent.</param>
/// <param name="Width">Width of the area shown; equals <paramref name="SensorWidth"/> at 1×.</param>
/// <param name="Height">Height of the area shown.</param>
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

/// <summary>A point in sensor pixels.</summary>
public readonly record struct CanonEvfPoint(uint X, uint Y);

/// <summary>A size in sensor pixels.</summary>
public readonly record struct CanonEvfSize(uint Width, uint Height);

/// <summary>A rectangle in sensor pixels.</summary>
public readonly record struct CanonEvfRect(uint X, uint Y, uint Width, uint Height);

/// <summary>
/// One AF frame from record 8, read only when the record holds exactly one frame.
/// </summary>
/// <param name="State">
/// The state word, raw. 16 at rest; 17 from the moment focus is achieved until the focus line opens
/// (an EOS R6, 1-point AF, issue #21). A 6D at rest also reads 16.
/// </param>
/// <param name="X">Left edge of the AF frame.</param>
/// <param name="Y">Top edge.</param>
/// <param name="Width">Width; smaller than the magnifier, and per body (400 on an R6 at 1×, 552 on a 6D).</param>
/// <param name="Height">Height.</param>
public readonly record struct CanonEvfAfFrame(uint State, uint X, uint Y, uint Width, uint Height)
{
    /// <summary>
    /// Focus achieved: the low bit of <see cref="State"/>, the only bit seen to change (16 → 17).
    /// </summary>
    public bool InFocus => (State & 1) != 0;
}

/// <summary>
/// What a live-view frame's envelope says about the frame, decoded from the same envelope as its
/// JPEG. Each field is null when the body did not send its record.
/// </summary>
/// <remarks>
/// <para>
/// Fields are named for what the records say, and nothing is derived from them. In particular there
/// is no factor: at 10× record 18 holds the origin of the 5× image with the size of the 10× area, so
/// a ratio of its width to the sensor would describe neither.
/// </para>
/// <para>
/// Measured on an EOS R6 and an EOS 6D over WPD raw ioctl, sensor 5472×3648, magnifier centred:
/// </para>
/// <code>
///                 1×                  5×                    10×                   10×, panned to (1000,700)
/// Magnification   1                   5                     10                    10
/// Magnifier (13)  2184,1456 1104x736  2184,1456 1104x736    2460,1640 552x368     1000,700 552x368
/// Image (18)      0,0 5472x3648       2184,1456 1104x736    2184,1456 552x368     724,516 552x368
/// JPEG            960x640             1104x736              1104x736              1104x736
/// </code>
/// <para>
/// At 10× the JPEG is the 5× image (the same field, checked on the 6D's pixels), and the 10× area
/// sits at <c>Magnifier − ImageOrigin</c> inside it: (276,184) when centred.
/// </para>
/// </remarks>
/// <param name="Magnification">Record 4: 1, 5 or 10.</param>
/// <param name="Magnifier">
/// Record 13: the magnifier rect, which a pan moves at every magnification, 1× included. At 1× it has
/// the 5× size (1104×736 on both bodies), and in 1-point AF the AF frame stays centred in it.
/// </param>
/// <param name="ImageOrigin">Record 18, first half: where the image sent starts on the sensor.</param>
/// <param name="ShownSize">Record 18, second half: the size of the area shown.</param>
/// <param name="Sensor">Record 14: the sensor size.</param>
/// <param name="AfBusy">Record 7: true while the body drives autofocus. Null for a value other than the 1 and 2 seen.</param>
/// <param name="AfFrame">Record 8 decoded, when it holds exactly one frame in the layout measured.</param>
/// <param name="AfFrames">
/// Record 8 raw, empty when absent. With more than one frame (zone AF, faces) the per-frame layout is
/// unmeasured, so this is all there is. It is a slice of the envelope, so holding it keeps the whole
/// envelope alive; copy it to keep it past the frame. Being a <see cref="ReadOnlyMemory{T}"/>, it takes
/// part in this struct's equality by reference (same array, offset and length), not by content.
/// </param>
public readonly record struct CanonEvfFrameInfo(
    uint? Magnification,
    CanonEvfRect? Magnifier,
    CanonEvfPoint? ImageOrigin,
    CanonEvfSize? ShownSize,
    CanonEvfSize? Sensor,
    bool? AfBusy,
    CanonEvfAfFrame? AfFrame,
    ReadOnlyMemory<byte> AfFrames);

/// <summary>
/// The exposure histogram the camera computes for its own live-view display: four 256-bin channels
/// over the current frame.
/// </summary>
/// <remarks>
/// This is the closest thing an EOS offers to a light meter over PTP. It is the body's own metering
/// of the live image, so it reflects the aperture, shutter and ISO actually set — which on a body in
/// Manual is the only feedback there is short of taking the picture.
/// </remarks>
/// <param name="Luma">
/// Luminance, the channel to judge overall exposure by. Confirmed to be the first of the four groups
/// on an EOS 6D, and to satisfy the Rec.601 blend of the other three to within 0.03%.
/// </param>
/// <param name="Red">Red channel.</param>
/// <param name="Green">Green channel.</param>
/// <param name="Blue">Blue channel.</param>
public readonly record struct CanonEvfHistogram(uint[] Luma, uint[] Red, uint[] Green, uint[] Blue)
{
    /// <summary>
    /// Total pixels counted, identical across all four channels by construction. Zero for a
    /// <c>default</c> value, which a struct cannot prevent being created even though nothing in this
    /// assembly produces one: every member below tolerates it rather than throwing.
    /// </summary>
    public long PixelCount => Sum(Luma);

    /// <summary>
    /// Mean luma as a fraction of full scale, 0 to 1. A well-exposed average scene sits near 0.18;
    /// the frames that made an earlier focus measurement worthless read 0.01.
    /// </summary>
    /// <remarks>
    /// Measured to be linear in light on an EOS 6D, not gamma encoded: a 4x ISO increase moved this
    /// 3.96x. So two of these can be compared as an exposure ratio directly, and the stops between
    /// them are <c>log2</c> of the ratio, provided neither is near clipping.
    /// </remarks>
    public double MeanLevel => Mean(Luma);

    /// <summary>
    /// Fraction of pixels in the top bin, meaning blown highlights. Non-zero is not automatically
    /// wrong (specular highlights and stars are meant to clip), but a large value means detail is gone.
    /// </summary>
    public double ClippedHighlights => PixelCount is 0 ? 0 : (double)Luma[^1] / PixelCount;

    /// <summary>Fraction of pixels in the bottom bin, meaning crushed shadows.</summary>
    public double ClippedShadows => PixelCount is 0 ? 0 : (double)Luma[0] / PixelCount;

    /// <summary>The luma level at or below which <paramref name="fraction"/> of pixels fall, 0 to 1.</summary>
    public double Percentile(double fraction)
    {
        if (Luma is not { Length: > 1 }) return 0;

        var target = PixelCount * Math.Clamp(fraction, 0, 1);
        long running = 0;
        for (var b = 0; b < Luma.Length; b++)
        {
            running += Luma[b];
            if (running >= target) return b / (double)(Luma.Length - 1);
        }
        return 1.0;
    }

    private static long Sum(uint[]? bins)
    {
        if (bins is null) return 0;
        long total = 0;
        foreach (var v in bins) total += v;
        return total;
    }

    private static double Mean(uint[]? bins)
    {
        if (bins is not { Length: > 1 }) return 0;
        long total = 0, weighted = 0;
        for (var b = 0; b < bins.Length; b++) { total += bins[b]; weighted += (long)bins[b] * b; }
        return total is 0 ? 0 : weighted / (double)total / (bins.Length - 1);
    }

    public override string ToString() =>
        $"mean {MeanLevel:P1}, p50 {Percentile(0.5):P1}, p99 {Percentile(0.99):P1}, "
        + $"clipped {ClippedShadows:P1} low / {ClippedHighlights:P1} high";
}
