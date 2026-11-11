namespace FC.SDK.Canon;

/// <summary>
/// What to write to <c>Evf_OutputDevice</c> (0xD1B0) when live view starts and stops.
/// </summary>
/// <remarks>
/// <para>
/// The property is a bit mask, and the two obvious fixed values are both wrong on some body
/// (issue #21, measured on an EOS R6 and an EOS 6D over WPD raw ioctl):
/// </para>
/// <list type="bullet">
/// <item>
/// Starting with <see cref="EdsEvfOutputDevice.PC"/> alone takes the display away from the body. An
/// R6 then ignores its wired remote and its own dial and buttons. With its display kept in the mask
/// (<c>TFT | PC</c>) the remote focuses and releases, at the same frame rate.
/// </item>
/// <item>
/// Stopping with <see cref="EdsEvfOutputDevice.TFT"/> is right for a mirrorless body, which reads 1
/// at rest, and wrong for a DSLR, which reads 0: a 6D was left with live view running on its own
/// screen.
/// </item>
/// </list>
/// <para>
/// So a start adds the PC bit to what the body already shows, and a stop puts back what was there
/// before the start. The arithmetic lives here, apart from the camera, so it can be pinned without
/// a body on the desk.
/// </para>
/// </remarks>
internal static class CanonEvfOutput
{
    /// <summary>
    /// Set in the first of the two announcements a body makes for one change: a 6D reports
    /// <c>0x80000002</c>, then <c>2</c>. Not part of the value.
    /// </summary>
    internal const uint TransitionFlag = 0x8000_0000;

    /// <summary>The bits that send frames to the host.</summary>
    private const uint HostBits = (uint)(EdsEvfOutputDevice.PC | EdsEvfOutputDevice.PC_Small);

    /// <summary>A reading with the transition flag taken off.</summary>
    internal static uint Settled(uint reading) => reading & ~TransitionFlag;

    /// <summary>True when the reading already sends frames to the host.</summary>
    internal static bool SendsToHost(uint reading) => (Settled(reading) & HostBits) != 0;

    /// <summary>
    /// The value a start writes: the PC bit on top of whatever the body shows now. An unread
    /// property counts as nothing shown, which gives PC alone.
    /// </summary>
    internal static uint StartValue(uint? current) =>
        Settled(current ?? 0) | (uint)EdsEvfOutputDevice.PC;

    /// <summary>
    /// The value to keep for the stop, or <paramref name="remembered"/> unchanged when this reading
    /// is no use: an unread property, or one that already sends to the host (a second start, or a
    /// body an earlier session left in live view), which says nothing about its state at rest.
    /// </summary>
    internal static uint? Remember(uint? current, uint? remembered) =>
        current is { } c && !SendsToHost(c) ? Settled(c) : remembered;

    /// <summary>
    /// The value a stop writes, or null when there is nothing to write.
    /// </summary>
    /// <param name="remembered">What <see cref="Remember"/> kept at the start, if anything.</param>
    /// <param name="current">The property as it reads now, if it reads.</param>
    internal static uint? StopValue(uint? remembered, uint? current)
    {
        // The state at rest is known: put it back. 0 on a 6D, 1 on an R6.
        if (remembered is { } before) return before;

        // Nothing remembered and the body is not sending to the host: it is not in a live view this
        // library started, so leave its display alone. Writing TFT here would switch a DSLR's live
        // view ON.
        if (current is { } c && !SendsToHost(c)) return null;

        // Sending to the host with no record of what came before. TFT hands the display back on
        // every body measured; on a DSLR it leaves live view on the screen, which the user can see
        // and switch off, where PC alone leaves a body that ignores its buttons.
        return (uint)EdsEvfOutputDevice.TFT;
    }
}
