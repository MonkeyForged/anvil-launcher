using Microsoft.Extensions.Logging;

namespace Anvil.Core.Logging;

/// <summary>Leitet Log-Einträge formatiert in den rotierenden Datei-Writer.</summary>
public sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly RotatingFileWriter _writer;
    private readonly LogLevelSwitch _levelSwitch;
    private readonly TimeProvider _time;

    public FileLoggerProvider(
        RotatingFileWriter writer, LogLevelSwitch levelSwitch, TimeProvider time)
    {
        _writer = writer;
        _levelSwitch = levelSwitch;
        _time = time;
    }

    public ILogger CreateLogger(string categoryName)
        => new FileLogger(categoryName, _writer, _levelSwitch, _time);

    public void Dispose() => _writer.Dispose();

    private sealed class FileLogger(
        string category, RotatingFileWriter writer, LogLevelSwitch levelSwitch,
        TimeProvider time) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => levelSwitch.IsEnabled(logLevel);

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            var entry = new LogEntry(
                time.GetLocalNow(), logLevel, category,
                formatter(state, exception), exception?.ToString());
            writer.WriteLine(LogFormatter.Format(entry));
        }
    }
}
