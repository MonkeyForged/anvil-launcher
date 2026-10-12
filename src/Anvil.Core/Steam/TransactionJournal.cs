using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Anvil.Core.Steam;

/// <summary>Eine Datei, die die Transaktion angefasst hat, und wo ihre Sicherung liegt.</summary>
public sealed record TransactionFile(string Path, string BackupPath);

/// <summary>
/// Ein offener Schreibvorgang (Spec S4.4 §3.1). <b>Kein Phasenfeld</b> (E-A): Ohne Resume ist
/// die einzige Frage an den Vermerk „liegen hier Sicherungen?", und die beantwortet
/// <see cref="Files"/> selbst.
/// </summary>
public sealed record TransactionRecord(
    DateTimeOffset StartedAt, IReadOnlyList<TransactionFile> Files);

/// <summary>
/// Der Vermerk auf der Platte (Spec S4.4 §3.1): lesbares JSON, unteilbar geschrieben, nach dem
/// Muster von <c>SettingsStore</c>. Kennt nur Pfade — nichts von Steam, VDF oder Modulen.
///
/// <para>
/// <b>Warum <see cref="Add"/> sofort schreibt:</b> Das ist die ganze Daseinsberechtigung der
/// Klasse. Der Aufruf kehrt erst zurueck, wenn der Sicherungspfad auf der Platte steht — erst
/// danach faellt im Schreiber das erste Byte (Projektplan M4, Korrektur a). Der Vorgaenger
/// schrieb die Pfade nach dem Schreiben und behauptete nach einem Absturz „nichts angefasst".
/// </para>
/// </summary>
public sealed class TransactionJournal
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private readonly ILogger? _logger;
    private readonly TimeProvider _time;

    public TransactionJournal(string filePath, ILogger? logger = null, TimeProvider? time = null)
    {
        FilePath = filePath;
        _logger = logger;
        _time = time ?? TimeProvider.System;
    }

    public string FilePath { get; }

    /// <summary>
    /// Der offene Vorgang, oder <c>null</c>. <b>Wirft nie und aendert nichts</b>: Eine fehlende
    /// Datei ist kein Vorgang, eine unlesbare auch nicht (Projektplan M4: „kaputter Vermerk =
    /// kein Vorgang"). Die kaputte Datei bleibt liegen — eine .invalid-Kopie waere hier nur
    /// Muell, und der naechste <see cref="Begin"/> raeumt sie ohnehin weg.
    /// </summary>
    public TransactionRecord? Read()
    {
        if (!File.Exists(FilePath))
        {
            return null;
        }

        try
        {
            var record = JsonSerializer.Deserialize<TransactionRecord>(
                File.ReadAllText(FilePath), JsonOptions);

            // Ein JSON ohne „files" ist gueltig und ergaebe eine null-Liste — die will hier
            // niemand sehen.
            return record is null ? null : record with { Files = record.Files ?? [] };
        }
        catch (Exception exception)
            when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            _logger?.LogWarning(
                exception,
                "journal unreadable — treated as no pending transaction: {Path}", FilePath);
            return null;
        }
    }

    /// <summary>
    /// Legt einen Vermerk ohne Dateien an: „Anvil ist dabei, es wurde noch nichts angefasst."
    /// Ein Wurf (Platte voll, Rechte) gehoert dem Aufrufer — wer keinen Vermerk anlegen kann,
    /// darf nicht anfangen.
    /// </summary>
    public void Begin()
    {
        Save(new TransactionRecord(_time.GetUtcNow(), []));
        _logger?.LogInformation("journal opened: {Path}", FilePath);
    }

    /// <summary>
    /// Haengt eine Datei samt Sicherungspfad an und schreibt <b>sofort</b>. Siehe den
    /// Klassenkommentar: Danach — und nur danach — darf geschrieben werden.
    /// </summary>
    public void Add(string path, string backupPath)
    {
        var record = Read() ?? new TransactionRecord(_time.GetUtcNow(), []);
        Save(record with { Files = [.. record.Files, new TransactionFile(path, backupPath)] });
        _logger?.LogInformation("journal notes {Backup} for {File}", backupPath, path);
    }

    /// <summary>Schliesst den Vorgang. Ohne Datei still — es gibt nichts zu schliessen.</summary>
    public void Clear()
    {
        if (!File.Exists(FilePath))
        {
            return;
        }

        File.Delete(FilePath);
        _logger?.LogInformation("journal cleared: {Path}", FilePath);
    }

    private void Save(TransactionRecord record)
    {
        if (Path.GetDirectoryName(FilePath) is { Length: > 0 } directory)
        {
            Directory.CreateDirectory(directory);
        }

        AtomicFile.WriteAllText(FilePath, JsonSerializer.Serialize(record, JsonOptions));
    }
}
