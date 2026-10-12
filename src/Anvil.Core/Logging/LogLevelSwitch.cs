using Microsoft.Extensions.Logging;

namespace Anvil.Core.Logging;

/// <summary>Zur Laufzeit umschaltbares Mindest-Level; von beiden Providern gemeinsam genutzt.</summary>
public sealed class LogLevelSwitch
{
    private volatile LogLevel _minimumLevel;

    public LogLevelSwitch(LogLevel initial = LogLevel.Information)
    {
        _minimumLevel = initial;
    }

    public LogLevel MinimumLevel
    {
        get => _minimumLevel;
        set => _minimumLevel = value;
    }

    public bool IsEnabled(LogLevel level)
        => level != LogLevel.None && level >= _minimumLevel;
}
