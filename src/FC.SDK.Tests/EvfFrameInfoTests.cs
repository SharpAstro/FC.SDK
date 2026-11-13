using System.Buffers.Binary;
using System.Runtime.InteropServices;
using FC.SDK.Canon;
using Shouldly;
using Xunit;

namespace FC.SDK.Tests;

/// <summary>
/// The JPEG and the frame information from one walk over a live-view envelope, and the pan check
/// built on it.
/// </summary>
/// <remarks>
/// <para>
/// The fixtures are envelopes an EOS 6D and an EOS R6 sent on 2026-11-13 over WPD raw ioctl, every
/// record in wire order with its payload verbatim, magnifier centred, at 1×, 5×, 10× and 10× panned
/// to (1000,700): the states of the R6 table in issue #21. Two payloads are stubbed: the JPEG (a
/// picture of a room, and nothing here decodes it) and the 4096-byte histogram, which
/// <see cref="EvfHistogramTests"/> covers.
/// </para>
/// <para>
/// The header record (type 0xFFFFFFFF) differs between frames of one state, so each fixture keeps
/// the one it was captured with.
/// </para>
/// </remarks>
public class EvfFrameInfoTests
{
    private const uint HeaderRecord = 0xFFFFFFFF;
    private static readonly byte[] JpegStub = [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10];

    private static byte[] Envelope(params (uint Type, string Hex)[] records)
    {
        var payloads = records.Select(r => (r.Type, Payload: r.Hex switch
        {
            "JPEG" => JpegStub,
            "HISTOGRAM" => new byte[4096],
            "ZEROS176" => new byte[176],
            _ => Convert.FromHexString(r.Hex),
        })).ToList();

        var buffer = new byte[payloads.Sum(p => 8 + p.Payload.Length)];
        var offset = 0;
        foreach (var (type, payload) in payloads)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(offset), (uint)(8 + payload.Length));
            BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(offset + 4), type);
            payload.CopyTo(buffer.AsSpan(offset + 8));
            offset += 8 + payload.Length;
        }
        return buffer;
    }

    // ------------------------------------------------------------------ EOS 6D

    private static byte[] SixD(string header, string mag, string origin5, string rect18, string rect13, string clock, string af) =>
        Envelope(
            (HeaderRecord, header),
            (1, "JPEG"),
            (4, mag),
            (5, origin5),
            (18, rect18),
            (12, "00000000"),
            (13, rect13),
            (14, "60150000400E0000"),
            (10, clock),
            (7, "01000000"),
            (17, "HISTOGRAM"),
            (8, af),
            (0, ""));

    internal static byte[] SixD1x() => SixD(
        "00000000F81AB340000000000000000094EC76005C8CA80090BE0A000000200030141B0090BE0A000000000000002000",
        "01000000", "88080000B0050000", "000000000000000060150000400E0000", "88080000B005000050040000E0020000",
        "304817657D030000", "0100000010000000010000009C090000B005000028020000E0020000");

    internal static byte[] SixD5x() => SixD(
        "00000000D4FCB840000000000000000094EC76005C8CA80090BE0A000000200030141B0090BE0A0000000000000020000000000000000000",
        "05000000", "88080000B0050000", "88080000B005000050040000E0020000", "88080000B005000050040000E0020000",
        "33481765CC010000", "010000001000000001000000FA090000680600006C01000070010000");

    internal static byte[] SixD10x() => SixD(
        "0000000074C4BE40000000000000000094EC76005C8CA80090BE0A0000002000",
        "0A000000", "9C09000068060000", "88080000B00500002802000070010000", "9C090000680600002802000070010000",
        "3648176501000000", "010000001000000001000000550A0000C4060000B6000000B8000000");

    internal static byte[] SixD10xPanned() => SixD(
        "00000000A49AC440000000000000000094EC76005C8CA80090BE0A000000200030141B00",
        "0A000000", "E8030000BC020000", "D4020000040200002802000070010000", "E8030000BC0200002802000070010000",
        "384817653E020000", "010000001000000001000000A104000018030000B6000000B8000000");

    // ------------------------------------------------------------------ EOS R6

    private const string R6Header =
        "0500000000000000A054AA018C5202000A000000504832000203000000002A000000000000000000000000000000000000100000";

    private static byte[] R6(string header, string mag, string origin5, string rect18, string rect13, string clock, string af) =>
        Envelope(
            (HeaderRecord, header),
            (1, "JPEG"),
            (16, "02000000030000000000000000000000"),
            (20, "000000000000000000000000"),
            (4, mag),
            (5, origin5),
            (18, rect18),
            (13, rect13),
            (14, "60150000400E0000"),
            (10, clock),
            (19, "000000000000000060150000400E0000"),
            (21, "00000000000000000000000000000000"),
            (22, "00000000"),
            (24, "0000000000000000000000000000000000000000"),
            (28, "ZEROS176"),
            (29, "FFFF0000FFFF0000"),
            (7, "01000000"),
            (17, "HISTOGRAM"),
            (8, af),
            (0, ""));

    internal static byte[] R61x() => R6(
        "0500000000000000A054AA018C5202000A000000504832000203000000002A0000000000",
        "01000000", "88080000B0050000", "000000000000000060150000400E0000", "88080000B005000050040000E0020000",
        "55A3F76AFA000000", "010000001000000001000000E80900004806000090010000B0010000");

    internal static byte[] R65x() => R6(R6Header,
        "05000000", "88080000B0050000", "88080000B005000050040000E0020000", "88080000B005000050040000E0020000",
        "57A3F76A36030000", "010000001000000001000000F8090000680600007001000070010000");

    internal static byte[] R610x() => R6(R6Header,
        "0A000000", "9C09000068060000", "88080000B00500002802000070010000", "9C090000680600002802000070010000",
        "5AA3F76A44010000", "010000001000000001000000540A0000C4060000B8000000B8000000");

    internal static byte[] R610xPanned() => R6(R6Header,
        "0A000000", "E8030000BC020000", "D4020000040200002802000070010000", "E8030000BC0200002802000070010000",
        "5CA3F76A5D030000", "010000001000000001000000A004000018030000B8000000B8000000");

    // ------------------------------------------------------------------ what each state decodes to

    public static TheoryData<string, byte[], uint, CanonEvfRect, CanonEvfPoint, CanonEvfSize, CanonEvfAfFrame> States => new()
    {
        { "6D 1x", SixD1x(), 1, new(2184, 1456, 1104, 736), new(0, 0), new(5472, 3648), new(16, 2460, 1456, 552, 736) },
        { "6D 5x", SixD5x(), 5, new(2184, 1456, 1104, 736), new(2184, 1456), new(1104, 736), new(16, 2554, 1640, 364, 368) },
        { "6D 10x", SixD10x(), 10, new(2460, 1640, 552, 368), new(2184, 1456), new(552, 368), new(16, 2645, 1732, 182, 184) },
        { "6D 10x panned", SixD10xPanned(), 10, new(1000, 700, 552, 368), new(724, 516), new(552, 368), new(16, 1185, 792, 182, 184) },
        { "R6 1x", R61x(), 1, new(2184, 1456, 1104, 736), new(0, 0), new(5472, 3648), new(16, 2536, 1608, 400, 432) },
        { "R6 5x", R65x(), 5, new(2184, 1456, 1104, 736), new(2184, 1456), new(1104, 736), new(16, 2552, 1640, 368, 368) },
        { "R6 10x", R610x(), 10, new(2460, 1640, 552, 368), new(2184, 1456), new(552, 368), new(16, 2644, 1732, 184, 184) },
        { "R6 10x panned", R610xPanned(), 10, new(1000, 700, 552, 368), new(724, 516), new(552, 368), new(16, 1184, 792, 184, 184) },
    };

    /// <summary>The R6 table of issue #21 and the 6D measured to match it, cell for cell.</summary>
    [Theory]
    [MemberData(nameof(States))]
    public void Each_state_decodes_to_what_the_body_reported(
        string state, byte[] envelope, uint magnification, CanonEvfRect magnifier,
        CanonEvfPoint imageOrigin, CanonEvfSize shownSize, CanonEvfAfFrame afFrame)
    {
        CanonViewfinderFrame.Decode(envelope, out var info);

        info.Magnification.ShouldBe(magnification, state);
        info.Magnifier.ShouldBe(magnifier, state);
        info.ImageOrigin.ShouldBe(imageOrigin, state);
        info.ShownSize.ShouldBe(shownSize, state);
        info.Sensor.ShouldBe(new CanonEvfSize(5472, 3648), state);
        info.AfBusy.ShouldBe(false, state);
        info.AfFrame.ShouldBe(afFrame, state);
        info.AfFrame!.Value.InFocus.ShouldBeFalse(state);
        info.AfFrames.Length.ShouldBe(28, state);
    }

    /// <summary>
    /// At 10× record 18 is the 5× origin with the 10× size, on both bodies, and the 10× area sits
    /// at (276,184) inside the 5× image: the reason the information carries no factor.
    /// </summary>
    [Fact]
    public void At_ten_times_the_image_is_the_five_times_one_on_both_bodies()
    {
        foreach (var envelope in new[] { SixD10x(), R610x() })
        {
            CanonViewfinderFrame.Decode(envelope, out var info);
            var (origin, magnifier) = (info.ImageOrigin!.Value, info.Magnifier!.Value);

            origin.ShouldBe(new CanonEvfPoint(2184, 1456));
            (magnifier.X - origin.X, magnifier.Y - origin.Y).ShouldBe((276u, 184u));
        }
    }

    /// <summary>The JPEG is handed back as a slice of the envelope it came in, not a copy.</summary>
    [Fact]
    public void The_jpeg_is_a_slice_of_the_envelope()
    {
        var envelope = R65x();

        var jpeg = CanonViewfinderFrame.Decode(envelope, out _);

        jpeg.ToArray().ShouldBe(JpegStub);
        MemoryMarshal.TryGetArray(jpeg, out var segment).ShouldBeTrue();
        segment.Array.ShouldBeSameAs(envelope);
    }

    /// <summary>The single pass and the older readers agree on every fixture.</summary>
    [Fact]
    public void The_zoom_rect_and_jpeg_agree_with_the_older_readers()
    {
        foreach (var envelope in new[] { SixD1x(), SixD5x(), SixD10x(), SixD10xPanned(), R61x(), R65x(), R610x(), R610xPanned() })
        {
            var jpeg = CanonViewfinderFrame.Decode(envelope, out var info);

            CanonViewfinderFrame.ZoomRectOf(info).ShouldBe(CanonViewfinderFrame.TryGetZoomRect(CanonViewfinderFrame.ParseRecords(envelope)));
            jpeg.ToArray().ShouldBe(CanonViewfinderFrame.ExtractJpeg(envelope));
        }
    }

    /// <summary>
    /// Record 8 while focus is held, as an R6 sent it in issue #21 (magnifier at (3884,970) at 1×):
    /// the state goes 16 → 17 and the rest of the frame is unchanged.
    /// </summary>
    [Fact]
    public void An_r6_frame_in_focus_reads_state_17()
    {
        var envelope = Envelope(
            (1, "JPEG"),
            (8, "0100000011000000010000008C1000006204000090010000B0010000"));

        CanonViewfinderFrame.Decode(envelope, out var info);

        info.AfFrame.ShouldBe(new CanonEvfAfFrame(17, 4236, 1122, 400, 432));
        info.AfFrame!.Value.InFocus.ShouldBeTrue();
    }

    /// <summary>
    /// More than one frame (zone AF, faces) has never been captured, so its layout is not guessed:
    /// the record comes back raw and the decoded frame is null.
    /// </summary>
    [Fact]
    public void Two_af_frames_come_back_raw()
    {
        var two = "02000000" + "10000000" + "01000000" + "9C090000B005000028020000E0020000"
                             + "10000000" + "01000000" + "8C1000006204000090010000B0010000";
        var envelope = Envelope((1, "JPEG"), (8, two));

        CanonViewfinderFrame.Decode(envelope, out var info);

        info.AfFrame.ShouldBeNull();
        info.AfFrames.ToArray().ShouldBe(Convert.FromHexString(two));
    }

    /// <summary>Record 7 is 2 while the body drives AF; a value never seen is not guessed at.</summary>
    [Theory]
    [InlineData("01000000", false)]
    [InlineData("02000000", true)]
    [InlineData("03000000", null)]
    public void Af_busy_reads_record_7(string hex, bool? busy)
    {
        CanonViewfinderFrame.Decode(Envelope((1, "JPEG"), (7, hex)), out var info);

        info.AfBusy.ShouldBe(busy);
    }

    /// <summary>A body that sends none of these records gets nulls, not zeros.</summary>
    [Fact]
    public void An_envelope_with_only_the_image_yields_no_information()
    {
        var jpeg = CanonViewfinderFrame.Decode(Envelope((1, "JPEG"), (0, "")), out var info);

        jpeg.ToArray().ShouldBe(JpegStub);
        info.ShouldBe(new CanonEvfFrameInfo(null, null, null, null, null, null, null, ReadOnlyMemory<byte>.Empty));
    }

    [Fact]
    public void A_bare_jpeg_comes_back_whole()
    {
        var bare = new byte[] { 0xFF, 0xD8, 0x01, 0x02 };

        var jpeg = CanonViewfinderFrame.Decode(bare, out var info);

        jpeg.ToArray().ShouldBe(bare);
        info.Magnifier.ShouldBeNull();
    }

    [Fact]
    public void A_truncated_envelope_stops_cleanly()
    {
        var envelope = SixD5x();

        var jpeg = CanonViewfinderFrame.Decode(envelope[..80], out var info);

        jpeg.IsEmpty.ShouldBeFalse();     // the 64-byte header record and the 14-byte JPEG record fit in 80
        info.Magnification.ShouldBeNull(); // record 4 does not
    }

    // ------------------------------------------------------------------ the pan check

    /// <summary>The R6 at 1× after a pan to (1000,1000): record 13 moved there, record 18 is still the whole sensor.</summary>
    internal static byte[] R61xPannedTo1000() => Envelope(
        (1, "JPEG"),
        (18, "000000000000000060150000400E0000"),
        (13, "E8030000E803000050040000E0020000"),     // 1000,1000 1104x736
        (14, "60150000400E0000"));

    private static CanonEvfFrameInfo Info(byte[] envelope)
    {
        CanonViewfinderFrame.Decode(envelope, out var info);
        return info;
    }

    /// <summary>
    /// A 1× pan as both bodies report it: record 13 moves to the requested origin and record 18
    /// stays the whole sensor. Judged on record 18 this never settled, and
    /// <c>SetEvfZoomPositionAsync</c> waited out its 3 s timeout (3021–3144 ms, twelve tries on an R6
    /// and a 6D over WPD raw ioctl).
    /// </summary>
    [Fact]
    public void A_pan_at_one_times_settles_on_the_magnifier()
    {
        var before = Info(R61x());
        var after = Info(R61xPannedTo1000());

        after.ImageOrigin.ShouldBe(before.ImageOrigin);   // 18 did not move
        CanonViewfinderFrame.PanSettled(before, after, 1000, 1000).ShouldBeTrue();
    }

    [Fact]
    public void A_pan_that_has_not_shown_up_yet_is_not_settled()
    {
        var before = Info(SixD1x());

        CanonViewfinderFrame.PanSettled(before, Info(SixD1x()), 1000, 1000).ShouldBeFalse();
    }

    /// <summary>A clamped pan lands somewhere other than asked, and somewhere other than it started.</summary>
    [Fact]
    public void A_clamped_pan_is_settled_once_the_magnifier_moves()
    {
        var before = Info(R610x());

        CanonViewfinderFrame.PanSettled(before, Info(R610xPanned()), 900, 650).ShouldBeTrue();
    }

    /// <summary>Without record 13 the zoom rect is the only account left, as before.</summary>
    [Fact]
    public void Without_the_magnifier_the_zoom_rect_decides()
    {
        static CanonEvfFrameInfo Only18(string rect) =>
            Info(Envelope((1, "JPEG"), (18, rect), (14, "60150000400E0000")));

        var before = Only18("88080000B005000050040000E0020000");          // 2184,1456 at 5x
        var moved = Only18("E8030000E803000050040000E0020000");           // 1000,1000

        CanonViewfinderFrame.PanSettled(before, before, 1000, 1000).ShouldBeFalse();
        CanonViewfinderFrame.PanSettled(before, moved, 1000, 1000).ShouldBeTrue();
    }
}
