using FC.SDK.Canon;
using Shouldly;
using Xunit;

namespace FC.SDK.Tests;

/// <summary>
/// <see cref="CanonLens"/>: lens presence from <c>LensStatus</c> (0xD1A8), and the name in
/// <c>LensName</c> (0xD1D8), on the bytes an EOS R6 and an EOS 6D sent (issue #28, 2026-11-14).
/// </summary>
public class CanonLensTests
{
    private const string Sigma2470 = "24-70mm F2.8 DG OS HSM | Art 017";

    /// <summary>A name as a body sends it: the text, a 0, and zeros to <paramref name="length"/> bytes.</summary>
    private static byte[] Padded(int length, params byte[] start)
    {
        var bytes = new byte[length];
        start.CopyTo(bytes, 0);
        return bytes;
    }

    private static byte[] Ascii(string text) => System.Text.Encoding.ASCII.GetBytes(text);

    /// <summary>6D, Sigma 35mm F1.4 DG HSM Art: <c>33 35 6D 6D 00</c>, 64 bytes.</summary>
    private static byte[] Sigma35OnA6D() => Padded(64, 0x33, 0x35, 0x6D, 0x6D);

    /// <summary>R6, the same lens: <c>00 33 35 6D 6D 00</c>, 63 bytes.</summary>
    private static byte[] Sigma35OnAnR6() => Padded(63, 0x00, 0x33, 0x35, 0x6D, 0x6D);

    /// <summary>6D, bare mount after the 24-70: its name with the first byte zeroed, 64 bytes.</summary>
    private static byte[] Bare6DAfterThe2470()
    {
        var bytes = Padded(64, Ascii(Sigma2470));
        bytes[0] = 0;
        return bytes;
    }

    /// <summary>
    /// R6, bare mount: a 0 and then 62 bytes that are neither zero nor text (issue #28). Synthetic:
    /// the real ones stay with the reporter, since they may identify a body or a lens.
    /// </summary>
    private static byte[] BareR6()
    {
        var bytes = new byte[63];
        for (int i = 1; i < bytes.Length; i++) bytes[i] = (byte)(0x80 + i);
        return bytes;
    }

    /// <summary>
    /// What the gate on the lens status is for: behind a bare R6's leading 0 there is no name at all,
    /// and without the gate those bytes would be read as one. The 6D fixture cannot show it, since
    /// what follows its 0 is readable text.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(null)]
    public void A_bare_R6_has_no_name_though_bytes_follow_the_zero(bool? present) =>
        CanonLens.Name(BareR6(), present).ShouldBeNull();

    [Theory]
    [InlineData(1u, true)]
    [InlineData(0u, false)]
    public void A_status_of_one_is_a_lens_and_zero_is_none(uint status, bool expected) =>
        CanonLens.Present(status).ShouldBe(expected);

    /// <summary>Only 0 and 1 were seen, so nothing else is read as either.</summary>
    [Theory]
    [InlineData(2u)]
    [InlineData(0x80000001u)]
    [InlineData(null)]
    public void Any_other_status_or_none_is_unknown(uint? status) =>
        CanonLens.Present(status).ShouldBeNull();

    [Fact]
    public void A_6D_names_the_sigma_35mm() =>
        CanonLens.Name(Sigma35OnA6D(), present: true).ShouldBe("35mm");

    /// <summary>The case the leading 0 is skipped for: the R6 says a lens is fitted.</summary>
    [Fact]
    public void An_R6_names_it_too_once_the_status_says_a_lens_is_fitted() =>
        CanonLens.Name(Sigma35OnAnR6(), present: true).ShouldBe("35mm");

    /// <summary>
    /// The case it must not be skipped for: the bytes after the 0 are the lens that came off.
    /// </summary>
    [Fact]
    public void A_bare_6D_has_no_name_though_the_last_lens_follows_the_zero() =>
        CanonLens.Name(Bare6DAfterThe2470(), present: false).ShouldBeNull();

    /// <summary>A body that reports no lens status keeps today's reading: a leading 0 is an empty name.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData(false)]
    public void Without_a_lens_status_a_leading_zero_is_an_empty_name(bool? present)
    {
        CanonLens.Name(Sigma35OnAnR6(), present).ShouldBeNull();
        CanonLens.Name(Bare6DAfterThe2470(), present).ShouldBeNull();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    [InlineData(null)]
    public void A_name_that_starts_with_text_reads_whole_whatever_the_status(bool? present) =>
        CanonLens.Name(Padded(64, Ascii(Sigma2470)), present).ShouldBe(Sigma2470);

    /// <summary>An R6 follows the terminator with bytes that are not zero; they are not the name.</summary>
    [Fact]
    public void Bytes_after_the_terminator_are_not_read()
    {
        var bytes = Padded(63, Ascii(Sigma2470));
        bytes[Sigma2470.Length + 1] = 0x7F;
        bytes[Sigma2470.Length + 2] = 0x41;

        CanonLens.Name(bytes, present: true).ShouldBe(Sigma2470);
    }

    /// <summary>Only one 0 is skipped: a fitted lens with nothing after it is a lens with no name.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(63)]
    public void All_zeros_or_nothing_is_no_name(int length) =>
        CanonLens.Name(new byte[length], present: true).ShouldBeNull();

    [Fact]
    public void An_unterminated_name_reads_to_the_end() =>
        CanonLens.Name(Ascii("35mm"), present: true).ShouldBe("35mm");
}
