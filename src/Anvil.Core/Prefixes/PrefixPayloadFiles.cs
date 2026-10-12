using System.Security.Cryptography;

namespace Anvil.Core.Prefixes;

/// <summary>Was ein Paket an Dateien mitbringt — gesammelt, bevor irgendetwas angefasst wird.</summary>
/// <param name="RelativePaths">Relativ zu <c>files/</c> = relativ zu <c>drive_c/</c>, mit <c>/</c>, ordinal sortiert.</param>
/// <param name="TotalBytes">Summe der Größen, gezählt.</param>
/// <param name="SkippedLinks">Symlinks im Paket, die nicht angewandt werden.</param>
public sealed record PayloadFilePlan(IReadOnlyList<string> RelativePaths, long TotalBytes, int SkippedLinks);

/// <summary>Was das Kopieren hinterlassen hat — gezählt, nicht geschätzt.</summary>
/// <param name="FileCount">Kopierte Dateien.</param>
/// <param name="TotalBytes">Kopierte Bytes.</param>
/// <param name="ReplacedLinks">Symlinks im Prefix, die dabei durch echte Einträge ersetzt wurden.</param>
/// <param name="Sha256ByPath">
/// Je kopierter Datei die SHA-256 dessen, was jetzt dort liegt (Spec S4.1 §3.6). <b>Hier gerechnet
/// und nirgends sonst</b>: Diese Stelle weiß als einzige, was geschrieben wurde; ein zweiter
/// Durchgang müsste es erneut herausfinden und könnte dabei etwas anderes finden.
/// </param>
public sealed record PayloadFilesResult(
    int FileCount, long TotalBytes, int ReplacedLinks, IReadOnlyDictionary<string, string> Sha256ByPath);

/// <summary>
/// Schreibt den <c>files/</c>-Baum eines Pakets in <c>drive_c/</c> eines Prefixes — <b>ohne je
/// durch einen Symlink zu schreiben</b> (Spec S3.6 §3.4). Das Gegenstück zu
/// <see cref="PrefixHarvest"/>, aber <b>nicht spiegelbildlich</b>: Die Ernte überspringt Links,
/// das Anwenden muss sie ersetzen.
///
/// <para>
/// <b>Warum das die ganze Story trägt:</b> Ein Proton-Prefix verlinkt an vielen Stellen in Protons
/// <b>geteilte</b> Installation (in Blasphemous 2 gemessen: <c>mscoree.dll</c> in beiden
/// Bitbreiten). <c>File.Copy(…, overwrite: true)</c> auf einen solchen Link schreibt hindurch und
/// verändert Protons Datei — für jedes Spiel, dauerhaft. Deshalb je Datei: Verzeichnisse auf dem
/// Weg zu echten Verzeichnissen machen, das Ziel entfernen (<c>--remove-destination</c>), dann
/// kopieren. Der Vorgänger hat genau das live abgenommen; übernommen ist das Wissen, nicht der Code.
/// </para>
///
/// <para>
/// <b>Kein Zurückrollen</b> (E-7): Abbruch wird zwischen Dateien geprüft; was schon kopiert ist,
/// bleibt. Ein zweiter Lauf ersetzt dieselben Dateien noch einmal und ist unschädlich.
/// </para>
/// </summary>
public static class PrefixPayloadFiles
{
    /// <summary>Sammelt die Dateien des Pakets. Symlinks im Paket werden gezählt, nie angewandt.</summary>
    public static PayloadFilePlan Plan(string packageFilesPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageFilesPath);

        var relativePaths = new List<string>();
        long totalBytes = 0;
        var skippedLinks = 0;

        Collect(new DirectoryInfo(packageFilesPath));
        relativePaths.Sort(StringComparer.Ordinal);
        return new PayloadFilePlan(relativePaths, totalBytes, skippedLinks);

        void Collect(DirectoryInfo directory)
        {
            foreach (var entry in directory.EnumerateFileSystemInfos())
            {
                // Die Ernte legt keine Links an; käme doch einer vorbei, könnte er überallhin
                // zeigen — also nie anwenden.
                if (entry.LinkTarget is not null)
                {
                    skippedLinks++;
                    continue;
                }

                if (entry is DirectoryInfo child)
                {
                    Collect(child);
                }
                else if (entry is FileInfo file)
                {
                    totalBytes += file.Length;
                    relativePaths.Add(
                        Path.GetRelativePath(packageFilesPath, file.FullName).Replace(Path.DirectorySeparatorChar, '/'));
                }
            }
        }
    }

    /// <summary>
    /// <c>true</c>, wenn das Paket etwas unter <c>users/</c> mitbringt — dort liegen Spielstände
    /// (Spec S3.6 §3.3, Schritt 4). Nur das erste Segment zählt: <c>windows/users.dll</c> ist harmlos.
    /// </summary>
    public static bool TouchesUsers(PayloadFilePlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);

        return plan.RelativePaths.Any(relativePath =>
            relativePath.Split('/')[0].Equals("users", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Kopiert die Dateien aus <paramref name="plan"/> nach <c>&lt;prefixPath&gt;/drive_c/</c>.
    /// <b>Prüft nicht</b>, ob der Plan <c>users/</c> berührt — das ist Sache des Aufrufers, vor dem
    /// ersten Byte (<see cref="TouchesUsers"/>).
    /// </summary>
    public static PayloadFilesResult Apply(
        string packageFilesPath, string prefixPath, PayloadFilePlan plan, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageFilesPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(prefixPath);
        ArgumentNullException.ThrowIfNull(plan);

        var driveC = Path.Combine(prefixPath, "drive_c");
        var fileCount = 0;
        long totalBytes = 0;
        var replacedLinks = 0;
        var sha256ByPath = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var relativePath in plan.RelativePaths)
        {
            // Zwischen Dateien, nie mitten in einer: eine halbe DLL wäre schlimmer als eine
            // Sekunde Verzögerung.
            cancellationToken.ThrowIfCancellationRequested();

            var parts = relativePath.Split('/');
            var source = Path.Combine([packageFilesPath, .. parts]);
            var target = Path.Combine([driveC, .. parts]);

            replacedLinks += EnsureRealDirectories(driveC, parts[..^1]);
            replacedLinks += RemoveTarget(target);
            File.Copy(source, target);

            fileCount++;
            totalBytes += new FileInfo(target).Length;
            sha256ByPath[relativePath] = Sha256Of(target);
        }

        return new PayloadFilesResult(fileCount, totalBytes, replacedLinks, sha256ByPath);
    }

    // Vom Ziel gelesen, nicht von der Quelle: Aufgeschrieben wird, was im Prefix liegt.
    private static string Sha256Of(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    // Jede Ebene unterhalb von drive_c/: Ist sie ein Symlink, wird der Link entfernt und ein
    // echtes Verzeichnis angelegt — sonst landete die Kopie in Protons geteiltem Baum. Oberhalb
    // von drive_c/ wird nichts ersetzt; dort liegt kein Paketinhalt.
    private static int EnsureRealDirectories(string driveC, string[] directoryParts)
    {
        var replaced = 0;
        var current = driveC;
        foreach (var part in directoryParts)
        {
            current = Path.Combine(current, part);
            if (IsSymlink(current))
            {
                Unlink(current);
                replaced++;
            }

            Directory.CreateDirectory(current);
        }

        return replaced;
    }

    // Der eigentliche --remove-destination-Schritt. 1, wenn dabei ein Link verschwand.
    private static int RemoveTarget(string target)
    {
        if (IsSymlink(target))
        {
            Unlink(target);
            return 1;
        }

        // Löscht nur, wenn es die Datei gibt; sonst passiert nichts.
        File.Delete(target);
        return 0;
    }

    // lstat-Semantik: fragt den Pfad selbst, folgt dem Link nicht, und antwortet auch bei einem
    // toten Link (PrefixHarvest.IsSymlink, gemessen).
    private static bool IsSymlink(string path) => new FileInfo(path).LinkTarget is not null;

    // Entfernt den Link selbst, nie sein Ziel. File.Delete ist unlink und löscht unter Linux auch
    // einen Verzeichnis-Symlink direkt (geprüft, siehe Task-Bericht) — der catch ist nur ein Netz
    // für den Fall, dass ein System den Eintrag doch als Verzeichnis führt.
    private static void Unlink(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (UnauthorizedAccessException)
        {
            Directory.Delete(path);
        }
    }
}
