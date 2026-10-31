using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;

namespace SeedBomb.Bulk.Tests;

/// <summary>Keeps every formatted log line so tests can assert on the diagnostics a run emits.</summary>
internal sealed class RecordingLogger<T> : ILogger<T>
{
    private readonly ConcurrentQueue<string> _lines = new();

    public IReadOnlyList<string> Lines => [.. _lines];

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
        _lines.Enqueue(formatter(state, exception));
}
