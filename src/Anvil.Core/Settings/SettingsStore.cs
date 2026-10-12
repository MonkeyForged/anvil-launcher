using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Anvil.Core.Settings;

/// <summary>
/// Lädt und speichert <see cref="AppSettings"/> als lesbares JSON. Laden ist fehlertolerant
/// (Vorgabewerte statt Absturz; die defekte Datei wird vorher als .invalid gesichert),
/// Speichern unteilbar über <see cref="AtomicFile"/>. Den Pfad liefert die Composition Root
/// (S1.4) über <see cref="XdgPaths.SettingsFilePath"/>.
/// </summary>
public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private readonly ILogger? _logger;

    public SettingsStore(string filePath, ILogger? logger = null)
    {
        FilePath = filePath;
        _logger = logger;
    }

    public string FilePath { get; }

    public AppSettings Load()
    {
        if (!File.Exists(FilePath))
        {
            return new AppSettings();
        }

        try
        {
            var settings = JsonSerializer.Deserialize<AppSettings>(
                File.ReadAllText(FilePath), JsonOptions);
            return settings ?? new AppSettings();
        }
        catch (Exception exception) when (exception is JsonException or IOException)
        {
            BackUpBrokenFile();
            _logger?.LogWarning(
                exception, "settings file unreadable, using defaults: {Path}", FilePath);
            return new AppSettings();
        }
    }

    public void Save(AppSettings settings) =>
        AtomicFile.WriteAllText(FilePath, JsonSerializer.Serialize(settings, JsonOptions));

    private void BackUpBrokenFile()
    {
        try
        {
            File.Copy(FilePath, FilePath + ".invalid", overwrite: true);
        }
        catch (IOException)
        {
            // Best effort — schlimmstenfalls fehlt die Sicherung, der Start geht weiter.
        }
    }
}
