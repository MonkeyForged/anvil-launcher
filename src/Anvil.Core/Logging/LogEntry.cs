using Microsoft.Extensions.Logging;

namespace Anvil.Core.Logging;

/// <summary>Ein unveränderlicher Log-Eintrag — die gemeinsame Währung von Datei-Sink und Ringpuffer.</summary>
public sealed record LogEntry(
    DateTimeOffset Timestamp,
    LogLevel Level,
    string Category,
    string Message,
    string? ExceptionText);
