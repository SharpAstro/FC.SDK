using System.Collections.Concurrent;
using System.Text;
using Microsoft.Extensions.Logging;

namespace FC.SDK.Viewer;

/// <summary>Severity of a log line, used to colour it in the log pane.</summary>
public enum ViewerLogLevel { Trace, Debug, Info, Warning, Error }

/// <summary>One captured log line.</summary>
public sealed record ViewerLogLine(DateTime Timestamp, ViewerLogLevel Level, string Category, string Message)
{
    public string Format() => $"{Timestamp:HH:mm:ss.fff} {Abbreviate(Level)} {Category}: {Message}";

    private static string Abbreviate(ViewerLogLevel level) => level switch
    {
        ViewerLogLevel.Trace => "TRC",
        ViewerLogLevel.Debug => "DBG",
        ViewerLogLevel.Info => "INF",
        ViewerLogLevel.Warning => "WRN",
        _ => "ERR",
    };
}

/// <summary>
/// Captures every log line twice: into a bounded ring the UI renders, and into a file the user can
/// attach to a bug report. The whole point of the viewer is producing that file, so the sink is
/// wired before anything else runs and flushes on every write rather than on a timer.
/// </summary>
public sealed class ViewerLog : IDisposable
{
    private const int MaxLines = 4000;

    private readonly ConcurrentQueue<ViewerLogLine> _lines = new();
    private readonly StreamWriter? _file;
    private readonly Lock _fileGate = new();
    private int _count;

    /// <summary>Path of the log file, or null if it could not be created.</summary>
    public string? FilePath { get; }

    /// <summary>Raised after each line is appended, so the UI can request a redraw.</summary>
    public event Action? LineAppended;

    public ViewerLog(string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);
            FilePath = Path.Combine(directory, $"fc-viewer-{DateTime.Now:yyyyMMdd-HHmmss}.log");
            _file = new StreamWriter(FilePath, append: false, Encoding.UTF8) { AutoFlush = true };
            _file.WriteLine($"# FC.SDK viewer log — {DateTime.Now:O}");
            _file.WriteLine($"# OS {Environment.OSVersion} {(Environment.Is64BitProcess ? "x64/arm64" : "x86")}, .NET {Environment.Version}");
        }
        catch (Exception ex)
        {
            // A missing log file must not stop the app — the in-memory pane still works.
            FilePath = null;
            Append(ViewerLogLevel.Warning, nameof(ViewerLog), $"Could not open log file in {directory}: {ex.Message}");
        }
    }

    public void Append(ViewerLogLevel level, string category, string message)
    {
        var line = new ViewerLogLine(DateTime.Now, level, category, message);

        _lines.Enqueue(line);
        if (Interlocked.Increment(ref _count) > MaxLines && _lines.TryDequeue(out _))
        {
            Interlocked.Decrement(ref _count);
        }

        if (_file is not null)
        {
            lock (_fileGate)
            {
                try { _file.WriteLine(line.Format()); }
                catch { /* disk full / handle lost — keep the UI alive */ }
            }
        }

        LineAppended?.Invoke();
    }

    /// <summary>Snapshot of the ring, oldest first.</summary>
    public ViewerLogLine[] Snapshot() => [.. _lines];

    public ILoggerFactory CreateLoggerFactory() =>
        LoggerFactory.Create(builder =>
        {
            builder.SetMinimumLevel(LogLevel.Trace);
            builder.AddProvider(new Provider(this));
        });

    public void Dispose()
    {
        lock (_fileGate)
        {
            _file?.Dispose();
        }
    }

    private sealed class Provider(ViewerLog log) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new Sink(log, ShortenCategory(categoryName));
        public void Dispose() { }

        // "FC.SDK.CanonCamera" -> "CanonCamera": the pane is narrow and the namespace is constant.
        private static string ShortenCategory(string category)
        {
            var lastDot = category.LastIndexOf('.');
            return lastDot >= 0 && lastDot < category.Length - 1 ? category[(lastDot + 1)..] : category;
        }
    }

    private sealed class Sink(ViewerLog log, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel is not LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var message = formatter(state, exception);
            if (exception is not null) message = $"{message} — {exception.GetType().Name}: {exception.Message}";

            log.Append(Map(logLevel), category, message);
        }

        private static ViewerLogLevel Map(LogLevel level) => level switch
        {
            LogLevel.Trace => ViewerLogLevel.Trace,
            LogLevel.Debug => ViewerLogLevel.Debug,
            LogLevel.Information => ViewerLogLevel.Info,
            LogLevel.Warning => ViewerLogLevel.Warning,
            _ => ViewerLogLevel.Error,
        };
    }
}
