using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using FC.SDK.Canon;
using FC.SDK.Scripting;
using Microsoft.Extensions.Logging;

namespace FC.SDK.Viewer;

/// <summary>What the operator is being asked right now.</summary>
/// <param name="Text">The instruction or question.</param>
/// <param name="IsQuestion">A yes/no question rather than an instruction to carry out.</param>
/// <param name="Answer">Completed by the prompt bar: 0 done or yes, 1 skip or no, 2 abort.</param>
public sealed record ScriptPrompt(string Text, bool IsQuestion, TaskCompletionSource<int> Answer);

/// <summary>A script the viewer is running: its name, its tally, and the prompt on screen if any.</summary>
public sealed class ScriptSession(string name, CancellationTokenSource cancel)
{
    public string Name { get; } = name;

    /// <summary>Cancelled by Abort and by teardown.</summary>
    public CancellationTokenSource Cancel { get; } = cancel;

    public volatile ScriptPrompt? Prompt;
    public int Passed;
    public int Failed;
}

// Running a camera script from the viewer. A script is a sequence of ordinary camera operations
// with an operator in the loop, so it does not run as one queued action holding the gate: the
// live-view loop and the auto-download have to get the camera between its steps, exactly as they do
// between a person's clicks. Each step takes the gate for itself instead.
public sealed partial class ViewerActions
{
    /// <summary>Images announced while a script runs, for its <c>wait-object</c>. Null when none is running.</summary>
    private ConcurrentQueue<uint>? _scriptObjects;

    /// <summary>The state, for the nested script classes: a primary-constructor parameter is not a member.</summary>
    private ViewerState State => state;

    /// <summary>
    /// Finds the scripts on offer: the ones shipped beside the binary, and any in the output
    /// directory, where a reporter is told to drop one they were sent.
    /// </summary>
    public void FindScripts(string? explicitPath = null)
    {
        var found = new List<string>();
        if (explicitPath is not null && File.Exists(explicitPath)) found.Add(Path.GetFullPath(explicitPath));

        foreach (var directory in new[] { state.OutputDirectory, Path.Combine(AppContext.BaseDirectory, "scripts") })
        {
            if (!Directory.Exists(directory)) continue;
            foreach (var file in Directory.GetFiles(directory, "*.camscript").Order(StringComparer.OrdinalIgnoreCase))
            {
                if (!found.Contains(file, StringComparer.OrdinalIgnoreCase)) found.Add(file);
            }
        }

        state.ScriptFiles = found;
        state.Invalidate();
    }

    /// <summary>
    /// Parses and runs a script. A script that does not parse is refused here, before the camera is
    /// touched: the transcript is the deliverable, and a typo must not cost shutter actuations.
    /// </summary>
    public void RunScript(string path)
    {
        if (state.Script is not null) return;

        var name = Path.GetFileName(path);
        CameraScript script;
        try
        {
            script = CameraScript.Parse(File.ReadAllText(path));
        }
        catch (Exception ex) when (ex is ScriptSyntaxException or IOException or UnauthorizedAccessException)
        {
            _logger.LogError("Script {Name} not run: {Message}", name, ex.Message);
            state.StatusMessage = $"{name} not run: {ex.Message}";
            state.Invalidate();
            return;
        }

        if (state.Camera is not { } camera || !state.SessionOpen)
        {
            state.StatusMessage = $"{name} not run: open a session first.";
            state.Invalidate();
            return;
        }

        var cancel = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
        var session = new ScriptSession(name, cancel);
        state.Script = session;
        _scriptObjects = new ConcurrentQueue<uint>();
        state.Invalidate();

        _ = Task.Run(async () =>
        {
            _logger.LogInformation("Script {Name} started ({Path})", name, path);
            ScriptRunResult? result = null;
            try
            {
                var host = new ViewerScriptHost(this, session);
                result = await new ScriptRunner(new ViewerScriptCamera(this, camera), host).RunAsync(script, cancel.Token);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Script {Name} threw", name);
                state.StatusMessage = $"{name}: {ex.GetType().Name}: {ex.Message}";
            }
            finally
            {
                session.Prompt = null;
                _scriptObjects = null;
                state.Script = null;
                cancel.Dispose();
                state.Invalidate();
            }

            if (result is null) return;

            _logger.Log(result.Succeeded ? LogLevel.Information : LogLevel.Warning,
                "Script {Name} finished: {Summary}", name, result.Summary);
            state.StatusMessage = $"{name}: {result.Summary}";
            SaveTranscript(name, result);
            state.Invalidate();
        });
    }

    /// <summary>The operator's answer to the prompt on screen: 0 done or yes, 1 skip or no, 2 abort.</summary>
    public void AnswerScriptPrompt(int answer)
    {
        if (state.Script?.Prompt is not { } prompt) return;

        _logger.LogInformation("Operator answered \"{Prompt}\": {Answer}", prompt.Text, (answer, prompt.IsQuestion) switch
        {
            (0, true) => "yes",
            (1, true) => "no",
            (0, false) => "done",
            (1, false) => "skip",
            _ => "abort",
        });
        prompt.Answer.TrySetResult(answer);
    }

    /// <summary>Stops the running script. Live view it started is stopped by the runner.</summary>
    public void AbortScript()
    {
        if (state.Script is not { } session) return;

        _logger.LogInformation("Script {Name}: abort requested", session.Name);
        session.Prompt?.Answer.TrySetResult(2);
        try { session.Cancel.Cancel(); } catch (ObjectDisposedException) { /* it just finished */ }
    }

    /// <summary>
    /// The whole transcript as its own file, beside the log. The log has every step too, interleaved
    /// with the PTP traffic; this is the page to paste into an issue.
    /// </summary>
    private void SaveTranscript(string name, ScriptRunResult result)
    {
        try
        {
            Directory.CreateDirectory(state.OutputDirectory);
            var path = Path.Combine(state.OutputDirectory,
                $"script-{Path.GetFileNameWithoutExtension(name)}-{DateTime.Now:yyyyMMdd_HHmmss}.txt");
            var lines = new List<string>
            {
                $"{name} on {state.Model ?? "an unnamed camera"} ({state.ConnectedTo?.ToString() ?? "no transport"})",
                result.Summary,
                "",
            };
            lines.AddRange(result.Steps.Select(s => s.ToString()));
            File.WriteAllLines(path, lines);
            _logger.LogInformation("Script transcript saved to {Path}", path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning("Could not save the script transcript: {Message}", ex.Message);
        }
    }

    /// <summary>
    /// One script step under the same gate as every other camera operation, named on the status bar
    /// like one.
    /// </summary>
    private async Task<T> GatedAsync<T>(string name, Func<CancellationToken, Task<T>> body, CancellationToken ct)
    {
        Interlocked.Increment(ref state.QueuedOperations);
        state.Invalidate();
        try
        {
            await _gate.WaitAsync(ct);
        }
        finally
        {
            Interlocked.Decrement(ref state.QueuedOperations);
        }

        state.BusyOperation = name;
        state.Invalidate();
        try
        {
            return await body(ct);
        }
        finally
        {
            state.BusyOperation = null;
            state.Invalidate();
            try { _gate.Release(); } catch (ObjectDisposedException) { }
        }
    }

    /// <summary>
    /// The camera as a script sees it, routed through the viewer's own state: live view is the
    /// viewer's live view (so the frames a script counts are the frames on screen), and a capture
    /// is downloaded by the viewer's own auto-download.
    /// </summary>
    private sealed class ViewerScriptCamera(ViewerActions actions, CanonCamera camera) : IScriptCamera
    {
        private ViewerState State => actions.State;

        public Task<uint?> ReadPropertyAsync(ushort code, CancellationToken ct) =>
            actions.GatedAsync<uint?>($"Script: read 0x{code:X4}", async token =>
            {
                // Past the cache: it mirrors our own writes, so a cached read after a write is the
                // viewer agreeing with itself.
                await camera.DrainEventsAsync(token);
                var (err, bytes) = await camera.GetRawPropertyBytesAsync(code, refresh: true, token);
                return err is EdsError.OK && bytes.Length >= 4 ? BinaryPrimitives.ReadUInt32LittleEndian(bytes) : null;
            }, ct);

        public Task<EdsError> WritePropertyAsync(ushort code, uint value, CancellationToken ct) =>
            actions.GatedAsync($"Script: set 0x{code:X4}", token => camera.SetRawPropertyAsync(code, value, token), ct);

        public async Task<EdsError> StartLiveViewAsync(EdsEvfOutputDevice? output, CancellationToken ct)
        {
            var err = await actions.GatedAsync("Script: start live view",
                token => output is { } o ? camera.StartLiveViewAsync(o, token) : camera.StartLiveViewAsync(token), ct);
            if (err is not EdsError.OK) return err;

            // A second start while the loop is already running must not start a second loop.
            if (!State.LiveViewActive)
            {
                State.LiveViewActive = true;
                State.LiveViewFrameCount = 0;
                State.PreviewMode = PreviewPane.LiveView;
                actions._liveViewLoop = Task.Run(() => actions.LiveViewLoopAsync(camera, actions._shutdown.Token));
            }
            State.Invalidate();
            return err;
        }

        public async Task<EdsError> StopLiveViewAsync(CancellationToken ct)
        {
            // The loop first, and outside the gate: it takes the gate for every frame, so waiting for
            // it while holding the gate can wait for ever.
            await actions.StopLiveViewLoopAsync();
            return await actions.GatedAsync("Script: stop live view", camera.StopLiveViewAsync, ct);
        }

        public async Task<ScriptFrameCount> CountFramesAsync(TimeSpan window, CancellationToken ct)
        {
            // The viewer's loop only counts a frame it decoded, so these are real frames.
            var before = State.LiveViewFrameCount;
            await Task.Delay(window, ct);
            var frames = Math.Max(0, State.LiveViewFrameCount - before);
            return new ScriptFrameCount(frames, frames > 0 ? State.LiveViewFrameBytes : 0);
        }

        public Task<EdsError> ReleaseAsync(CancellationToken ct) =>
            actions.GatedAsync("Script: release", async token =>
            {
                actions.BeginExposure("Exposing", ExposureDeadline);
                var err = await camera.TakePictureAsync(token);
                if (err is not EdsError.OK) actions.EndExposure($"Script release failed: {err}");
                return err;
            }, ct);

        public async Task<IReadOnlyList<uint>> WaitForObjectsAsync(TimeSpan window, CancellationToken ct)
        {
            if (actions._scriptObjects is not { } objects) return [];

            var clock = Stopwatch.StartNew();
            while (objects.IsEmpty && clock.Elapsed < window)
                await Task.Delay(100, ct);
            if (objects.IsEmpty) return [];

            // A RAW+JPEG shot announces two objects, the second a moment after the first.
            await Task.Delay(1500, ct);

            var handles = new List<uint>();
            while (objects.TryDequeue(out var handle)) handles.Add(handle);

            if (actions.AutoDownload)
            {
                // The viewer's own download fetches and releases each frame. Let it finish, so the
                // script's next step meets a body that is not holding one.
                var settle = Stopwatch.StartNew();
                while ((State.IsBusy || Volatile.Read(ref State.QueuedOperations) > 0)
                       && settle.Elapsed < TimeSpan.FromSeconds(60))
                    await Task.Delay(100, ct);
            }
            else
            {
                // Nobody else will hand them back, and a few held frames make a body refuse to release.
                foreach (var handle in handles)
                    await actions.GatedAsync("Script: release frame", token => camera.TransferCompleteAsync(handle, token), ct);
            }

            return handles;
        }
    }

    /// <summary>The operator, through the prompt bar, and the transcript, into the log.</summary>
    private sealed class ViewerScriptHost(ViewerActions actions, ScriptSession session) : IScriptHost
    {
        public async Task<ScriptConfirmation> ConfirmAsync(string prompt, CancellationToken ct) =>
            await PromptAsync(prompt, isQuestion: false, ct) switch
            {
                0 => ScriptConfirmation.Done,
                1 => ScriptConfirmation.Skip,
                _ => ScriptConfirmation.Abort,
            };

        public async Task<bool?> AskAsync(string question, CancellationToken ct) =>
            await PromptAsync(question, isQuestion: true, ct) switch
            {
                0 => true,
                1 => false,
                _ => null,
            };

        private async Task<int> PromptAsync(string text, bool isQuestion, CancellationToken ct)
        {
            var answer = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            session.Prompt = new ScriptPrompt(text, isQuestion, answer);
            actions._logger.LogInformation("Script asks the operator: {Prompt}", text);
            actions.State.StatusMessage = $"{session.Name} is waiting for you: {text}";
            actions.State.Invalidate();
            try
            {
                await using var _ = ct.Register(() => answer.TrySetResult(2));
                return await answer.Task;
            }
            finally
            {
                session.Prompt = null;
                actions.State.Invalidate();
            }
        }

        public void Report(ScriptStep step)
        {
            if (step.Outcome is ScriptOutcome.Pass) Interlocked.Increment(ref session.Passed);
            if (step.Outcome is ScriptOutcome.Fail) Interlocked.Increment(ref session.Failed);

            actions._logger.Log(step.Outcome is ScriptOutcome.Fail ? LogLevel.Warning : LogLevel.Information,
                "Script {Step}", step.ToString());
            actions.State.StatusMessage = $"{session.Name}: {step.ToString().Trim()}";
            actions.State.Invalidate();
        }
    }
}
