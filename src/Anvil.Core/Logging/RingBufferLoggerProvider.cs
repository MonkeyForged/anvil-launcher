using Microsoft.Extensions.Logging;

namespace Anvil.Core.Logging;

/// <summary>Leitet Log-Einträge in den Ringpuffer (Datenquelle der Log-Seite).</summary>
public sealed class RingBufferLoggerProvider : ILoggerProvider
{
    private readonly LogRingBuffer _buffer;
    private readonly LogLevelSwitch _levelSwitch;
    private readonly TimeProvider _time;

    public RingBufferLoggerProvider(
        LogRingBuffer buffer, LogLevelSwitch levelSwitch, TimeProvider time)
    {
        _buffer = buffer;
        _levelSwitch = levelSwitch;
        _time = time;
    }

    public ILogger CreateLogger(string categoryName)
        => new RingBufferLogger(categoryName, _buffer, _levelSwitch, _time);

    public void Dispose()
    {
    }

    private sealed class RingBufferLogger(
        string category, LogRingBuffer buffer, LogLevelSwitch levelSwitch, TimeProvider time)
        : ILogger
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

            buffer.Add(new LogEntry(
                time.GetLocalNow(), logLevel, category,
                formatter(state, exception), exception?.ToString()));
        }
    }
}
