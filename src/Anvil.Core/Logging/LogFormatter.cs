using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Anvil.Core.Logging;

/// <summary>Formatiert Einträge für die Log-Datei; Level-Token englisch (Sprachregel CLAUDE.md).</summary>
public static class LogFormatter
{
    public static string LevelToken(LogLevel level) => level switch
    {
        LogLevel.Trace => "TRACE",
        LogLevel.Debug => "DEBUG",
        LogLevel.Information => "INFO ",
        LogLevel.Warning => "WARN ",
        LogLevel.Error => "ERROR",
        LogLevel.Critical => "CRIT ",
        _ => "NONE ",
    };

    public static string Format(LogEntry entry)
    {
        var builder = new StringBuilder();
        builder.Append(entry.Timestamp.ToString(
            "yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture));
        builder.Append(" [").Append(LevelToken(entry.Level)).Append("] [");
        builder.Append(entry.Category).Append("] ").Append(entry.Message);

        if (entry.ExceptionText is not null)
        {
            foreach (var line in entry.ExceptionText.Split('\n'))
            {
                builder.Append('\n').Append("    ").Append(line.TrimEnd('\r'));
            }
        }

        return builder.ToString();
    }
}
