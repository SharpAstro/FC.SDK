using FC.SDK.Canon;

namespace FC.SDK.Scripting;

/// <summary>
/// The camera as a script sees it. An interface so a host that owns the camera's state (the
/// viewer, with its own live-view loop) can route a script through that state, and so the
/// interpreter can be tested with no body attached.
/// </summary>
public interface IScriptCamera
{
    /// <summary>
    /// A property's value on the camera now. Must bypass any cache: a cache mirrors the host's own
    /// writes, so a cached read after a write only shows the host agreeing with itself. Null when
    /// the body does not answer.
    /// </summary>
    Task<uint?> ReadPropertyAsync(ushort code, CancellationToken ct);

    /// <summary>Writes a scalar property.</summary>
    Task<EdsError> WritePropertyAsync(ushort code, uint value, CancellationToken ct);

    /// <summary>Starts live view, with the SDK's default output when <paramref name="output"/> is null.</summary>
    Task<EdsError> StartLiveViewAsync(EdsEvfOutputDevice? output, CancellationToken ct);

    /// <summary>Stops live view.</summary>
    Task<EdsError> StopLiveViewAsync(CancellationToken ct);

    /// <summary>
    /// Counts real frames over a window: a JPEG start-of-image and a plausible size. An idle body
    /// answers OK with a zero-length frame for ever, so counting answers proves nothing.
    /// </summary>
    Task<ScriptFrameCount> CountFramesAsync(TimeSpan window, CancellationToken ct);

    /// <summary>Releases the shutter over PTP.</summary>
    Task<EdsError> ReleaseAsync(CancellationToken ct);

    /// <summary>
    /// The images the body has announced since the last call: waits up to <paramref name="window"/>
    /// for the first, allows a moment for a second (a RAW+JPEG shot announces two), and hands every
    /// one back to the body so none is left holding it busy. Empty when nothing arrived.
    /// </summary>
    Task<IReadOnlyList<uint>> WaitForObjectsAsync(TimeSpan window, CancellationToken ct);
}

/// <summary>What <see cref="IScriptCamera.CountFramesAsync"/> saw.</summary>
/// <param name="Frames">Real frames in the window.</param>
/// <param name="LastFrameBytes">Size of the last one, 0 when there was none.</param>
public readonly record struct ScriptFrameCount(int Frames, int LastFrameBytes);

/// <summary>The operator's answer to a <c>confirm</c>.</summary>
public enum ScriptConfirmation
{
    /// <summary>They did what was asked.</summary>
    Done,

    /// <summary>They did not, and the script should carry on and say so.</summary>
    Skip,

    /// <summary>Stop the script here.</summary>
    Abort,
}

/// <summary>
/// The person running the script, and where its transcript goes.
/// </summary>
public interface IScriptHost
{
    /// <summary>Shows an instruction and waits until the operator answers it.</summary>
    Task<ScriptConfirmation> ConfirmAsync(string prompt, CancellationToken ct);

    /// <summary>Asks a yes/no question. Null stops the script.</summary>
    Task<bool?> AskAsync(string question, CancellationToken ct);

    /// <summary>One line of the transcript, as it happens.</summary>
    void Report(ScriptStep step);
}

/// <summary>How a step came out.</summary>
public enum ScriptOutcome
{
    /// <summary>Something the script did or read, with no verdict attached.</summary>
    Info,

    /// <summary>A check that held.</summary>
    Pass,

    /// <summary>A check that did not, or a command the camera refused.</summary>
    Fail,

    /// <summary>A step the operator skipped.</summary>
    Skipped,
}

/// <summary>One executed statement.</summary>
/// <param name="Line">1-based source line, 0 for something the runner did by itself (cleanup).</param>
/// <param name="Source">The statement's source text.</param>
/// <param name="Outcome">How it came out.</param>
/// <param name="Detail">What was read, answered or refused.</param>
public sealed record ScriptStep(int Line, string Source, ScriptOutcome Outcome, string Detail)
{
    /// <inheritdoc/>
    public override string ToString() =>
        $"{Outcome switch { ScriptOutcome.Pass => "PASS", ScriptOutcome.Fail => "FAIL", ScriptOutcome.Skipped => "SKIP", _ => "    " }} "
        + (Line > 0 ? $"{Line,3}: " : "     ")
        + Source + (Detail.Length > 0 ? $"   -> {Detail}" : "");
}

/// <summary>A finished run.</summary>
/// <param name="Steps">Every step, in order.</param>
/// <param name="Stopped">Why the run ended early, or null when it ran to the end.</param>
public sealed record ScriptRunResult(IReadOnlyList<ScriptStep> Steps, string? Stopped)
{
    /// <summary>Checks that held.</summary>
    public int Passed => Steps.Count(s => s.Outcome is ScriptOutcome.Pass);

    /// <summary>Checks that did not hold, and refused commands.</summary>
    public int Failed => Steps.Count(s => s.Outcome is ScriptOutcome.Fail);

    /// <summary>Steps the operator skipped.</summary>
    public int Skipped => Steps.Count(s => s.Outcome is ScriptOutcome.Skipped);

    /// <summary>Ran to the end with nothing failed. Skipped steps do not count against it.</summary>
    public bool Succeeded => Stopped is null && Failed is 0;

    /// <summary>One line: the counts, and why it stopped if it did.</summary>
    public string Summary =>
        $"{Passed} passed, {Failed} failed, {Skipped} skipped"
        + (Stopped is null ? "" : $". Stopped: {Stopped}");
}
