using Microsoft.Extensions.Logging;
using System.IO;
using System.Text;

namespace Seedbomb.Services.Diagnostics;

/// <summary>Appends log lines to one file per day under <see cref="AppPaths.Logs"/>.</summary>
public sealed class FileLoggerProvider : ILoggerProvider
{
    private const int RetainDays = 7;
    private readonly Lock _gate = new();
    private readonly LogLevel _minimum;

    /// <summary>Creates the provider and sweeps files older than seven days.</summary>
    /// <param name="minimum">Lowest level written to disk.</param>
    public FileLoggerProvider(LogLevel minimum = LogLevel.Information)
    {
        _minimum = minimum;
        Prune();
    }

    /// <inheritdoc />
    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    /// <inheritdoc />
    public void Dispose()
    {
    }

    private bool IsLevelEnabled(LogLevel level) => level >= _minimum && level != LogLevel.None;

    private void Append(string line)
    {
        // ponytail: one global lock, one open/close per line. Fine at this app's volume; move to a
        // queued writer if logging ever shows up in a run profile.
        lock (_gate)
        {
            try
            {
                File.AppendAllText(CurrentFile(), line + Environment.NewLine, Encoding.UTF8);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private static string CurrentFile() =>
        Path.Combine(AppPaths.Logs, $"seedbomb-{DateTime.Now:yyyyMMdd}.log");

    private static void Prune()
    {
        try
        {
            var cutoff = DateTime.Now.AddDays(-RetainDays);
            foreach (var file in Directory.EnumerateFiles(AppPaths.Logs, "seedbomb-*.log"))
                if (File.GetLastWriteTime(file) < cutoff)
                    File.Delete(file);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private sealed class FileLogger(FileLoggerProvider owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => owner.IsLevelEnabled(logLevel);

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            ArgumentNullException.ThrowIfNull(formatter);

            var line = new StringBuilder()
                .Append(DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss.fff zzz"))
                .Append(" [").Append(logLevel).Append("] ")
                .Append(category).Append(": ")
                .Append(formatter(state, exception));
            if (exception is not null)
                line.AppendLine().Append(exception);
            owner.Append(line.ToString());
        }
    }
}