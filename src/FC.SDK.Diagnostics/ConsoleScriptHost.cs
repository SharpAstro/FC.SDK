using FC.SDK.Scripting;

/// <summary>
/// The operator at a terminal: a prompt is printed, announced with a beep, and answered with a key
/// and Enter.
/// </summary>
/// <remarks>
/// With no terminal (stdin redirected: a pipe, CI, an agent's shell) nobody can answer. A
/// <c>confirm</c> is then recorded as skipped and the run carries on, since the steps around it still
/// measure something; an <c>ask</c> stops the run, because an answer nobody gave must not go into a
/// transcript as "no".
/// </remarks>
internal sealed class ConsoleScriptHost(Action<string> log) : IScriptHost
{
    private static bool Attended => !Console.IsInputRedirected;

    public Task<ScriptConfirmation> ConfirmAsync(string prompt, CancellationToken ct)
    {
        if (!Attended)
        {
            log($"  >>> {prompt}");
            log("      no terminal to answer at, so this is recorded as skipped");
            return Task.FromResult(ScriptConfirmation.Skip);
        }

        return Task.Run(() =>
        {
            while (true)
            {
                Announce(prompt, "[Enter] done   [s] skip   [a] abort");
                switch (Console.ReadLine()?.Trim().ToLowerInvariant())
                {
                    case "" or "d" or "done": return ScriptConfirmation.Done;
                    case "s" or "skip": return ScriptConfirmation.Skip;
                    case null or "a" or "abort": return ScriptConfirmation.Abort;
                }
            }
        }, ct);
    }

    public Task<bool?> AskAsync(string question, CancellationToken ct)
    {
        if (!Attended)
        {
            log($"  >>> {question}");
            log("      no terminal to answer at, so the run stops here");
            return Task.FromResult<bool?>(null);
        }

        return Task.Run<bool?>(() =>
        {
            while (true)
            {
                Announce(question, "[y] yes   [n] no   [a] abort");
                switch (Console.ReadLine()?.Trim().ToLowerInvariant())
                {
                    case "y" or "yes": return true;
                    case "n" or "no": return false;
                    case null or "a" or "abort": return null;
                }
            }
        }, ct);
    }

    public void Report(ScriptStep step) => log(step.ToString());

    private static void Announce(string text, string keys)
    {
        // The person this is for is looking at a camera, not at the terminal.
        if (OperatingSystem.IsWindows()) Console.Beep(1200, 150);
        Console.WriteLine();
        Console.WriteLine($"  >>> {text}");
        Console.Write($"      {keys}: ");
    }
}
