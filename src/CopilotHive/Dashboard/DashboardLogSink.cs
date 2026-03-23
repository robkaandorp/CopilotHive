using System.Collections.Concurrent;

namespace CopilotHive.Dashboard;

/// <summary>
/// Circular buffer that captures log entries for the dashboard live log view.
/// Registered as a singleton and shared between <see cref="DashboardLoggerProvider"/>
/// and <see cref="DashboardStateService"/>.
/// </summary>
public sealed class DashboardLogSink
{
    private readonly ConcurrentQueue<LogEntry> _entries = new();
    private readonly int _maxEntries;

    /// <summary>Fired when a new log entry is added.</summary>
    public event Action<LogEntry>? OnNewEntry;

    /// <summary>Creates a log sink with the given maximum capacity.</summary>
    public DashboardLogSink(int maxEntries = 1000)
    {
        _maxEntries = maxEntries;
    }

    /// <summary>Adds a log entry to the circular buffer.</summary>
    public void Add(LogEntry entry)
    {
        _entries.Enqueue(entry);
        while (_entries.Count > _maxEntries)
            _entries.TryDequeue(out _);
        OnNewEntry?.Invoke(entry);
    }

    /// <summary>Returns the most recent log entries.</summary>
    public IReadOnlyList<LogEntry> GetRecent(int count)
    {
        var all = _entries.ToArray();
        return all.Length <= count ? all : all[^count..];
    }
}

/// <summary>
/// Logger provider that feeds log entries into the <see cref="DashboardLogSink"/>.
/// </summary>
public sealed class DashboardLoggerProvider : ILoggerProvider
{
    private readonly DashboardLogSink _sink;

    /// <summary>Creates a provider backed by the given sink.</summary>
    public DashboardLoggerProvider(DashboardLogSink sink) => _sink = sink;

    /// <inheritdoc />
    public ILogger CreateLogger(string categoryName) => new DashboardLogger(categoryName, _sink);

    /// <inheritdoc />
    public void Dispose() { }
}

file sealed class DashboardLogger(string category, DashboardLogSink sink) : ILogger
{
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
        Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel)) return;

        sink.Add(new LogEntry
        {
            Timestamp = DateTime.UtcNow,
            Level = logLevel,
            Category = SimplifyCategory(category),
            Message = formatter(state, exception),
        });
    }

    private static string SimplifyCategory(string cat)
    {
        var lastDot = cat.LastIndexOf('.');
        return lastDot >= 0 ? cat[(lastDot + 1)..] : cat;
    }
}

/// <summary>A single captured log entry for the dashboard.</summary>
public sealed class LogEntry
{
    /// <summary>When the entry was logged.</summary>
    public DateTime Timestamp { get; init; }
    /// <summary>Log severity level.</summary>
    public LogLevel Level { get; init; }
    /// <summary>Simplified logger category name.</summary>
    public string Category { get; init; } = "";
    /// <summary>Formatted log message.</summary>
    public string Message { get; init; } = "";

    /// <summary>CSS class for the log level.</summary>
    public string LevelCss => Level switch
    {
        LogLevel.Warning => "log-warn",
        LogLevel.Error or LogLevel.Critical => "log-error",
        LogLevel.Debug or LogLevel.Trace => "log-debug",
        _ => "log-info",
    };

    /// <summary>Short log level label.</summary>
    public string LevelLabel => Level switch
    {
        LogLevel.Information => "INF",
        LogLevel.Warning => "WRN",
        LogLevel.Error => "ERR",
        LogLevel.Critical => "CRT",
        LogLevel.Debug => "DBG",
        _ => "???",
    };
}
