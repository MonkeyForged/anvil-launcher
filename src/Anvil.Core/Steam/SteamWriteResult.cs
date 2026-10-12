namespace Anvil.Core.Steam;

/// <summary>Was mit einer der zwei Dateien geschah (Spec S4.3 §4.6).</summary>
public enum SteamFileOutcome
{
    /// <summary>Kein Wunsch für die Datei, oder der Text blieb gleich — kein Byte angefasst.</summary>
    Unchanged,

    /// <summary>Gesichert, atomar geschrieben, neu gelesen und geprüft.</summary>
    Written,

    /// <summary>
    /// Fehlschlag mit Klartext. Steht <see cref="SteamFileResult.BackupPath"/>, war die
    /// Sicherung schon angelegt — und die Datei womöglich geschrieben (Prüfung fehlgeschlagen).
    /// </summary>
    Failed,

    /// <summary>Nicht versucht, weil die Datei davor scheiterte.</summary>
    Skipped,
}

/// <param name="Path">Der volle Pfad; ohne Steam-Wurzel nur der Dateiname.</param>
/// <param name="BackupPath">Der Pfad der Sicherung, sobald der Delegat gerufen wurde.</param>
/// <param name="Error">Klartext bei <see cref="SteamFileOutcome.Failed"/>, sonst <c>null</c>.</param>
public sealed record SteamFileResult(
    string Path, SteamFileOutcome Outcome, string? BackupPath, string? Error)
{
    public string FileName => System.IO.Path.GetFileName(Path);
}

/// <summary>Das Ergebnis eines <see cref="SteamConfigWriter.Apply"/>: beide Dateien, immer.</summary>
public sealed record SteamWriteResult(SteamFileResult Config, SteamFileResult LocalConfig)
{
    /// <summary>Keine der zwei Dateien ist gescheitert.</summary>
    public bool Succeeded
        => Config.Outcome != SteamFileOutcome.Failed
            && LocalConfig.Outcome != SteamFileOutcome.Failed;

    /// <summary>Die tatsächlich geschriebenen Dateien — was S4.4 ins Journal stellt.</summary>
    public IEnumerable<SteamFileResult> Written
        => new[] { Config, LocalConfig }.Where(file => file.Outcome == SteamFileOutcome.Written);
}
