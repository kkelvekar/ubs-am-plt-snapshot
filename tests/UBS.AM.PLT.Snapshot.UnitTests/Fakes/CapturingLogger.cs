using Microsoft.Extensions.Logging;

namespace UBS.AM.PLT.Snapshot.UnitTests.Fakes;

public sealed record LogEntry(
    LogLevel Level,
    string Message,
    IReadOnlyDictionary<string, object?> State);

public sealed class CapturingLogger<T> : ILogger<T>
{
    private readonly List<LogEntry> _entries = [];

    public IReadOnlyList<LogEntry> Entries => _entries;

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        var values = state as IReadOnlyList<KeyValuePair<string, object?>>
            ?? Array.Empty<KeyValuePair<string, object?>>();

        _entries.Add(new LogEntry(
            logLevel,
            formatter(state, exception),
            values.ToDictionary(kv => kv.Key, kv => kv.Value)));
    }
}
