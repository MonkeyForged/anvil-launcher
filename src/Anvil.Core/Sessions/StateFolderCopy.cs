namespace Anvil.Core.Sessions;

/// <summary>
/// Ersetzt einen Ordner durch die gefilterte Kopie eines anderen (Spec S4.6 §3.3) — das Werkzeug
/// beider Richtungen: saeen wie ernten.
///
/// <para>
/// <b>Erst daneben, dann an die Stelle</b>: Kopiert wird nach <c>&lt;Ziel&gt;.partial</c>, und
/// erst das Umbenennen macht die Kopie gueltig. Die Hausform aus <c>WandApplication</c>, und hier
/// die eigentliche Zusicherung: Ein Abbruch mitten im Ernten darf die Vorlage nicht zerstoeren —
/// sie ist die einzige Stelle, an der die Anmeldung aller Spiele zusammenlaeuft.
/// </para>
///
/// <para>
/// <b>Rechte nur fuer den Besitzer</b> (0700 auf Ordner, 0600 auf Dateien). Was hier reist, ist
/// die Anmeldung bei einem fremden Dienst; sie geht niemanden sonst auf diesem Rechner an.
/// </para>
///
/// <para>
/// <b>Symlinks reisen nicht mit.</b> Dieselbe Linie wie beim Paketinhalt
/// (<c>PrefixPayloadFiles</c>): Ein Link zeigt irgendwohin, und irgendwohin gehoert nicht in eine
/// Kopie, die auf einer anderen Platte landen kann.
/// </para>
///
/// <para>
/// <b>Gelesen wird nichts.</b> Diese Klasse oeffnet keine Datei zum Ansehen; sie kopiert Bytes und
/// zaehlt.
/// </para>
/// </summary>
public static class StateFolderCopy
{
    /// <summary>Der Ordner, in den kopiert wird, bevor umbenannt wird — die Fertig-Marke.</summary>
    public const string PartialSuffix = ".partial";

    private const UnixFileMode FolderMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    private const UnixFileMode FileMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    /// <summary>
    /// Ob in <paramref name="source"/> ueberhaupt etwas steht, das mitreisen wuerde. <b>Nach der
    /// Auslassregel geurteilt</b> (Spec §3.3): Ein Ordner, in dem nur Caches liegen, ist fuer diese
    /// Frage leer — sonst ersetzte eine Vorlage aus lauter Caches eine gute Kopie durch nichts.
    /// </summary>
    public static bool HasAnythingToCopy(string source, IReadOnlyList<string> skipped)
        => Directory.Exists(source)
            && Directory.EnumerateFileSystemEntries(source)
                .Any(entry => !IsSkipped(Path.GetFileName(entry), skipped));

    /// <summary>
    /// Ersetzt <paramref name="target"/> durch die Kopie von <paramref name="source"/> und liefert
    /// die Zahl kopierter Dateien. Wirft bei Abbruch und bei jedem Dateifehler — das Urteil faellt
    /// der Aufrufer (<see cref="AttachStateRelay"/>).
    /// </summary>
    /// <param name="skipped">
    /// Namen der <b>ersten Ebene</b>, die nie mitreisen. <c>Cache</c> trifft den Ordner
    /// <c>Cache</c>, nicht <c>DawnGraphiteCache</c>.
    /// </param>
    public static int Replace(
        string source,
        string target,
        IReadOnlyList<string> skipped,
        CancellationToken cancellationToken)
    {
        var partial = target + PartialSuffix;
        if (Directory.Exists(partial))
        {
            // Der Rest eines abgebrochenen Laufs. Er gehoert Anvil, nicht dem Nutzer.
            Directory.Delete(partial, recursive: true);
        }

        Directory.CreateDirectory(partial);
        File.SetUnixFileMode(partial, FolderMode);

        var copied = 0;
        foreach (var entry in Directory.EnumerateFileSystemEntries(source))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var name = Path.GetFileName(entry);
            if (IsSkipped(name, skipped))
            {
                continue;
            }

            copied += CopyEntry(entry, Path.Combine(partial, name), cancellationToken);
        }

        if (Directory.Exists(target))
        {
            Directory.Delete(target, recursive: true);
        }

        Directory.Move(partial, target);
        return copied;
    }

    /// <summary>
    /// Ob <paramref name="name"/> — ein Name der <b>ersten Ebene</b> — nie mitreist. <c>Cache</c>
    /// trifft den Ordner <c>Cache</c> und <c>cache</c>, nicht <c>DawnGraphiteCache</c>.
    ///
    /// <para>
    /// <b>Oeffentlich, damit es nur eine Wahrheit gibt</b> (Spec S4.7 §3.5): Der Live-Lauf sagt,
    /// welche Eintraege im Login-Ordner die Regel <b>nicht</b> greift — das sind die Kandidaten
    /// fuer die Auslassliste. Wer die Regel dort nachbaute, koennte der Produktion widersprechen,
    /// ohne dass es auffaellt.
    /// </para>
    /// </summary>
    public static bool IsSkipped(string name, IReadOnlyList<string> skipped)
        => skipped.Any(one => string.Equals(one, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Kopiert eine Datei oder einen ganzen Teilbaum und liefert die Zahl der Dateien.</summary>
    private static int CopyEntry(string source, string target, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (Directory.Exists(source))
        {
            // Ein Symlink auf einen Ordner meldet sich hier ebenfalls — die Pruefung darunter
            // faengt ihn ab, bevor ein Baum kopiert wird, der gar nicht hier liegt.
            if (new DirectoryInfo(source).LinkTarget is not null)
            {
                return 0;
            }

            Directory.CreateDirectory(target);
            File.SetUnixFileMode(target, FolderMode);

            var copied = 0;
            foreach (var child in Directory.EnumerateFileSystemEntries(source))
            {
                copied += CopyEntry(child, Path.Combine(target, Path.GetFileName(child)), cancellationToken);
            }

            return copied;
        }

        if (new FileInfo(source).LinkTarget is not null)
        {
            return 0;
        }

        File.Copy(source, target, overwrite: true);
        File.SetUnixFileMode(target, FileMode);
        return 1;
    }
}
