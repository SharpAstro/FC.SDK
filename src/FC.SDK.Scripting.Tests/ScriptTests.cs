using FC.SDK.Canon;
using Shouldly;
using Xunit;

namespace FC.SDK.Scripting.Tests;

/// <summary>
/// The script language: what parses, what a mistake is told, and what a run reports. No camera:
/// <see cref="FakeCamera"/> stands in, so these pin the language and the interpreter, and say
/// nothing about what a body does.
/// </summary>
public class ScriptTests
{
    private const ushort EvfOutput = 0xD1B0;

    private static async Task<(ScriptRunResult Result, FakeCamera Camera, FakeHost Host)> RunAsync(
        string source, FakeCamera? camera = null, FakeHost? host = null)
    {
        camera ??= new FakeCamera();
        host ??= new FakeHost();
        var result = await new ScriptRunner(camera, host)
            .RunAsync(CameraScript.Parse(source), TestContext.Current.CancellationToken);
        return (result, camera, host);
    }

    // --- Parsing ---

    [Fact]
    public void Statements_need_no_terminator_and_comments_are_ignored()
    {
        var script = CameraScript.Parse("""
            # a comment
            let rest = read 0xD1B0   # trailing comment
            start-liveview stop-liveview
            expect rest == 0
            """);

        script.Statements.Count.ShouldBe(4);
    }

    /// <summary>
    /// The reason the grammar is stratified: comparison binds loosest, so the right-hand side of
    /// <c>==</c> can be a mask built with <c>|</c> without brackets.
    /// </summary>
    [Fact]
    public async Task Comparison_binds_looser_than_the_bit_operators()
    {
        var (result, _, _) = await RunAsync("""
            expect 3 == 1 | 2
            expect 6 & 3 == 2
            expect ~0 & 0xFF == 0xFF
            expect (1 | 2) & 2 == 2
            """);

        result.Failed.ShouldBe(0);
        result.Passed.ShouldBe(4);
    }

    [Theory]
    [InlineData("EdsEvfOutputDevice.PC", 2u)]
    [InlineData("EvfOutputDevice.PC", 2u)]
    [InlineData("EvfOutputDevice.TFT | EvfOutputDevice.PC", 3u)]
    [InlineData("EdsTv.Bulb", 0x0Cu)]
    [InlineData("Tv.Bulb", 0x0Cu)]
    [InlineData("CanonCaptureDestination.Host", 4u)]
    [InlineData("CaptureDestination.Host", 4u)]
    [InlineData("true", 1u)]
    public async Task Enum_members_are_constants_with_or_without_the_type_prefix(string expression, uint expected)
    {
        var (result, _, _) = await RunAsync($"expect {expression} == {expected}");

        result.Succeeded.ShouldBeTrue(result.Steps[0].ToString());
    }

    /// <summary>
    /// A property name is its PTP code, because that is what <c>read</c> and <c>set</c> take. EDSDK
    /// numbers Evf_OutputDevice 0x500, which no camera would answer to.
    /// </summary>
    [Theory]
    [InlineData("Evf_OutputDevice", 0xD1B0u)]
    [InlineData("PropertyId.Evf_OutputDevice", 0xD1B0u)]
    [InlineData("EdsPropertyId.ISOSpeed", 0xD103u)]
    public async Task A_property_name_is_its_PTP_code(string name, uint code)
    {
        var (result, _, _) = await RunAsync($"expect {name} == {code}");

        result.Succeeded.ShouldBeTrue(result.Steps[0].ToString());
    }

    /// <summary>
    /// The old diagnostics dispatch ran its DEFAULT experiment on a mistyped mode name, minutes of
    /// shutter actuations. A script must refuse before it starts.
    /// </summary>
    [Theory]
    [InlineData("start-liveveiw", 1, "unexpected")]
    [InlineData("expect read 0xD1B0 ==", 1, "the end of the script")]
    [InlineData("let x = 1\nexpect y == 1", 2, "'y' is not defined")]
    [InlineData("expect EvfOutputDevice.Pc == 2", 1, "Did you mean")]
    [InlineData("frames 3", 1, "a duration")]
    [InlineData("expect 1 == 1 $", 1, "unexpected character '$'")]
    [InlineData("expect 0x1FFFFFFFF == 1", 1, "does not fit in 32 bits")]
    public void A_mistake_is_refused_before_anything_runs(string source, int line, string says)
    {
        var ex = Should.Throw<ScriptSyntaxException>(() => CameraScript.Parse(source));

        ex.Line.ShouldBe(line);
        ex.Message.ShouldContain(says);
    }

    [Fact]
    public void A_loop_variable_is_not_defined_after_its_loop()
    {
        Should.Throw<ScriptSyntaxException>(() => CameraScript.Parse("""
            for o in [1, 2] { log o }
            log o
            """)).Line.ShouldBe(2);
    }

    // --- Running ---

    [Fact]
    public async Task A_failed_check_reports_both_sides_and_the_run_carries_on()
    {
        var camera = new FakeCamera { Properties = { [EvfOutput] = 3 } };

        var (result, _, _) = await RunAsync("""
            expect read 0xD1B0 == 0
            expect read 0xD1B0 == 3
            """, camera);

        result.Stopped.ShouldBeNull();
        result.Failed.ShouldBe(1);
        result.Passed.ShouldBe(1);
        result.Steps[0].Detail.ShouldBe("left is 0x3 (3), right is 0x0 (0)");
        result.Steps[0].ToString().ShouldStartWith("FAIL   1: expect read 0xD1B0 == 0");
    }

    [Fact]
    public async Task A_loop_runs_its_body_once_per_item_with_the_item_bound()
    {
        var (result, camera, _) = await RunAsync("""
            for output in [EvfOutputDevice.PC, EvfOutputDevice.TFT | EvfOutputDevice.PC] {
                start-liveview output
                expect read Evf_OutputDevice == output
                stop-liveview
            }
            """);

        result.Succeeded.ShouldBeTrue(result.Summary);
        camera.Starts.ShouldBe([EdsEvfOutputDevice.PC, EdsEvfOutputDevice.TFT | EdsEvfOutputDevice.PC]);
    }

    [Fact]
    public async Task Frames_with_a_minimum_is_a_check_and_without_one_is_a_reading()
    {
        var camera = new FakeCamera { FramesPerCount = 12 };

        var (result, _, _) = await RunAsync("""
            start-liveview
            frames 3s
            frames 3s min 10
            frames 500ms min 30
            stop-liveview
            """, camera);

        result.Steps.Skip(1).Take(3).Select(s => s.Outcome)
            .ShouldBe([ScriptOutcome.Info, ScriptOutcome.Pass, ScriptOutcome.Fail]);
        result.Steps[3].Detail.ShouldStartWith("12 real frames in 500 ms");
    }

    [Fact]
    public async Task Wait_object_passes_on_an_image_and_fails_on_silence()
    {
        var camera = new FakeCamera();
        camera.Announce(0x9191E521, 0x9191E522);

        var (result, _, _) = await RunAsync("""
            confirm "press the shutter"
            wait-object 10s
            wait-object 10s
            """, camera, new FakeHost { OnConfirm = () => camera.Announce(0xAA) });

        result.Steps[1].Outcome.ShouldBe(ScriptOutcome.Pass);
        // The two announced before the script began are not the script's to judge.
        result.Steps[1].Detail.ShouldBe("1 object: 0x000000AA");
        result.Steps[2].Outcome.ShouldBe(ScriptOutcome.Fail);
        camera.Released.ShouldBe([0x9191E521u, 0x9191E522u, 0xAAu]);
    }

    [Fact]
    public async Task The_operators_answers_are_part_of_the_transcript()
    {
        var host = new FakeHost { Confirmations = { ScriptConfirmation.Skip }, Answers = { true, false } };

        var (result, _, _) = await RunAsync("""
            confirm "press the shutter"
            log ask "screen on?"
            expect ask "did it expose?"
            """, host: host);

        result.Steps.Select(s => (s.Outcome, s.Detail)).ShouldBe(
        [
            (ScriptOutcome.Skipped, "operator: skipped"),
            (ScriptOutcome.Info, "operator: yes"),
            (ScriptOutcome.Info, "0x1 (1)"),
            (ScriptOutcome.Info, "operator: no"),
            (ScriptOutcome.Fail, "0x0 (0)"),
        ]);
        result.Skipped.ShouldBe(1);
        host.Reported.ShouldBe(result.Steps);
    }

    /// <summary>
    /// A body left sending frames to a host that has gone can ignore its own buttons until it is
    /// power-cycled, so an abort must not leave live view running.
    /// </summary>
    [Fact]
    public async Task An_abort_stops_the_run_and_the_live_view_it_started()
    {
        var host = new FakeHost { Confirmations = { ScriptConfirmation.Abort } };

        var (result, camera, _) = await RunAsync("""
            start-liveview
            confirm "press the shutter"
            release
            stop-liveview
            """, host: host);

        result.Stopped.ShouldBe("aborted by the operator at line 2");
        result.Succeeded.ShouldBeFalse();
        camera.Releases.ShouldBe(0);
        camera.LiveView.ShouldBeFalse();
        result.Steps[^1].Source.ShouldBe("cleanup: stop-liveview");
    }

    [Fact]
    public async Task A_refused_command_is_a_failure_with_the_cameras_own_word()
    {
        var camera = new FakeCamera { WriteAnswer = EdsError.DeviceBusy, ReleaseAnswer = EdsError.DeviceBusy };

        var (result, _, _) = await RunAsync("""
            set ISOSpeed = ISOSpeed.ISO_400
            release
            """, camera);

        result.Steps[0].Outcome.ShouldBe(ScriptOutcome.Fail);
        result.Steps[0].Detail.ShouldBe("0xD103 = 0x58 (88): DeviceBusy");
        result.Steps[1].Detail.ShouldBe("DeviceBusy");
    }

    /// <summary>
    /// A property the body will not report is not zero. Carrying on would let the next check judge
    /// a number nobody read.
    /// </summary>
    [Fact]
    public async Task A_read_the_camera_does_not_answer_stops_the_run()
    {
        var (result, _, _) = await RunAsync("""
            let x = read 0xD1FF
            expect x == 0
            """);

        result.Stopped.ShouldBe("line 1: the camera did not answer a read of 0xD1FF");
        result.Steps.ShouldBeEmpty();
    }

    /// <summary>Every script shipped in <c>scripts/</c> parses, names and all.</summary>
    [Fact]
    public void The_shipped_scripts_parse()
    {
        var files = Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "scripts"), "*.camscript");

        files.ShouldNotBeEmpty();
        foreach (var file in files)
            Should.NotThrow(() => CameraScript.Parse(File.ReadAllText(file)), Path.GetFileName(file));
    }

    /// <summary>
    /// The shipped live-view output script against a fake that behaves as issue #21 says an EOS 6D
    /// does (0 at rest). A dry run of the script's logic, not of a camera.
    /// </summary>
    [Fact]
    public async Task The_evf_output_script_passes_against_a_body_that_restores()
    {
        var camera = new FakeCamera { FramesPerCount = 58 };
        var host = new FakeHost { OnConfirm = () => camera.Announce(0x9191E521), Answers = { false, true } };
        var source = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "scripts", "evf-output.camscript"));

        // The script's sleeps are real; a few seconds here buys the whole file being exercised.
        var (result, _, _) = await RunAsync(source, camera, host);

        result.Succeeded.ShouldBeTrue(string.Join('\n', result.Steps));
        camera.LiveView.ShouldBeFalse();
        camera.Properties[EvfOutput].ShouldBe(0u);
    }
}

/// <summary>A camera that holds properties in a dictionary and does what it is told.</summary>
internal sealed class FakeCamera : IScriptCamera
{
    private const ushort EvfOutput = 0xD1B0;
    private const uint Pc = (uint)EdsEvfOutputDevice.PC;

    private readonly Queue<uint> _announced = new();
    private uint? _atRest;

    public Dictionary<ushort, uint> Properties { get; } = new() { [EvfOutput] = 0 };
    public List<EdsEvfOutputDevice?> Starts { get; } = [];
    public List<uint> Released { get; } = [];
    public bool LiveView { get; private set; }
    public int Releases { get; private set; }
    public int FramesPerCount { get; init; } = 60;
    public EdsError WriteAnswer { get; init; } = EdsError.OK;
    public EdsError ReleaseAnswer { get; init; } = EdsError.OK;

    public void Announce(params uint[] handles)
    {
        foreach (var handle in handles) _announced.Enqueue(handle);
    }

    public Task<uint?> ReadPropertyAsync(ushort code, CancellationToken ct) =>
        Task.FromResult(Properties.TryGetValue(code, out var value) ? value : (uint?)null);

    public Task<EdsError> WritePropertyAsync(ushort code, uint value, CancellationToken ct)
    {
        if (WriteAnswer is EdsError.OK) Properties[code] = value;
        return Task.FromResult(WriteAnswer);
    }

    public Task<EdsError> StartLiveViewAsync(EdsEvfOutputDevice? output, CancellationToken ct)
    {
        Starts.Add(output);
        if ((Properties[EvfOutput] & Pc) == 0) _atRest = Properties[EvfOutput];
        Properties[EvfOutput] = output is { } o ? (uint)o : Properties[EvfOutput] | Pc;
        LiveView = true;
        return Task.FromResult(EdsError.OK);
    }

    public Task<EdsError> StopLiveViewAsync(CancellationToken ct)
    {
        Properties[EvfOutput] = _atRest ?? (uint)EdsEvfOutputDevice.TFT;
        _atRest = null;
        LiveView = false;
        return Task.FromResult(EdsError.OK);
    }

    public Task<ScriptFrameCount> CountFramesAsync(TimeSpan window, CancellationToken ct) =>
        Task.FromResult(new ScriptFrameCount(LiveView ? FramesPerCount : 0, LiveView ? 90_000 : 0));

    public Task<EdsError> ReleaseAsync(CancellationToken ct)
    {
        Releases++;
        return Task.FromResult(ReleaseAnswer);
    }

    public Task<IReadOnlyList<uint>> WaitForObjectsAsync(TimeSpan window, CancellationToken ct)
    {
        var handles = _announced.ToArray();
        _announced.Clear();
        Released.AddRange(handles);
        return Task.FromResult<IReadOnlyList<uint>>(handles);
    }
}

/// <summary>An operator with their answers decided in advance.</summary>
internal sealed class FakeHost : IScriptHost
{
    private int _confirmed;
    private int _answered;

    /// <summary>Answers to successive <c>confirm</c>s; Done once they run out.</summary>
    public List<ScriptConfirmation> Confirmations { get; } = [];

    /// <summary>Answers to successive <c>ask</c>s; yes once they run out.</summary>
    public List<bool> Answers { get; } = [];

    public List<ScriptStep> Reported { get; } = [];
    public Action? OnConfirm { get; init; }

    public Task<ScriptConfirmation> ConfirmAsync(string prompt, CancellationToken ct)
    {
        var answer = _confirmed < Confirmations.Count ? Confirmations[_confirmed++] : ScriptConfirmation.Done;
        if (answer is ScriptConfirmation.Done) OnConfirm?.Invoke();
        return Task.FromResult(answer);
    }

    public Task<bool?> AskAsync(string question, CancellationToken ct) =>
        Task.FromResult<bool?>(_answered >= Answers.Count || Answers[_answered++]);

    public void Report(ScriptStep step) => Reported.Add(step);
}
