using Microsoft.Extensions.Logging;

namespace Anvil.Core.Autostart;

/// <summary>
/// Die Autostart-Datei als Spiegel des Schalters (Spec S3.6 §3.2, E-D): Schalter an → die
/// Datei steht mit dem aktuellen Programmpfad, Schalter aus → es gibt sie nicht. Gerufen bei
/// jedem Start (Entscheidung 6: der Pfad erneuert sich) und bei jedem Umschalten. IO-Fehler
/// werden geloggt, nie geworfen (E-E): Der Schalter ist gespeichert, der Eintrag ist best
/// effort. Kein Interface, kein Fake-Dateisystem (E-F) — getestet gegen ein Temp-Verzeichnis.
/// </summary>
public sealed class AutostartFile
{
    private readonly string _filePath;
    private readonly string _exec;
    private readonly ILogger _logger;

    public AutostartFile(string filePath, string exec, ILogger logger)
    {
        _filePath = filePath;
        _exec = exec;
        _logger = logger;
    }

    public void Sync(bool startOnBoot)
    {
        if (startOnBoot)
        {
            Write();
        }
        else
        {
            Remove();
        }
    }

    private void Write()
    {
        try
        {
            AtomicFile.WriteAllText(_filePath, AutostartEntry.Render(_exec));
            _logger.LogInformation("autostart entry written: {Path}", _filePath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(exception, "autostart entry could not be written: {Path}", _filePath);
        }
    }

    private void Remove()
    {
        try
        {
            // Erst nachsehen: File.Delete schweigt bei fehlender Datei, und eine Zeile
            // „removed" ohne Datei waere eine Luege.
            if (!File.Exists(_filePath))
            {
                return;
            }

            File.Delete(_filePath);
            _logger.LogInformation("autostart entry removed: {Path}", _filePath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(exception, "autostart entry could not be removed: {Path}", _filePath);
        }
    }
}
