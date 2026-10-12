using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace Anvil.Core.Backups;

/// <summary>
/// Sichert Dateien Byte fuer Byte in einen Ordner (Spec S4.1). Der Dienst kennt nur Dateien —
/// nichts von Steam, VDF oder Modulen. Der Name traegt einen sortierbaren Zeitstempel in
/// Ortszeit, weil ein Mensch diesen Ordner liest, wenn etwas schiefging; Reihenfolge und
/// Aufraeumen lesen den Namen, nie mtime (E-B). Kein Interface, kein Fake-Dateisystem —
/// getestet gegen ein Temp-Verzeichnis, wie AutostartFile.
/// </summary>
public sealed partial class BackupStore
{
    /// <summary>Fest im Code (Projektplan M4, Entscheidung 3): nach jedem Sichern bleiben je Dateiname so viele.</summary>
    public const int KeepPerFileName = 20;

    private const string StampFormat = "yyyy-MM-dd_HH-mm-ss";

    /// <summary>Die Endung der Seitendatei — <c>&lt;sicherung&gt;.json</c> (Spec S4.8 §3.2).</summary>
    private const string NoteSuffix = ".json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private readonly ILogger _logger;
    private readonly TimeProvider _time;

    public BackupStore(string directoryPath, ILogger logger, TimeProvider? time = null)
    {
        DirectoryPath = directoryPath;
        _logger = logger;
        _time = time ?? TimeProvider.System;
    }

    public string DirectoryPath { get; }

    /// <summary>
    /// Kopiert die Datei in den Ordner, legt den Vermerk daneben, raeumt danach fuer diesen
    /// Dateinamen auf und liefert den Pfad der Sicherung. Kopierfehler werfen — der Aufrufer
    /// entscheidet.
    /// </summary>
    /// <param name="note">
    /// Was gleich geschrieben wird (S4.8 §3.2), oder <c>null</c>. Ein <b>Fehlschlag beim Vermerk
    /// wirft nicht</b>: Das Versprechen dieser Methode ist die Kopie, und eine Kopie ohne Zettel
    /// ist ein kleinerer Schaden als ein Vorgang, der wegen eines Zettels abbricht.
    /// </param>
    public string Create(string sourcePath, BackupNote? note = null)
    {
        Directory.CreateDirectory(DirectoryPath);
        var name = Path.GetFileName(sourcePath);
        var stamp = _time.GetLocalNow().ToString(StampFormat, CultureInfo.InvariantCulture);

        var target = Path.Combine(DirectoryPath, $"{stamp}-{name}");
        for (var counter = 2; File.Exists(target); counter++)
        {
            target = Path.Combine(DirectoryPath, $"{stamp}-{counter}-{name}");
        }

        File.Copy(sourcePath, target);
        _logger.LogInformation("backup created: {Path}", target);
        WriteNote(target, note);
        Prune(name);
        return target;
    }

    /// <summary>
    /// Alle Sicherungen, neueste zuerst. Fremde Dateien ohne unser Namensmuster werden
    /// uebergangen; ohne Ordner ist die Liste leer. Keine Log-Zeile — eine Abfrage ist kein
    /// Vorgang (E-E).
    /// </summary>
    public IReadOnlyList<BackupEntry> List()
    {
        if (!Directory.Exists(DirectoryPath))
        {
            return [];
        }

        return Directory.EnumerateFiles(DirectoryPath)
            .Select(TryParse)
            .OfType<BackupEntry>()
            .OrderByDescending(entry => entry.Timestamp)
            .ThenByDescending(entry => entry.Counter)
            .ToList();
    }

    /// <summary>
    /// Spielt die Sicherung unteilbar an den Zielpfad zurueck (AtomicFile, E-F) und laesst sie
    /// liegen. Eine fehlende Sicherung ist ein Fehlschlag mit Klartext, kein Achselzucken (E-C):
    /// Stillschweigend zu ueberspringen hiesse, einen halben Zustand als hergestellt zu melden.
    /// </summary>
    public void Restore(string backupPath, string targetPath)
    {
        if (!File.Exists(backupPath))
        {
            throw new FileNotFoundException(
                $"backup is gone and cannot be restored: {backupPath}", backupPath);
        }

        AtomicFile.WriteAllBytes(targetPath, File.ReadAllBytes(backupPath));
        _logger.LogInformation("backup restored: {Backup} -> {Target}", backupPath, targetPath);
    }

    /// <summary>
    /// Loescht eine Sicherung. Der Pfad muss direkt im Ordner liegen — eine fremde Datei ist
    /// hier nie loeschbar; wer eine reicht, hat einen Fehler im Code, und der soll knallen
    /// (E-C). Eine schon fehlende Sicherung ist kein Fehler (E-D): nachsehen, warnen, zurueck —
    /// „deleted" ohne Datei waere eine Luege.
    /// </summary>
    public void Delete(string backupPath)
    {
        var fullPath = Path.GetFullPath(backupPath);
        var parent = Path.GetDirectoryName(fullPath);
        var directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(DirectoryPath));
        if (!string.Equals(parent, directory, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"not a backup in {DirectoryPath}: {backupPath}", nameof(backupPath));
        }

        if (!File.Exists(fullPath))
        {
            _logger.LogWarning("backup already gone: {Path}", fullPath);
            return;
        }

        File.Delete(fullPath);
        DeleteNote(fullPath);
        _logger.LogInformation("backup deleted: {Path}", fullPath);
    }

    /// <summary>
    /// Laesst die juengsten Sicherungen dieses Dateinamens stehen und loescht den Rest. Andere
    /// Dateinamen bleiben unberuehrt. Best effort: Eine gebliebene Sicherung schadet nicht, und
    /// Create hat sein Versprechen schon gehalten.
    /// </summary>
    private void Prune(string fileName)
    {
        var outdated = List()
            .Where(entry => entry.FileName == fileName)
            .Skip(KeepPerFileName);

        foreach (var entry in outdated)
        {
            try
            {
                File.Delete(entry.Path);
                DeleteNote(entry.Path);
                _logger.LogInformation("backup pruned: {Path}", entry.Path);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(exception, "backup could not be pruned: {Path}", entry.Path);
            }
        }
    }

    private void WriteNote(string backupPath, BackupNote? note)
    {
        if (note is null)
        {
            return;
        }

        var path = backupPath + NoteSuffix;
        try
        {
            AtomicFile.WriteAllText(path, JsonSerializer.Serialize(note, JsonOptions));
            _logger.LogInformation("backup note written: {Path}", path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(exception, "backup note could not be written: {Path}", path);
        }
    }

    /// <summary>
    /// Der Vermerk zur Sicherung, oder <c>null</c>. <b>Wirft nie</b>: Eine fehlende Seitendatei
    /// ist der Normalfall (E-G), eine kaputte wird wie keine behandelt — dieselbe Regel wie beim
    /// Vermerk der Transaktion („kaputt = nicht da").
    /// </summary>
    private BackupNote? ReadNote(string backupPath)
    {
        var path = backupPath + NoteSuffix;
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var note = JsonSerializer.Deserialize<BackupNote>(File.ReadAllText(path), JsonOptions);

            // Ein JSON ohne „games" ist gueltig und ergaebe eine null-Liste.
            return note is null ? null : note with { Games = note.Games ?? [] };
        }
        catch (Exception exception)
            when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(
                exception, "backup note unreadable — treated as none: {Path}", path);
            return null;
        }
    }

    /// <summary>Nimmt den Zettel mit. Eine fehlende Seitendatei ist kein Fehler.</summary>
    private void DeleteNote(string backupPath)
    {
        var path = backupPath + NoteSuffix;
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(exception, "backup note could not be deleted: {Path}", path);
        }
    }

    // <stamp>-<name> oder <stamp>-<zaehler>-<name>. Grenze (E-B): Ein Original, dessen Name
    // mit „<Zahl>-" beginnt, wird als Zaehler gelesen — Steam-Dateien heissen so nicht.
    [GeneratedRegex(@"^(\d{4}-\d{2}-\d{2}_\d{2}-\d{2}-\d{2})-(?:(\d+)-)?(.+)$")]
    private static partial Regex NamePattern();

    private BackupEntry? TryParse(string path)
    {
        // Die Seitendatei einer Sicherung ist keine Sicherung (E-H): Ohne diese Regel liest das
        // Namensmuster sie als Kopie einer Datei namens „config.vdf.json" — und die Liste zeigte
        // jede Sicherung doppelt. Anvil sichert nur config.vdf und localconfig.vdf.
        if (path.EndsWith(NoteSuffix, StringComparison.Ordinal))
        {
            return null;
        }

        var match = NamePattern().Match(Path.GetFileName(path));
        if (!match.Success)
        {
            return null;
        }

        if (!DateTime.TryParseExact(
                match.Groups[1].Value, StampFormat, CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var timestamp))
        {
            return null;
        }

        var counter = 1;
        if (match.Groups[2].Success
            && !int.TryParse(match.Groups[2].Value, NumberStyles.None,
                CultureInfo.InvariantCulture, out counter))
        {
            return null;
        }

        return new BackupEntry(
            timestamp, counter, match.Groups[3].Value, path, new FileInfo(path).Length,
            ReadNote(path));
    }
}
