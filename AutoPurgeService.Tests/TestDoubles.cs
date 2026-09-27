using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AutoPurgeService.Tests;

internal sealed class ListLogger<T> : ILogger<T>
{
    public ConcurrentQueue<(LogLevel Level, string Message)> Entries { get; } = new();

    // Lets a test react to a log entry at the moment it is written, e.g. to cancel mid-purge.
    public Action<string>? OnLog { get; set; }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        var message = formatter(state, exception);
        Entries.Enqueue((logLevel, message));
        OnLog?.Invoke(message);
    }

    public int Count(LogLevel level, string text) => Entries.Count(e => e.Level == level && e.Message.Contains(text));

    public bool Has(LogLevel level, string text) => Count(level, text) > 0;
}

// Stands in for the configuration-backed monitor; Change() simulates an appsettings.json edit.
internal sealed class TestOptionsMonitor : IOptionsMonitor<PurgeSettings>
{
    private readonly List<Action<PurgeSettings, string?>> _listeners = new();

    public TestOptionsMonitor(PurgeSettings value) => CurrentValue = value;

    public PurgeSettings CurrentValue { get; private set; }

    public PurgeSettings Get(string? name) => CurrentValue;

    public IDisposable? OnChange(Action<PurgeSettings, string?> listener)
    {
        lock (_listeners) _listeners.Add(listener);
        return new Subscription(() => { lock (_listeners) _listeners.Remove(listener); });
    }

    public void Change(PurgeSettings value)
    {
        CurrentValue = value;
        Action<PurgeSettings, string?>[] listeners;
        lock (_listeners) listeners = _listeners.ToArray();
        foreach (var listener in listeners) listener(value, Options.DefaultName);
    }

    private sealed class Subscription(Action dispose) : IDisposable
    {
        public void Dispose() => dispose();
    }
}
