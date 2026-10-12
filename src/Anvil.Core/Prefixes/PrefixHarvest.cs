using Microsoft.Extensions.Logging;

namespace Anvil.Core.Prefixes;

/// <summary>
/// Kopiert die Nutzlast einer Komponente aus einem fertigen Wegwerf-Prefix in ein Paket (S3.5,
/// Spec §3.7) — die eine Hälfte der Ernte, die andere ist <see cref="WineRegistryExport"/>.
///
/// <para>
/// <b>Pfade werden gebaut wie in <see cref="PrefixMarkProbe"/></b> (Plan P-5): über
/// <see cref="Path.Combine(string[])"/> mit gesplitteten Segmenten, nie durch Ersetzen von
/// <c>/</c>. Nur so meinen Befund und Ernte denselben Pfad — ein Fund über <c>Path.Combine</c>
/// und ein Kopieren über <c>string.Replace</c> könnten unter Wine-Eigenheiten (führende Punkte,
/// Groß-/Kleinschreibung) auseinanderlaufen, ohne dass ein Test es je sähe.
/// </para>
///
/// <para>
/// <b>Symlinks werden übersprungen und gezählt</b>, gleich ob Datei oder Verzeichnis
/// (<c>LinkTarget is not null</c>) — sie sind Wines eigene Builtin-Stubs (z. B.
/// <c>mscoree.dll</c> zeigt auf eine gemeinsame Proton-Datei) und gehören nicht in unser Paket.
/// <see cref="HarvestResult.SkippedLinks"/> geht ins Log, damit ein unerwarteter Sprung auffällt
/// — ein Wine-Update, das aus einem Stub eine echte Datei macht oder umgekehrt, soll sich zeigen.
/// </para>
///
/// <para>
/// <b>Ein fehlender Eintrag ist kein Fehler.</b> Ob ein Eintrag im Prefix stehen <i>sollte</i>,
/// hat der Befund (<see cref="PrefixMarkProbe"/>) schon vor der Ernte entschieden — diese Klasse
/// kopiert nur noch, was da ist, und merkt sich fürs Log, was nicht da war.
/// </para>
/// </summary>
public static class PrefixHarvest
{
    /// <summary>
    /// Kopiert <paramref name="payloadFiles"/> und die zu <paramref name="patterns"/>
    /// passenden Dateien aus <c>&lt;prefixPath&gt;/drive_c/</c> nach
    /// <paramref name="targetFilesPath"/>, mit derselben relativen Struktur.
    /// </summary>
    /// <param name="prefixPath">Der <c>pfx</c>-Ordner des Wegwerf-Prefix.</param>
    /// <param name="payloadFiles">
    /// Einträge aus <see cref="PrefixPayload.Files"/>: eine Datei <b>oder</b> ein Verzeichnis,
    /// dann rekursiv. Ein fehlender Eintrag ist kein Fehler, nur eine Log-Zeile.
    /// </param>
    /// <param name="patterns">
    /// Muster des Rezepts, ein Stern nur im letzten Segment
    /// (<c>windows/system32/*_clr0400.dll</c>), ohne Rekursion (Plan P-6) — die passenden
    /// Dateien liegen genau in dem Verzeichnis, das die vorderen Segmente nennen.
    /// </param>
    /// <param name="targetFilesPath">Das <c>files/</c>-Verzeichnis des entstehenden Pakets.</param>
    /// <param name="cancellationToken">Wird zwischen Dateien geprüft.</param>
    /// <param name="logger"><c>null</c> heißt stumm.</param>
    public static HarvestResult Copy(
        string prefixPath,
        IReadOnlyList<string> payloadFiles,
        IReadOnlyList<string> patterns,
        string targetFilesPath,
        CancellationToken cancellationToken,
        ILogger? logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prefixPath);
        ArgumentNullException.ThrowIfNull(payloadFiles);
        ArgumentNullException.ThrowIfNull(patterns);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetFilesPath);

        var state = new HarvestState();

        foreach (var relativePath in payloadFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CopyPayloadEntry(prefixPath, relativePath, targetFilesPath, state, cancellationToken, logger);
        }

        foreach (var pattern in patterns)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CopyPatternEntry(prefixPath, pattern, targetFilesPath, state, cancellationToken, logger);
        }

        logger?.LogInformation(
            "harvest finished: {FileCount} files, {TotalBytes} bytes, {SkippedLinks} symlinks skipped",
            state.FileCount,
            state.TotalBytes,
            state.SkippedLinks);

        return new HarvestResult(state.FileCount, state.TotalBytes, state.SkippedLinks);
    }

    private static void CopyPayloadEntry(
        string prefixPath,
        string relativePath,
        string targetFilesPath,
        HarvestState state,
        CancellationToken cancellationToken,
        ILogger? logger)
    {
        var parts = relativePath.Split('/');
        var sourcePath = Path.Combine([prefixPath, "drive_c", .. parts]);

        if (IsSymlink(sourcePath))
        {
            state.SkippedLinks++;
            logger?.LogDebug("skipped symlink in payload: {RelativePath}", relativePath);
            return;
        }

        var targetPath = Path.Combine([targetFilesPath, .. parts]);

        if (Directory.Exists(sourcePath))
        {
            CopyDirectory(sourcePath, targetPath, relativePath, state, cancellationToken, logger);
            return;
        }

        if (File.Exists(sourcePath))
        {
            CopyFile(sourcePath, targetPath, state);
            return;
        }

        // Kein Fehler: Der Befund hat schon geurteilt (Klassenkommentar), das hier ist nur
        // Auskunft fürs Log.
        logger?.LogInformation("payload entry not found in prefix, skipped: {RelativePath}", relativePath);
    }

    // Rekursiv, damit ein Verzeichniseintrag der Nutzlast (z. B. "windows/Microsoft.NET")
    // vollständig mitgeht — inklusive verschachtelter Symlinks, die derselben Regel unterliegen
    // wie das oberste Element.
    private static void CopyDirectory(
        string sourceDir,
        string targetDir,
        string relativeDir,
        HarvestState state,
        CancellationToken cancellationToken,
        ILogger? logger)
    {
        Directory.CreateDirectory(targetDir);

        foreach (var sourcePath in Directory.EnumerateFileSystemEntries(sourceDir))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var name = Path.GetFileName(sourcePath);
            var targetPath = Path.Combine(targetDir, name);
            var relativePath = relativeDir + "/" + name;

            if (IsSymlink(sourcePath))
            {
                state.SkippedLinks++;
                logger?.LogDebug("skipped symlink: {RelativePath}", relativePath);
                continue;
            }

            if (Directory.Exists(sourcePath))
            {
                CopyDirectory(sourcePath, targetPath, relativePath, state, cancellationToken, logger);
            }
            else if (File.Exists(sourcePath))
            {
                CopyFile(sourcePath, targetPath, state);
            }
        }
    }

    // Ein Muster ist ein Stern im letzten Segment, sonst nichts (Plan P-6): Die vorderen
    // Segmente sind das Verzeichnis, das letzte ist das Suchmuster für
    // Directory.EnumerateFiles — kein eigener Glob-Dialekt, kein Rekursivlauf.
    private static void CopyPatternEntry(
        string prefixPath,
        string pattern,
        string targetFilesPath,
        HarvestState state,
        CancellationToken cancellationToken,
        ILogger? logger)
    {
        var parts = pattern.Split('/');
        var directoryParts = parts[..^1];
        var filePattern = parts[^1];

        var sourceDir = Path.Combine([prefixPath, "drive_c", .. directoryParts]);
        var targetDir = Path.Combine([targetFilesPath, .. directoryParts]);

        if (!Directory.Exists(sourceDir))
        {
            logger?.LogInformation(
                "payload pattern directory not found in prefix, skipped: {Pattern}", pattern);
            return;
        }

        foreach (var sourcePath in Directory.EnumerateFiles(sourceDir, filePattern))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var name = Path.GetFileName(sourcePath);
            var relativePath = string.Join('/', [.. directoryParts, name]);

            if (IsSymlink(sourcePath))
            {
                state.SkippedLinks++;
                logger?.LogDebug("skipped symlink matched by pattern: {RelativePath}", relativePath);
                continue;
            }

            var targetPath = Path.Combine(targetDir, name);
            CopyFile(sourcePath, targetPath, state);
        }
    }

    private static void CopyFile(string sourcePath, string targetPath, HarvestState state)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
        File.Copy(sourcePath, targetPath, overwrite: true);
        state.FileCount++;
        state.TotalBytes += new FileInfo(sourcePath).Length;
    }

    // FileInfo.LinkTarget stellt fest, ob der Pfad selbst ein Symlink ist — unabhängig davon,
    // ob er auf eine Datei oder ein Verzeichnis zeigt, und unabhängig davon, ob das Ziel
    // überhaupt existiert (lstat-Semantik, kein Verfolgen des Links). Gemessen: Sowohl
    // FileInfo als auch DirectoryInfo liefern für denselben Symlink dasselbe LinkTarget.
    private static bool IsSymlink(string path) => new FileInfo(path).LinkTarget is not null;

    private sealed class HarvestState
    {
        internal int FileCount;
        internal long TotalBytes;
        internal int SkippedLinks;
    }
}

/// <summary>Was <see cref="PrefixHarvest.Copy"/> tatsächlich kopiert hat — gezählt, nicht geschätzt.</summary>
/// <param name="FileCount">Zahl der kopierten Dateien.</param>
/// <param name="TotalBytes">Gesamtgröße der kopierten Dateien in Bytes.</param>
/// <param name="SkippedLinks">Zahl der übersprungenen Symlinks (Dateien und Verzeichnisse).</param>
public sealed record HarvestResult(int FileCount, long TotalBytes, int SkippedLinks);
