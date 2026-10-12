namespace Anvil.Core.Backups;

/// <summary>
/// Eine Sicherung, gelesen aus ihrem Dateinamen (Spec S4.1 E-A): Zeitpunkt in Ortszeit ohne
/// Zone, Zaehler bei Kollision in derselben Sekunde (1 ohne Zusatz), der Name der gesicherten
/// Datei. Die Datei-Zeit taugt nicht — File.Copy uebernimmt das Aenderungsdatum des Originals.
/// </summary>
/// <param name="Note">
/// Der Vermerk aus der Seitendatei, oder <c>null</c> (S4.8 §3.2). <b>Letzter Parameter mit
/// Vorgabe</b>, damit vorhandene Aufrufe und Tests unverändert bleiben — und weil „kein Vermerk"
/// der Normalfall für alles ist, was vor S4.8 entstand (E-G).
/// </param>
public sealed record BackupEntry(
    DateTime Timestamp,
    int Counter,
    string FileName,
    string Path,
    long Size,
    BackupNote? Note = null)
{
    public string BackupFileName => System.IO.Path.GetFileName(Path);
}
