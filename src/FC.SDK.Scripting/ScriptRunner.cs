using System.Globalization;
using FC.SDK.Canon;

namespace FC.SDK.Scripting;

/// <summary>
/// Runs a <see cref="CameraScript"/> against a camera, with an operator in the loop.
/// </summary>
/// <remarks>
/// <para>
/// A failed check does not stop the run. A script is a measurement, and the rows after a failure
/// are usually the ones that explain it. What stops a run is the operator (Abort), cancellation, or
/// a statement that cannot be evaluated at all.
/// </para>
/// <para>
/// However a run ends, live view the script started and did not stop is stopped for it. A body
/// left sending frames to a host that has gone can ignore its own buttons until power-cycled.
/// </para>
/// </remarks>
public sealed class ScriptRunner(IScriptCamera camera, IScriptHost host)
{
    private readonly Dictionary<string, object> _variables = new(StringComparer.Ordinal);
    private readonly List<ScriptStep> _steps = [];
    private CameraScript _script = null!;
    private bool _liveView;

    /// <summary>Runs the script to its end, or to the point where it was stopped.</summary>
    public async Task<ScriptRunResult> RunAsync(CameraScript script, CancellationToken ct = default)
    {
        _script = script;
        _variables.Clear();
        _steps.Clear();
        _liveView = false;

        string? stopped = null;
        try
        {
            // Anything announced before the script began is not the script's to judge.
            await camera.WaitForObjectsAsync(TimeSpan.Zero, ct);
            await RunBlockAsync(script.Statements, ct);
        }
        catch (ScriptStopException ex)
        {
            stopped = ex.Message;
        }
        catch (OperationCanceledException)
        {
            stopped = "cancelled";
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
            // The link dropped, or the camera layer refused outright. The transcript up to here is
            // still the evidence, so it is kept and the reason recorded.
            stopped = $"{ex.GetType().Name}: {ex.Message}";
        }

        if (_liveView)
        {
            try
            {
                var err = await camera.StopLiveViewAsync(CancellationToken.None);
                Record(0, "cleanup: stop-liveview", err is EdsError.OK ? ScriptOutcome.Info : ScriptOutcome.Fail, err.ToString());
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException)
            {
                Record(0, "cleanup: stop-liveview", ScriptOutcome.Fail, ex.Message);
            }
        }

        return new ScriptRunResult([.. _steps], stopped);
    }

    private async Task RunBlockAsync(IReadOnlyList<Stmt> statements, CancellationToken ct)
    {
        foreach (var statement in statements)
        {
            ct.ThrowIfCancellationRequested();
            await RunAsync(statement, ct);
        }
    }

    private async Task RunAsync(Stmt statement, CancellationToken ct)
    {
        switch (statement)
        {
            case LetStmt s:
            {
                var value = await EvalAsync(s.Value, ct);
                _variables[s.Name] = value;
                Record(s, ScriptOutcome.Info, $"{s.Name} = {Show(value)}");
                break;
            }

            case SetStmt s:
            {
                var code = await CodeAsync(s.Code, ct);
                var value = await NumberAsync(s.Value, ct);
                var err = await camera.WritePropertyAsync(code, value, ct);
                Record(s, err is EdsError.OK ? ScriptOutcome.Info : ScriptOutcome.Fail, $"0x{code:X4} = {Show(value)}: {err}");
                break;
            }

            case ExpectStmt s:
                await ExpectAsync(s, ct);
                break;

            case LogStmt s:
                Record(s, ScriptOutcome.Info, Show(await EvalAsync(s.Value, ct)));
                break;

            case StartLiveViewStmt s:
            {
                EdsEvfOutputDevice? output = s.Output is null ? null : (EdsEvfOutputDevice)await NumberAsync(s.Output, ct);
                var err = await camera.StartLiveViewAsync(output, ct);
                if (err is EdsError.OK) _liveView = true;
                Record(s, err is EdsError.OK ? ScriptOutcome.Pass : ScriptOutcome.Fail,
                    output is { } o ? $"{o}: {err}" : err.ToString());
                break;
            }

            case StopLiveViewStmt s:
            {
                var err = await camera.StopLiveViewAsync(ct);
                _liveView = false;
                Record(s, err is EdsError.OK ? ScriptOutcome.Pass : ScriptOutcome.Fail, err.ToString());
                break;
            }

            case FramesStmt s:
            {
                var count = await camera.CountFramesAsync(s.Window, ct);
                var detail = $"{count.Frames} real frames in {Show(s.Window)}"
                    + (count.Frames > 0 ? $" (last {count.LastFrameBytes:N0} bytes)" : "");
                if (s.Minimum is null)
                {
                    Record(s, ScriptOutcome.Info, detail);
                    break;
                }
                var minimum = await NumberAsync(s.Minimum, ct);
                Record(s, count.Frames >= minimum ? ScriptOutcome.Pass : ScriptOutcome.Fail, detail);
                break;
            }

            case ReleaseStmt s:
            {
                var err = await camera.ReleaseAsync(ct);
                Record(s, err is EdsError.OK ? ScriptOutcome.Pass : ScriptOutcome.Fail, err.ToString());
                break;
            }

            case WaitObjectStmt s:
            {
                var handles = await camera.WaitForObjectsAsync(s.Window, ct);
                Record(s, handles.Count > 0 ? ScriptOutcome.Pass : ScriptOutcome.Fail,
                    handles.Count > 0
                        ? $"{handles.Count} object{(handles.Count is 1 ? "" : "s")}: {string.Join(", ", handles.Select(h => $"0x{h:X8}"))}"
                        : $"nothing in {Show(s.Window)}");
                break;
            }

            case ConfirmStmt s:
                switch (await host.ConfirmAsync(s.Prompt, ct))
                {
                    case ScriptConfirmation.Done:
                        Record(s, ScriptOutcome.Info, "operator: done");
                        break;
                    case ScriptConfirmation.Skip:
                        Record(s, ScriptOutcome.Skipped, "operator: skipped");
                        break;
                    default:
                        Record(s, ScriptOutcome.Info, "operator: abort");
                        throw new ScriptStopException($"aborted by the operator at line {s.Line}");
                }
                break;

            case SleepStmt s:
                await Task.Delay(s.Duration, ct);
                break;

            case ForStmt s:
            {
                var had = _variables.TryGetValue(s.Variable, out var outer);
                foreach (var item in s.Items)
                {
                    _variables[s.Variable] = await EvalAsync(item, ct);
                    await RunBlockAsync(s.Body, ct);
                }
                if (had) _variables[s.Variable] = outer!;
                else _variables.Remove(s.Variable);
                break;
            }

            default:
                throw new InvalidOperationException($"No interpreter for {statement.GetType().Name}.");
        }
    }

    /// <summary>
    /// A comparison reports both sides, because "expect failed" with no numbers sends the reader
    /// back to the camera to find out what it actually said.
    /// </summary>
    private async Task ExpectAsync(ExpectStmt s, CancellationToken ct)
    {
        if (s.Condition is BinaryExpr { Op: not (BinaryOp.Or or BinaryOp.And) } cmp)
        {
            var left = await EvalAsync(cmp.Left, ct);
            var right = await EvalAsync(cmp.Right, ct);
            var holds = Compare(cmp, left, right);
            Record(s, holds ? ScriptOutcome.Pass : ScriptOutcome.Fail,
                holds ? Show(left) : $"left is {Show(left)}, right is {Show(right)}");
            return;
        }

        var value = await EvalAsync(s.Condition, ct);
        var truthy = value is uint n ? n != 0 : value is string { Length: > 0 };
        Record(s, truthy ? ScriptOutcome.Pass : ScriptOutcome.Fail, Show(value));
    }

    private async Task<object> EvalAsync(Expr expr, CancellationToken ct)
    {
        switch (expr)
        {
            case NumberExpr e:
                return e.Value;

            case StringExpr e:
                return e.Value;

            case NameExpr e:
                if (_variables.TryGetValue(e.Name, out var variable)) return variable;
                if (ScriptConstants.TryGet(e.Name, out var constant)) return constant;
                throw new ScriptStopException($"line {e.Line}: '{e.Name}' is not defined");

            case ReadExpr e:
            {
                var code = await CodeAsync(e.Code, ct);
                // A property the body will not report is a finding, not a number. Stopping here
                // beats carrying on with a made-up zero that the next `expect` would then judge.
                return await camera.ReadPropertyAsync(code, ct)
                    ?? throw new ScriptStopException($"line {e.Line}: the camera did not answer a read of 0x{code:X4}");
            }

            case AskExpr e:
            {
                var answer = await host.AskAsync(e.Prompt, ct)
                    ?? throw new ScriptStopException($"aborted by the operator at line {e.Line}");
                Record(e.Line, $"ask \"{e.Prompt}\"", ScriptOutcome.Info, answer ? "operator: yes" : "operator: no");
                return answer ? 1u : 0u;
            }

            case NotExpr e:
                return ~await NumberAsync(e.Operand, ct);

            case BinaryExpr e:
            {
                var left = await EvalAsync(e.Left, ct);
                var right = await EvalAsync(e.Right, ct);
                return e.Op switch
                {
                    BinaryOp.Or => Number(left, e) | Number(right, e),
                    BinaryOp.And => Number(left, e) & Number(right, e),
                    _ => Compare(e, left, right) ? 1u : 0u,
                };
            }

            default:
                throw new InvalidOperationException($"No evaluator for {expr.GetType().Name}.");
        }
    }

    private static bool Compare(BinaryExpr e, object left, object right) => e.Op switch
    {
        BinaryOp.Eq => Equals(left, right),
        BinaryOp.Ne => !Equals(left, right),
        BinaryOp.Lt => Number(left, e) < Number(right, e),
        BinaryOp.Gt => Number(left, e) > Number(right, e),
        BinaryOp.Le => Number(left, e) <= Number(right, e),
        BinaryOp.Ge => Number(left, e) >= Number(right, e),
        _ => throw new InvalidOperationException($"{e.Op} is not a comparison."),
    };

    private async Task<uint> NumberAsync(Expr expr, CancellationToken ct) => Number(await EvalAsync(expr, ct), expr);

    private static uint Number(object value, Expr at) => value is uint n
        ? n
        : throw new ScriptStopException($"line {at.Line}: a number is needed here, and this is the text \"{value}\"");

    private async Task<ushort> CodeAsync(Expr expr, CancellationToken ct)
    {
        var code = await NumberAsync(expr, ct);
        return code <= ushort.MaxValue
            ? (ushort)code
            : throw new ScriptStopException($"line {expr.Line}: 0x{code:X} is not a property code (they are 16 bits)");
    }

    private void Record(Stmt statement, ScriptOutcome outcome, string detail) =>
        Record(statement.Line, _script.LineText(statement.Line), outcome, detail);

    private void Record(int line, string source, ScriptOutcome outcome, string detail)
    {
        var step = new ScriptStep(line, source, outcome, detail);
        _steps.Add(step);
        host.Report(step);
    }

    private static string Show(object value) => value is uint n ? $"0x{n:X} ({n})" : $"\"{value}\"";

    private static string Show(TimeSpan span) => span.TotalSeconds >= 1
        ? string.Create(CultureInfo.InvariantCulture, $"{span.TotalSeconds:0.###} s")
        : string.Create(CultureInfo.InvariantCulture, $"{span.TotalMilliseconds:0} ms");

    /// <summary>Ends the run, with the reason as its message.</summary>
    private sealed class ScriptStopException(string message) : Exception(message);
}
