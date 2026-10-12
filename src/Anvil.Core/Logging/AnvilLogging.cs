using Anvil.Core;
using Microsoft.Extensions.Logging;

namespace Anvil.Core.Logging;

/// <summary>
/// Komposition des Logging-Fundaments: baut die Logger-Fabrik mit Datei-
/// und Ringpuffer-Sink. Die Instanz hält die Composition Root (seit S1.4).
/// </summary>
public sealed class AnvilLogging : IDisposable
{
    private const int RingBufferCapacity = 2000;

    private AnvilLogging(
        ILoggerFactory factory, LogRingBuffer ringBuffer, LogLevelSwitch levelSwitch,
        string logDirectory, string logFilePath)
    {
        Factory = factory;
        RingBuffer = ringBuffer;
        LevelSwitch = levelSwitch;
        LogDirectory = logDirectory;
        LogFilePath = logFilePath;
    }

    public ILoggerFactory Factory { get; }

    public LogRingBuffer RingBuffer { get; }

    /// <summary>Mindest-Level der Datei. Der Ringpuffer hat einen eigenen, festen Schalter auf Trace.</summary>
    public LogLevelSwitch LevelSwitch { get; }

    /// <summary>Fürs Öffnen des Log-Ordners und die Settings-Seite (S1.6).</summary>
    public string LogDirectory { get; }

    public string LogFilePath { get; }

    public static AnvilLogging Create(
        string? logDirectory = null,
        TimeProvider? time = null,
        LogLevel initialLevel = LogLevel.Information)
    {
        var directory = logDirectory
            ?? XdgPaths.LogDirectory(Environment.GetEnvironmentVariable);
        var clock = time ?? TimeProvider.System;
        var levelSwitch = new LogLevelSwitch(initialLevel);
        var ringBuffer = new LogRingBuffer(RingBufferCapacity);
        var writer = new RotatingFileWriter(directory);

        var factory = LoggerFactory.Create(builder =>
        {
            // Filterung übernehmen die Provider selbst; MEL soll nichts zusätzlich wegfiltern.
            builder.SetMinimumLevel(LogLevel.Trace);
            // Der Puffer nimmt alles auf, damit der Level-Filter der Log-Seite ein reiner
            // Anzeige-Filter ist (Spec S1.6 E3); der umschaltbare LevelSwitch gilt der Datei.
            builder.AddProvider(new RingBufferLoggerProvider(
                ringBuffer, new LogLevelSwitch(LogLevel.Trace), clock));
            builder.AddProvider(new FileLoggerProvider(writer, levelSwitch, clock));
        });

        return new AnvilLogging(
            factory, ringBuffer, levelSwitch, directory, writer.CurrentFilePath);
    }

    public void Dispose() => Factory.Dispose();
}
