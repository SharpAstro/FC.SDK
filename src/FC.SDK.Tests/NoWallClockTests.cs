using Shouldly;
using Xunit;

namespace FC.SDK.Tests;

/// <summary>
/// The library and the script runner wait on a <see cref="TimeProvider"/>, never on the wall clock.
/// </summary>
/// <remarks>
/// A wait written against the system clock cannot be moved by a test, so the test either waits it
/// out or is not written: a pan's settle wait was once pinned by a test that timed three real
/// seconds. This reads the sources and fails on the calls that reach the wall clock directly, so
/// the next one is caught when it is written. The hosts (the viewer, the diagnostics, the sample)
/// are not covered: they run against a body, in real time.
/// </remarks>
public class NoWallClockTests
{
    /// <summary>Calls that read or wait on the system clock, and what to write in their place.</summary>
    private static readonly (string Call, string Instead)[] Banned =
    [
        ("DateTime.UtcNow", "TimeProvider.GetUtcNow() or GetTimestamp()"),
        ("DateTime.Now", "TimeProvider.GetLocalNow()"),
        ("DateTimeOffset.UtcNow", "TimeProvider.GetUtcNow()"),
        ("DateTimeOffset.Now", "TimeProvider.GetLocalNow()"),
        ("Stopwatch.", "TimeProvider.GetTimestamp() and GetElapsedTime()"),
        (".CancelAfter(", "new CancellationTokenSource(delay, timeProvider), linked with the caller's token"),
        ("Thread.Sleep(", "await Task.Delay(delay, timeProvider, ct)"),
    ];

    [Theory]
    [InlineData("FC.SDK")]
    [InlineData("FC.SDK.Scripting")]
    public void Nothing_reads_or_waits_on_the_system_clock(string project)
    {
        var offences = new List<string>();
        foreach (var file in SourcesOf(project))
        {
            var lines = File.ReadAllLines(file);
            for (int i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                if (line.TrimStart().StartsWith("//", StringComparison.Ordinal)) continue;

                foreach (var (call, instead) in Banned)
                {
                    if (line.Contains(call, StringComparison.Ordinal))
                        offences.Add($"{Path.GetFileName(file)}:{i + 1} uses {call}; use {instead}");
                }

                // A delay names its clock: Task.Delay(delay, timeProvider[, ct]).
                if (line.Contains("Task.Delay(", StringComparison.Ordinal) && !NamesAClock(line))
                    offences.Add($"{Path.GetFileName(file)}:{i + 1} delays on the system clock; pass the TimeProvider");

                // So does a source that cancels itself after a delay.
                if (line.Contains("new CancellationTokenSource(", StringComparison.Ordinal)
                    && !line.Contains("new CancellationTokenSource()", StringComparison.Ordinal)
                    && !NamesAClock(line))
                    offences.Add($"{Path.GetFileName(file)}:{i + 1} sets a deadline on the system clock; pass the TimeProvider");
            }
        }

        offences.ShouldBeEmpty();
    }

    /// <summary>The two names a time provider goes by in these sources: a field and a parameter.</summary>
    private static bool NamesAClock(string line) =>
        line.Contains("_time", StringComparison.Ordinal) || line.Contains(", time", StringComparison.Ordinal);

    private static IEnumerable<string> SourcesOf(string project)
    {
        // Found by walking up from the test binary to the solution, not from [CallerFilePath]: CI
        // builds with ContinuousIntegrationBuild, which rewrites source paths to /_/ and so to
        // nothing on disk.
        var repo = new DirectoryInfo(AppContext.BaseDirectory);
        while (repo is not null && !File.Exists(Path.Combine(repo.FullName, "FC.SDK.slnx")))
            repo = repo.Parent;
        repo.ShouldNotBeNull("FC.SDK.slnx was expected above the test binary");

        var root = Path.Combine(repo.FullName, "src", project);
        Directory.Exists(root).ShouldBeTrue($"the sources of {project} were expected at {root}");

        var separator = Path.DirectorySeparatorChar;
        return Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{separator}obj{separator}", StringComparison.Ordinal)
                        && !f.Contains($"{separator}bin{separator}", StringComparison.Ordinal));
    }
}
