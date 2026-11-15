namespace FC.SDK.Canon;

/// <summary>
/// How a body says whether a lens is fitted, and what it calls it.
/// </summary>
/// <remarks>
/// <para>
/// Measured on an EOS R6 and an EOS 6D (issue #28, 2026-11-14), three lenses and a bare mount each.
/// <c>LensStatus</c> (0xD1A8) reads 1 with a lens and 0 without, on both bodies, and both announce it
/// again when a lens comes off or goes back on, so the cache follows the mount while events are
/// drained. On removal a 6D sends one record of status 1 with lens id 0 and corrects it 225 ms later.
/// </para>
/// <para>
/// <b>A name can start with a 0 byte while a lens is fitted.</b> An R6 sends
/// <c>00 "35mm"</c> for a Sigma 35mm F1.4 DG HSM Art that a 6D names <c>"35mm"</c>, and writes the
/// same empty name into its own files. A 0 first byte is also how both bodies mark the name of a
/// lens that was taken off: a bare 6D holds <c>00 "4-70mm F2.8 ..."</c>, the previous lens's name
/// with its first byte zeroed, and a bare R6 holds a 0 and then 62 bytes that are not text
/// (the same over repeat reads, different from one day to the next). So the bytes after a leading
/// 0 are a name only when the lens status
/// says a lens is there, and that is the one case in which they are read.
/// </para>
/// </remarks>
internal static class CanonLens
{
    /// <summary>
    /// libgphoto2 <c>PTP_DPC_CANON_EOS_LensStatus</c>. Read raw: its pairing with an EDSDK id has not
    /// been extracted.
    /// </summary>
    internal const ushort LensStatusCode = 0xD1A8;

    /// <summary>True for a status of 1, false for 0, null for no reading or any other value.</summary>
    internal static bool? Present(uint? lensStatus) => lensStatus switch
    {
        1 => true,
        0 => false,
        _ => null,
    };

    /// <summary>
    /// The lens name in <paramref name="bytes"/> (0xD1D8, null-terminated ASCII), or null when there
    /// is none. One leading 0 is skipped when <paramref name="present"/> is true, and only then.
    /// </summary>
    internal static string? Name(ReadOnlySpan<byte> bytes, bool? present)
    {
        if (present is true && bytes.Length > 1 && bytes[0] == 0)
            bytes = bytes[1..];

        var end = bytes.IndexOf((byte)0);
        var name = System.Text.Encoding.ASCII.GetString(end < 0 ? bytes : bytes[..end]);
        return string.IsNullOrWhiteSpace(name) ? null : name;
    }
}
