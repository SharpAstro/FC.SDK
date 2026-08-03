using System.Buffers.Binary;
using FC.SDK.Canon;
using Shouldly;
using Xunit;

namespace FC.SDK.Tests;

/// <summary>
/// Decoding the live-view frame envelope, and the zoom rect inside it.
/// </summary>
/// <remarks>
/// The record layout here is a transcript of what an EOS 6D actually sent, captured by
/// <c>FC.SDK.Diagnostics --evf</c>. It is worth pinning because the zoom rect is the only honest
/// account of whether magnification took effect: the zoom operation (0x9158) answers OK whether the
/// body acts on it or not, so a caller that trusts the response code cannot tell a 5× crop from a
/// full frame. libgphoto2 decodes none of these records, so there is no second implementation to
/// check against — only the camera.
/// </remarks>
public class ViewfinderEnvelopeTests
{
    private const uint SensorWidth = 5472;
    private const uint SensorHeight = 3648;

    /// <summary>
    /// The non-image records a 6D sends with every frame, in wire order. Payload bytes verbatim;
    /// the JPEG is stubbed to keep the fixture readable, since nothing here decodes it.
    /// </summary>
    private static byte[] Envelope((uint X, uint Y, uint W, uint H) zoomRect)
    {
        var records = new List<(uint Type, byte[] Payload)>
        {
            (0xFFFFFFFF, new byte[32]),
            (1, [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10]),          // JPEG (truncated for the fixture)
            (4, U32(1)),
            (5, U32(2184, 1456)),
            (18, U32(zoomRect.X, zoomRect.Y, zoomRect.W, zoomRect.H)),
            (12, U32(0)),
            (13, U32(2184, 1456, 1104, 736)),
            (14, U32(SensorWidth, SensorHeight)),
            (10, U32(0x6A70974B, 0x29C)),                        // a Unix timestamp, and a counter
            (7, U32(1)),
            (17, new byte[4096]),                                // histogram: 4 x 256 x uint32
            (0, []),
        };

        var total = records.Sum(r => 8 + r.Payload.Length);
        var buffer = new byte[total];
        var offset = 0;
        foreach (var (type, payload) in records)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(offset), (uint)(8 + payload.Length));
            BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(offset + 4), type);
            payload.CopyTo(buffer.AsSpan(offset + 8));
            offset += 8 + payload.Length;
        }
        return buffer;
    }

    private static byte[] U32(params uint[] values)
    {
        var bytes = new byte[values.Length * 4];
        for (int i = 0; i < values.Length; i++)
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(i * 4), values[i]);
        return bytes;
    }

    [Fact]
    public void Every_record_is_walked_including_the_zero_length_terminator()
    {
        var records = CanonViewfinderFrame.ParseRecords(Envelope((0, 0, SensorWidth, SensorHeight)));

        records.Count.ShouldBe(12);
        records.Count(r => r.IsImage).ShouldBe(1);
        records.Single(r => r.IsImage).Type.ShouldBe(1u);
        records[^1].Payload.Length.ShouldBe(0);
    }

    /// <summary>
    /// The image still has to come out of an envelope this full — the JPEG record is fifth from the
    /// start and the walk has to skip a 32-byte header record to reach it.
    /// </summary>
    [Fact]
    public void The_jpeg_is_extracted_from_a_realistic_envelope()
    {
        var jpeg = CanonViewfinderFrame.ExtractJpeg(Envelope((0, 0, SensorWidth, SensorHeight)));

        jpeg.Length.ShouldBe(6);
        jpeg[0].ShouldBe((byte)0xFF);
        jpeg[1].ShouldBe((byte)0xD8);
    }

    [Fact]
    public void At_one_times_the_rect_covers_the_whole_sensor_and_is_not_magnified()
    {
        var rect = CanonViewfinderFrame.TryGetZoomRect(
            CanonViewfinderFrame.ParseRecords(Envelope((0, 0, SensorWidth, SensorHeight))));

        rect.ShouldNotBeNull();
        rect!.Value.ShouldBe(new CanonEvfZoomRect(0, 0, SensorWidth, SensorHeight, SensorWidth, SensorHeight));
        rect.Value.IsMagnified.ShouldBeFalse();
        rect.Value.Factor.ShouldBe(1.0, 0.001);
    }

    /// <summary>
    /// The exact numbers a 6D reports at "5×" — a centred 1104×736 crop, which is 4.96× rather than
    /// 5×. The factor is not the value that was requested, which is why callers are pointed at the
    /// rect instead of at what they asked for.
    /// </summary>
    [Fact]
    public void At_five_times_the_rect_is_a_centred_crop_and_reports_the_real_factor()
    {
        var rect = CanonViewfinderFrame.TryGetZoomRect(
            CanonViewfinderFrame.ParseRecords(Envelope((2184, 1456, 1104, 736))));

        rect.ShouldNotBeNull();
        rect!.Value.IsMagnified.ShouldBeTrue();
        rect.Value.Factor.ShouldBe(4.956, 0.001);

        // Centred: the margins either side are equal.
        rect.Value.X.ShouldBe((SensorWidth - 1104) / 2);
        rect.Value.Y.ShouldBe((SensorHeight - 736) / 2);
    }

    /// <summary>
    /// A body that does not describe its zoom rect must produce null, not a fabricated one. The
    /// record numbering is confirmed on exactly one model, so this is the expected path elsewhere.
    /// </summary>
    [Fact]
    public void An_envelope_without_the_rect_record_yields_no_rect()
    {
        var envelope = Envelope((0, 0, SensorWidth, SensorHeight));
        var withoutRect = CanonViewfinderFrame.ParseRecords(envelope).Where(r => r.Type != 18).ToList();

        CanonViewfinderFrame.TryGetZoomRect(withoutRect).ShouldBeNull();
    }

    /// <summary>
    /// Without the sensor-size record there is nothing to compare a crop against, so magnification
    /// is unanswerable — and must be answered "no" rather than guessed from the crop alone.
    /// </summary>
    [Fact]
    public void Without_the_sensor_record_magnification_is_not_claimed()
    {
        var envelope = Envelope((2184, 1456, 1104, 736));
        var withoutSensor = CanonViewfinderFrame.ParseRecords(envelope).Where(r => r.Type != 14).ToList();

        var rect = CanonViewfinderFrame.TryGetZoomRect(withoutSensor);

        rect.ShouldNotBeNull();
        rect!.Value.Width.ShouldBe(1104u);
        rect.Value.SensorWidth.ShouldBe(0u);
        rect.Value.IsMagnified.ShouldBeFalse();
    }

    /// <summary>A truncated envelope stops the walk rather than reading past the buffer.</summary>
    [Fact]
    public void A_truncated_envelope_stops_cleanly()
    {
        var envelope = Envelope((0, 0, SensorWidth, SensorHeight));
        var truncated = envelope[..40];

        var records = CanonViewfinderFrame.ParseRecords(truncated);

        records.Count.ShouldBe(1);   // the 32-byte header record; the JPEG record claims more than remains
    }
}
