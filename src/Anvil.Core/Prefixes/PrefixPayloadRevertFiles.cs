using System.Security.Cryptography;

namespace Anvil.Core.Prefixes;

/// <summary>Was der Rückbau der Dateien hinterlassen hat — gezählt, nicht geschätzt.</summary>
/// <param name="Restored">Einträge, deren Vorzustand wiederhergestellt wurde.</param>
/// <param name="LeftStanding">Einträge, die stehen blieben — Protons Werk oder schon zurückgebaut.</param>
/// <param name="LinksRestored">Wiederhergestellte Symlinks, Dateien wie Verzeichnisebenen.</param>
/// <param name="DanglingLinks">Davon die, deren Ziel es nicht mehr gibt (Spec S4.2 E-3).</param>
/// <param name="DirectoriesRemoved">Verzeichnisebenen, die vorher fehlten und jetzt wieder fehlen.</param>
public sealed record RevertFilesResult(
    int Restored, int LeftStanding, int LinksRestored, int DanglingLinks, int DirectoriesRemoved);

/// <summary>
/// Stellt den Vorzustand der Dateien einer Nutzlast wieder her — das Gegenstück zu
/// <see cref="PrefixPayloadFiles"/>, aber <b>nicht spiegelbildlich</b> (Spec S4.2 §3.3).
///
/// <para>
/// <b>Die Regel, die alles trägt</b> (Versuch D, <c>messungen-anhaengen.md</c> §8): Zurückgebaut wird nur,
/// was <b>noch die Prüfsumme aus dem Protokoll trägt</b>. Was Proton seither selbst gesetzt hat,
/// bleibt stehen — sonst zeigten Symlinks nach dem Rückbau auf Dateien der alten Proton-Fassung,
/// die dort nicht einmal am selben Ort liegen.
/// </para>
///
/// <para>
/// <b>Kein Zurückrollen, dafür wiederholbar</b> (dieselbe Linie wie <see cref="PrefixPayloadFiles"/>,
/// S3.6 E-7): Bricht der Lauf ab, trägt das schon Zurückgesetzte die Prüfsumme nicht mehr und wird
/// beim nächsten Lauf in Ruhe gelassen.
/// </para>
/// </summary>
public static class PrefixPayloadRevertFiles
{
    /// <param name="prefixPath">Der <c>pfx</c>-Ordner.</param>
    /// <param name="componentKey">Bestimmt den Sicherungsordner (<see cref="PrefixPayloadBackup"/>).</param>
    /// <param name="rollback">Ein Protokoll in Stufe <see cref="RollbackStage.Applied"/> — sonst fehlen die Prüfsummen.</param>
    /// <param name="warnings">Nimmt entgegen, was auffiel, ohne den Lauf zu beenden — wie <see cref="PrefixMarkProbe"/>.</param>
    /// <param name="cancellationToken">Geprüft zwischen Einträgen, nie mitten in einem.</param>
    public static RevertFilesResult Revert(
        string prefixPath,
        string componentKey,
        PayloadRollback rollback,
        ICollection<string> warnings,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prefixPath);
        ArgumentNullException.ThrowIfNull(rollback);
        ArgumentNullException.ThrowIfNull(warnings);

        var driveC = Path.Combine(prefixPath, "drive_c");
        var backupFiles = PrefixPayloadBackup.FilesPathFor(prefixPath, componentKey);

        var restored = 0;
        var leftStanding = 0;
        var linksRestored = 0;
        var danglingLinks = 0;
        var directoriesRemoved = 0;

        // 1. Die Dateien, in Protokollreihenfolge (Eltern vor Kindern).
        foreach (var entry in rollback.Entries)
        {
            if (entry.AppliedSha256 is not { } sha)
            {
                continue;
            }

            cancellationToken.ThrowIfCancellationRequested();

            var fullPath = FullPath(driveC, entry.Path);
            var info = new FileInfo(fullPath);

            // Ein Symlink oder gar nichts heißt: Dort stammt nichts mehr vom Paket. Exists ist
            // bei einem Verzeichnis false — auch das ist nicht mehr unsere Datei.
            if (info.LinkTarget is not null || !info.Exists
                || !Sha256Of(fullPath).Equals(sha, StringComparison.OrdinalIgnoreCase))
            {
                leftStanding++;
                continue;
            }

            switch (entry.Prior)
            {
                case PriorKind.Missing:
                    File.Delete(fullPath);
                    restored++;
                    break;

                case PriorKind.File:
                    var backupPath = FullPath(backupFiles, entry.Path);
                    if (!File.Exists(backupPath))
                    {
                        // Ohne Sicherung gibt es nichts herzustellen. Löschen wäre schlimmer als
                        // stehen lassen: Der Vorzustand war eine Datei, keine Lücke.
                        warnings.Add($"backup missing, file left as it is: {entry.Path}");
                        leftStanding++;
                        break;
                    }

                    File.Copy(backupPath, fullPath, overwrite: true);
                    restored++;
                    break;

                case PriorKind.Symlink:
                    File.Delete(fullPath);
                    File.CreateSymbolicLink(fullPath, entry.PriorTarget!);
                    restored++;
                    linksRestored++;
                    if (!TargetExists(fullPath, entry.PriorTarget!))
                    {
                        danglingLinks++;
                    }

                    break;

                default:
                    // Ein Verzeichnis als Vorzustand einer Datei kann das Anwenden nicht erzeugt
                    // haben (PrefixPayloadFiles.RemoveTarget scheiterte daran).
                    warnings.Add($"prior state of a file was a directory, left as it is: {entry.Path}");
                    leftStanding++;
                    break;
            }
        }

        // 2. Die Verzeichnisebenen, von innen nach außen — ein Kind muss weg sein, bevor sein
        //    Elternteil leer sein kann.
        for (var index = rollback.Entries.Count - 1; index >= 0; index--)
        {
            var entry = rollback.Entries[index];
            if (entry.AppliedSha256 is not null || entry.Prior == PriorKind.Directory)
            {
                continue;
            }

            cancellationToken.ThrowIfCancellationRequested();

            var fullPath = FullPath(driveC, entry.Path);
            if (!Directory.Exists(fullPath) || new FileInfo(fullPath).LinkTarget is not null
                || Directory.EnumerateFileSystemEntries(fullPath).Any())
            {
                continue;
            }

            if (entry.Prior == PriorKind.Missing)
            {
                Directory.Delete(fullPath);
                directoriesRemoved++;
            }
            else if (entry.Prior == PriorKind.Symlink)
            {
                Directory.Delete(fullPath);
                File.CreateSymbolicLink(fullPath, entry.PriorTarget!);
                linksRestored++;
                if (!TargetExists(fullPath, entry.PriorTarget!))
                {
                    danglingLinks++;
                }
            }
            else
            {
                warnings.Add($"prior state of a directory level was a file, left as it is: {entry.Path}");
            }
        }

        return new RevertFilesResult(restored, leftStanding, linksRestored, danglingLinks, directoriesRemoved);
    }

    private static string FullPath(string root, string relativePath)
        => Path.Combine([root, .. relativePath.Split('/')]);

    private static string Sha256Of(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    // Ein relatives Ziel steht relativ zum Ordner des Links; Path.Combine gibt ein absolutes
    // Ziel unverändert zurück. Gefragt wird nur fürs Zählen — wiederhergestellt wird ohnehin (E-3).
    private static bool TargetExists(string linkPath, string target)
        => Path.Exists(Path.Combine(Path.GetDirectoryName(linkPath)!, target));
}
