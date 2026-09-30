namespace FC.SDK.Canon;

/// <summary>
/// The shutter button over PTP (0x9128 presses, 0x9129 lets go), and the one rule a release and a bulb start both owe: a press
/// that was made is let go of, whatever answered after it.
/// </summary>
/// <remarks>
/// A body with a press held answers <see cref="EdsError.DeviceBusy"/> to every write. A bulb start the body refused (its mode
/// dial not on B answers <see cref="EdsError.NotSupported"/> to 0x9125) returned with the half press it had made still held,
/// and an EOS 6D stayed busy for longer than 30 s after it, refusing an ISO write six times over (2026-09-30, through TianWen). A
/// release whose full press was refused did the same.
/// </remarks>
internal static class ShutterPresses
{
    /// <summary>The half press (0x9128/0x9129 parameter 1).</summary>
    internal const uint Half = 0x01;

    /// <summary>The full press (0x9128/0x9129 parameter 2).</summary>
    internal const uint Full = 0x02;

    /// <summary>
    /// A release: half press, full press, let go of both. Returns the first answer that was not OK, or the last one; every
    /// press that was answered OK is let go of, and a refused half press holds nothing.
    /// </summary>
    internal static async Task<EdsError> ReleaseAsync(Func<uint, Task<EdsError>> press, Func<uint, Task<EdsError>> letGo)
    {
        var half = await press(Half);
        if (half is not EdsError.OK)
        {
            return half;
        }

        var result = EdsError.InternalError;
        try
        {
            result = await press(Full);
            if (result is EdsError.OK)
            {
                result = await letGo(Full);
            }
        }
        finally
        {
            var halfOff = await letGo(Half);
            if (result is EdsError.OK)
            {
                result = halfOff;
            }
        }

        return result;
    }

    /// <summary>
    /// A bulb start: the half press first where the body has the press pair (<paramref name="hasPressPair"/>), then
    /// <paramref name="bulbStart"/>. A start that is not answered OK lets go of the half press; one that is keeps it, for the
    /// bulb end to let go of.
    /// </summary>
    internal static async Task<EdsError> StartBulbAsync(
        bool hasPressPair, Func<uint, Task<EdsError>> press, Func<uint, Task<EdsError>> letGo, Func<Task<EdsError>> bulbStart)
    {
        if (hasPressPair)
        {
            var half = await press(Half);
            if (half is not EdsError.OK)
            {
                return half;
            }
        }

        var started = EdsError.InternalError;
        try
        {
            started = await bulbStart();
            return started;
        }
        finally
        {
            if (hasPressPair && started is not EdsError.OK)
            {
                await letGo(Half);
            }
        }
    }
}
