namespace Anvil.Core.Prefixes;

/// <summary>Was an einem Pfad lag, <b>bevor</b> Anvil ihn angefasst hat (Spec S4.1 §3.4).</summary>
public enum PriorKind
{
    /// <summary>Dort lag nichts. <b>Nicht dasselbe wie eine leere Datei</b> — die ist <see cref="File"/>.</summary>
    Missing,

    /// <summary>Eine echte Datei; ihre Kopie liegt unter <see cref="PrefixPayloadBackup"/>.</summary>
    File,

    /// <summary>Ein echtes Verzeichnis.</summary>
    Directory,

    /// <summary>Ein Symlink; sein Ziel steht in <see cref="RollbackEntry.PriorTarget"/>.</summary>
    Symlink,
}

/// <summary>Wie weit das Anwenden gekommen ist (Spec S4.1 §3.1).</summary>
public enum RollbackStage
{
    /// <summary>
    /// Geschrieben, bevor das erste Byte in den Prefix ging. Die Nutzlast liegt unvollständig da;
    /// <b>ein Rückbau ist daraus nicht möglich</b>, weil die Prüfsummen des Geschriebenen fehlen.
    /// </summary>
    Applying,

    /// <summary>Geschrieben, nachdem der Befund positiv war. Vollständig.</summary>
    Applied,
}

/// <summary>
/// Ein Pfad des Pakets oder eine Verzeichnisebene darüber, mit seinem Vorzustand (Spec S4.1 §3.4).
/// </summary>
/// <param name="Path">Relativ zu <c>drive_c/</c>, mit <c>/</c>.</param>
/// <param name="Prior">Was dort lag.</param>
/// <param name="PriorTarget">Das Ziel des Symlinks, wörtlich wie gelesen; sonst <c>null</c>.</param>
/// <param name="AppliedSha256">
/// Die Prüfsumme dessen, was Anvil dort hingeschrieben hat — <c>null</c> bei Verzeichnisebenen und
/// in Stufe <see cref="RollbackStage.Applying"/>. <b>Daran hängt die Regel aus Versuch D</b>
/// (Bericht §8): Nur was noch diese Prüfsumme trägt, stammt vom Paket und darf zurückgebaut werden.
/// </param>
public sealed record RollbackEntry(string Path, PriorKind Prior, string? PriorTarget, string? AppliedSha256);

/// <summary>Der bisherige Wert eines DllOverride (Spec S4.1 §3.3).</summary>
/// <param name="Name">Der Wertname unter <see cref="PrefixMarkProbe.DllOverridesKey"/>.</param>
/// <param name="Value"><c>null</c> heißt: Der Wert fehlte — der Rückbau entfernt ihn dann wieder.</param>
public sealed record PriorOverride(string Name, string? Value);

/// <summary>
/// Das Rückbau-Protokoll einer Nutzlast (Spec S4.1 §3.3). Es wohnt <b>im</b> Vermerk, nicht in einer
/// zweiten Datei — eine Wahrheit je Nutzlast (Vision Wand Bridge §7).
///
/// <para>
/// <b>Diese Story liest es nicht</b>; sie schreibt es nur. Der Leser ist S4.2.
/// </para>
/// </summary>
/// <param name="FormatVersion">
/// Heute <see cref="CurrentFormatVersion"/>. Eine andere Zahl — auch eine <b>höhere</b> — wird
/// abgelehnt (E-7): Ein Protokoll halb zu verstehen ist schlimmer, als es nicht zu haben.
/// </param>
/// <param name="Stage">Wie weit das Anwenden gekommen ist.</param>
/// <param name="Entries">Ein Eintrag je Pfad, ordinal sortiert, ohne Doppelte.</param>
/// <param name="RegistryBranches">Die Zweige, die das Paket gesetzt hat — aus dem Paketmanifest.</param>
/// <param name="RegistryBackup">
/// Ob eine vorherige Ausfuhr gesichert wurde. <c>false</c> heißt: <b>kein</b> Zweig war vorher da
/// (oder der Export ist gescheitert, E-8) — kein Fehler, sondern eine Aussage über vorher.
/// </param>
/// <param name="PriorOverrides">Der bisherige Wert je DllOverride der Komponente.</param>
public sealed record PayloadRollback(
    int FormatVersion,
    RollbackStage Stage,
    IReadOnlyList<RollbackEntry> Entries,
    IReadOnlyList<string> RegistryBranches,
    bool RegistryBackup,
    IReadOnlyList<PriorOverride> PriorOverrides)
{
    /// <summary>Die Fassung, die dieses Anvil schreibt und als einzige liest.</summary>
    public const int CurrentFormatVersion = 1;

    // Pruefung in Initialisierern wie bei PrefixPayloadRecord: Was hier wirft, faengt
    // PrefixPayloadRecords.Read und macht daraus ein ehrliches "unbekannt" (E-6).

    /// <summary>Heute <see cref="CurrentFormatVersion"/>; jede andere Zahl wird abgelehnt.</summary>
    public int FormatVersion { get; init; } = FormatVersion == CurrentFormatVersion
        ? FormatVersion
        : throw new ArgumentException(
            $"Rollback format version {FormatVersion} is not readable by this build.", nameof(FormatVersion));

    // Anders als FormatVersion braucht Entries eine eigene init-Logik statt nur eines
    // Initialisierers: Die Gegenprobe baut ein bestehendes Protokoll per "with" um (E-6), und ein
    // reiner Initialisierer liefe dabei nicht erneut — die Pruefung wuerde beim Umbau umgangen.
    private readonly IReadOnlyList<RollbackEntry> _entries = ValidateEntries(Entries);

    /// <summary>Ein Eintrag je Pfad, ordinal sortiert, ohne Doppelte.</summary>
    public IReadOnlyList<RollbackEntry> Entries
    {
        get => _entries;
        init => _entries = ValidateEntries(value);
    }

    private static IReadOnlyList<RollbackEntry> ValidateEntries(IReadOnlyList<RollbackEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            ArgumentNullException.ThrowIfNull(entry);
            ArgumentException.ThrowIfNullOrWhiteSpace(entry.Path, nameof(Entries));

            if (!seen.Add(entry.Path))
            {
                throw new ArgumentException(
                    $"A rollback protocol must name each path once, but got '{entry.Path}' twice.", nameof(Entries));
            }
        }

        return entries;
    }
}

/// <summary>
/// Der Ort der geretteten Dateien und der Erheber des Vorzustands (Spec S4.1 §3.4, §3.7):
/// <c>&lt;pfx&gt;/.anvil/backup/&lt;Komponentenschlüssel&gt;/</c>.
///
/// <para>
/// <b>Klumpen, kein zweiter Vermerk</b> (E-2): Was hier liegt, behauptet nichts — es <i>ist</i> der
/// Vorzustand. Der Vermerk bleibt die eine Wahrheit über die Nutzlast.
/// </para>
///
/// <para>
/// <b>Erst sichern, dann anfassen.</b> Was nicht gesichert werden kann, wird nicht überschrieben:
/// Diese Klasse wirft in dem Fall, und der Anwender endet mit <c>Failed</c>, bevor ein Byte
/// geschrieben ist.
/// </para>
/// </summary>
public static class PrefixPayloadBackup
{
    /// <summary>Der Unterordner der Sicherungen, neben <c>payloads</c>.</summary>
    public const string FolderName = "backup";

    /// <summary>Darunter der Baum der geretteten Dateien, gespiegelt zu <c>drive_c/</c>.</summary>
    public const string FilesFolderName = "files";

    /// <summary>Die vorherige Registry-Ausfuhr.</summary>
    public const string RegistryFileName = "registry.reg";

    /// <summary>Der Sicherungsordner dieser Komponente.</summary>
    public static string PathFor(string prefixPath, string componentKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prefixPath);
        return Path.Combine(
            prefixPath, PrefixPayloadRecords.FolderName, FolderName,
            PrefixPayloadRecords.EnsurePlainName(componentKey));
    }

    /// <summary>Der Baum der geretteten Dateien.</summary>
    public static string FilesPathFor(string prefixPath, string componentKey)
        => Path.Combine(PathFor(prefixPath, componentKey), FilesFolderName);

    /// <summary>Die vorherige Registry-Ausfuhr.</summary>
    public static string RegistryFilePathFor(string prefixPath, string componentKey)
        => Path.Combine(PathFor(prefixPath, componentKey), RegistryFileName);

    /// <summary>
    /// Räumt die Sicherungen eines früheren Anwendens weg. Sie sind falsch, sobald ein neues Paket
    /// anfängt — ein Vorzustand gehört zu genau einem Lauf.
    /// </summary>
    public static void Clear(string prefixPath, string componentKey)
    {
        var path = PathFor(prefixPath, componentKey);
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }

    /// <summary>
    /// Erhebt den Vorzustand für jeden Pfad des Pakets <b>und jede Verzeichnisebene darüber</b> und
    /// sichert dabei jede echte Datei, die überschrieben würde.
    ///
    /// <para>
    /// <b>Die Ebenen darüber gehören dazu</b>, weil <see cref="PrefixPayloadFiles"/> verlinkte
    /// Ebenen durch echte Verzeichnisse ersetzt: Ohne ihren Vorzustand ließe sich der Symlink nie
    /// wiederherstellen.
    /// </para>
    /// </summary>
    public static IReadOnlyList<RollbackEntry> Survey(
        string prefixPath, string componentKey, PayloadFilePlan plan, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prefixPath);
        ArgumentNullException.ThrowIfNull(plan);

        var driveC = Path.Combine(prefixPath, "drive_c");
        var backupFiles = FilesPathFor(prefixPath, componentKey);

        // Sortiert und doppelfrei: Eltern stehen vor ihren Kindern, und zwei Paketdateien im
        // selben Ordner ergeben eine Ebene, nicht zwei.
        var paths = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var relativePath in plan.RelativePaths)
        {
            var parts = relativePath.Split('/');
            for (var depth = 1; depth <= parts.Length; depth++)
            {
                paths.Add(string.Join('/', parts[..depth]));
            }
        }

        var entries = new List<RollbackEntry>(paths.Count);
        foreach (var path in paths)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var fullPath = Path.Combine([driveC, .. path.Split('/')]);

            // Symlink zuerst: Directory.Exists folgt dem Link und nennt einen Verzeichnis-Symlink
            // ein Verzeichnis — der Fehler, gegen den die halbe S3.6 gebaut ist. LinkTarget ist
            // lstat-basiert und antwortet auch bei einem toten Link.
            var linkTarget = new FileInfo(fullPath).LinkTarget;
            if (linkTarget is not null)
            {
                entries.Add(new RollbackEntry(path, PriorKind.Symlink, linkTarget, null));
            }
            else if (Directory.Exists(fullPath))
            {
                entries.Add(new RollbackEntry(path, PriorKind.Directory, null, null));
            }
            else if (File.Exists(fullPath))
            {
                var backupPath = Path.Combine([backupFiles, .. path.Split('/')]);
                Directory.CreateDirectory(Path.GetDirectoryName(backupPath)!);
                File.Copy(fullPath, backupPath, overwrite: true);
                entries.Add(new RollbackEntry(path, PriorKind.File, null, null));
            }
            else
            {
                entries.Add(new RollbackEntry(path, PriorKind.Missing, null, null));
            }
        }

        return entries;
    }
}
