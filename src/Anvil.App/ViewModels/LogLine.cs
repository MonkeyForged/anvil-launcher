using System.Globalization;
using Anvil.Core.Logging;
using Microsoft.Extensions.Logging;

namespace Anvil.App.ViewModels;

/// <summary>
/// One line of the log page: finished strings and a brush key per level, so the view computes
/// nothing (spec S1.6 §3.3). The core's <see cref="LogEntry"/> stays untouched and is reachable
/// as <see cref="Source"/>.
/// </summary>
public sealed class LogLine
{
    public LogLine(LogEntry source)
    {
        Source = source;
        Time = source.Timestamp.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture);
        LevelLabel = LabelOf(source.Level);
        LevelBrushKey = BrushKeyOf(source.Level);
        IsError = source.Level >= LogLevel.Error;
    }

    public LogEntry Source { get; }

    public string Time { get; }

    public string LevelLabel { get; }

    /// <summary>The key of a colour token brush of the design package (spec s13.1, Log).</summary>
    public string LevelBrushKey { get; }

    public string Category => Source.Category;

    public string Message => Source.Message;

    /// <summary>Exception text for the tooltip (spec E6); null when no exception came with it.</summary>
    public string? Details => Source.ExceptionText;

    public bool IsError { get; }

    public static string LabelOf(LogLevel level) => level switch
    {
        LogLevel.Trace => "Trace",
        LogLevel.Debug => "Debug",
        LogLevel.Information => "Info",
        LogLevel.Warning => "Warning",
        LogLevel.Error => "Error",
        LogLevel.Critical => "Critical",
        _ => "None",
    };

    private static string BrushKeyOf(LogLevel level) => level switch
    {
        LogLevel.Trace => "InkFaintBrush",
        LogLevel.Debug => "InkMutedBrush",
        LogLevel.Information => "InkBrush",
        LogLevel.Warning => "WarningBrush",
        _ => "DangerBrush",
    };
}
