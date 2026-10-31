using Microsoft.Extensions.Logging;

namespace SeedBomb.Services.Dataverse;

/// <summary>Forwards to <paramref name="inner"/> only at <paramref name="minimum"/> and above.</summary>
/// <param name="inner">The logger that receives the entries that pass.</param>
/// <param name="minimum">Lowest level forwarded.</param>
internal sealed class MinimumLevelLogger(ILogger inner, LogLevel minimum) : ILogger
{
    /// <inheritdoc />
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => inner.BeginScope(state);

    /// <inheritdoc />
    public bool IsEnabled(LogLevel logLevel) => logLevel >= minimum && inner.IsEnabled(logLevel);

    /// <inheritdoc />
    public void Log<TState>(
        LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (IsEnabled(logLevel))
            inner.Log(logLevel, eventId, state, exception, formatter);
    }
}
