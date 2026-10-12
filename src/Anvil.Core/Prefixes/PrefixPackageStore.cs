using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Anvil.Core.Prefixes;

/// <summary>
/// Wo gebaute Pakete liegen (S3.5, Spec §3.5): <c>&lt;Datenwurzel&gt;/packages/&lt;Schlüssel&gt;/</c>.
///
/// <para>
/// <b>Derselbe Delegat wie <see cref="Packages.PackageStore"/></b> (Spec §3.5, dieselbe Bauform):
/// Die Datenwurzel ist eine Einstellung, die der Nutzer im Betrieb umstellt, und wer den Pfad
/// festhielte, sähe danach in den alten Ordner. Der Konstruktor bekommt deshalb <c>root</c> — die
/// <b>Datenwurzel</b> —, nicht den Paketordner selbst: Diese Klasse ist die eine Stelle, die
/// weiß, dass <c>packages/</c> darunterliegt.
/// </para>
///
/// <para>
/// <b>Der Paket-Cache ist Maschinenbuchhaltung</b> (Vision §5) und wird dem Nutzer nicht
/// vorgeführt: keine Auflistung, keine Größenanzeige — nur <see cref="Find"/>,
/// <see cref="BeginBuild"/>, <see cref="RemoveStaleBuilds"/> und das Ergebnis.
/// </para>
///
/// <para>
/// <b>Der Punkt vor <c>build</c> ist Absicht</b>, kein Schmuck: Unter Linux die Form für „das geht
/// niemanden etwas an" — <see cref="Packages.PackageStore.List"/> überspringt Punktdateien seit
/// S3.3 aus genau diesem Grund. Ein halbes Paket taucht so nirgends als ganzes auf.
/// </para>
/// </summary>
public sealed class PrefixPackageStore
{
    /// <summary>
    /// Der Name des Paketordners unter der Datenwurzel: <c>&lt;Datenwurzel&gt;/packages/</c> —
    /// ein Geschwister von <c>dependencies/</c> und den Modulordnern. <b>Kein Modul darf so
    /// heißen</b> — dieselbe Regel wie bei <see cref="Packages.PackageStores.SharedDependencies"/>:
    /// Ein registriertes Modul mit diesem Schlüssel bekäme diesen Ordner als seinen Modulordner.
    /// </summary>
    public const string FolderName = "packages";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private readonly Func<string> _root;
    private readonly ILogger? _logger;

    // Zählt offene Bauten dieses Speichers in diesem Prozess (Spec S3.9 E-7): Solange einer
    // läuft, könnte ein liegen gebliebener ".build-*"-Ordner ihm gehören — RemoveStaleBuilds
    // fasst dann nichts an. Interlocked statt eines Schlosses: BeginBuild und Dispose greifen
    // von verschiedenen Fäden zu, und eine einzelne Zahl braucht kein eigenes Schloss.
    private int _openBuilds;

    /// <param name="root">Die Datenwurzel, bei jedem Zugriff neu aufgelöst.</param>
    /// <param name="logger">Null heißt stumm.</param>
    public PrefixPackageStore(Func<string> root, ILogger? logger = null)
    {
        _root = root;
        _logger = logger;
    }

    /// <summary>
    /// <c>&lt;Datenwurzel&gt;/packages/</c>, bei jedem Zugriff neu aufgelöst. Öffentlich, weil der
    /// Bauer (S3.5) zwei Fragen an genau diesen Ordner hat, die der Speicher nicht beantwortet:
    /// wie viel auf dessen Datenträger frei ist, und wo das fertige Paket danach liegt.
    /// </summary>
    public string PackagesPath => Path.Combine(_root(), FolderName);

    /// <summary>
    /// Das Paket unter <paramref name="key"/>, oder <c>null</c>: Ordner fehlt,
    /// <c>files/</c> fehlt, oder <c>package.json</c> fehlt oder ist unlesbar. <b>Ein kaputtes
    /// Paket ist kein Paket</b> — der Bauer baut es einfach neu, statt über eine Ausnahme zu
    /// stolpern; die unlesbare Manifest-Datei bekommt dafür eine Warnung im Log, die fehlenden
    /// Ordner nicht (ihr Fehlen ist vor dem ersten Bau der Normalfall).
    /// </summary>
    public PrefixPackage? Find(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        var path = Path.Combine(PackagesPath, key);
        if (!Directory.Exists(path))
        {
            return null;
        }

        if (!Directory.Exists(Path.Combine(path, PrefixPackage.FilesFolderName)))
        {
            return null;
        }

        var manifestPath = Path.Combine(path, PrefixPackage.ManifestFileName);
        if (!File.Exists(manifestPath))
        {
            return null;
        }

        PrefixPackageManifest? manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<PrefixPackageManifest>(
                File.ReadAllText(manifestPath), JsonOptions);
        }
        catch (Exception exception)
            when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            _logger?.LogWarning(
                exception, "package manifest unreadable, treated as missing: {Path}", manifestPath);
            return null;
        }

        if (manifest is null)
        {
            _logger?.LogWarning("package manifest empty, treated as missing: {Path}", manifestPath);
            return null;
        }

        return new PrefixPackage(key, path, manifest);
    }

    /// <summary>
    /// Beginnt einen Bau: legt <c>&lt;Datenwurzel&gt;/packages/.build-&lt;key&gt;-&lt;n&gt;/</c>
    /// an, mit den Unterordnern <c>prefix/</c> und <c>package/files/</c> darunter. <c>n</c> kommt
    /// aus <see cref="Path.GetRandomFileName"/>, damit zwei Läufe auf denselben Schlüssel (zwei
    /// Module, die beide <c>dotnet 4.8</c> anfordern) sich nie in dasselbe Verzeichnis schreiben.
    ///
    /// <para>
    /// Ob <paramref name="key"/> schon ein fertiges Paket ist, prüft diese Methode <b>nicht</b> —
    /// das ist die erste der billigen Prüfungen des Bauers, vor jedem teuren Schritt (Spec §3.6),
    /// und sie soll auch dann gelten, wenn sie fehlschlägt.
    /// </para>
    /// </summary>
    public PrefixPackageBuild BeginBuild(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        var packagesPath = PackagesPath;
        Directory.CreateDirectory(packagesPath);

        var buildPath = Path.Combine(packagesPath, $".build-{key}-{Path.GetRandomFileName()}");
        Directory.CreateDirectory(buildPath);

        var prefixPath = Path.Combine(buildPath, "prefix");
        var packagePath = Path.Combine(buildPath, "package");
        Directory.CreateDirectory(prefixPath);
        Directory.CreateDirectory(Path.Combine(packagePath, PrefixPackage.FilesFolderName));

        Interlocked.Increment(ref _openBuilds);
        _logger?.LogDebug("package build started: {Path}", buildPath);
        return new PrefixPackageBuild(
            key,
            packagesPath,
            buildPath,
            prefixPath,
            packagePath,
            onDisposed: () => Interlocked.Decrement(ref _openBuilds),
            _logger);
    }

    /// <summary>
    /// Entfernt liegengebliebene <c>.build-*</c>-Ordner (Spec S3.9 E-7): Wer Anvil während eines Baus
    /// beendet, lässt keinen <c>finally</c> mehr laufen. Ein gültiges Paket entsteht dabei nie — das
    /// macht erst das Umbenennen —, aber bis zu 1,6 GB blieben liegen. <b>Solange in diesem Prozess ein
    /// Bau offen ist, wird nichts angefasst</b>: Der Ordner könnte seiner sein.
    /// </summary>
    /// <returns>Wie viele Ordner entfernt wurden.</returns>
    public int RemoveStaleBuilds()
    {
        var packagesPath = PackagesPath;
        if (Volatile.Read(ref _openBuilds) > 0 || !Directory.Exists(packagesPath))
        {
            return 0;
        }

        var removed = 0;
        foreach (var path in Directory.EnumerateDirectories(packagesPath, ".build-*"))
        {
            try
            {
                Directory.Delete(path, recursive: true);
                removed++;
                _logger?.LogInformation("stale package build removed: {Path}", path);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                _logger?.LogWarning(exception, "stale package build could not be removed: {Path}", path);
            }
        }

        return removed;
    }
}

/// <summary>
/// Ein laufender Bau (S3.5, Spec §3.5) — <b>die ganze Atomarität der Story sitzt in diesem
/// <see cref="Dispose"/></b>. Gebaut wird in einem Nebenverzeichnis, das
/// <see cref="PrefixPackageStore.Find"/> nie als Paket liest; erst <see cref="Complete"/> benennt
/// <c>package/</c> per <see cref="Directory.Move(string, string)"/> auf den Schlüssel um — ein
/// Rename innerhalb desselben Dateisystems, kein Kopieren, also ein einziger, unteilbarer Schritt.
///
/// <para>
/// <b><see cref="Dispose"/> räumt alles weg, was nicht abgeschlossen ist</b> — bei Erfolg, bei
/// jeder Ausnahme, bei jedem Abbruch. Ein Bauer, der irgendwo zwischen <see cref="PrefixPackageStore.BeginBuild"/>
/// und <see cref="Complete"/> wirft oder abgebrochen wird, kann so kein halbes Paket
/// zurücklassen — er muss dafür nichts Eigenes tun, nur diesen Gegenstand in einem <c>using</c>
/// halten.
/// </para>
/// </summary>
public sealed class PrefixPackageBuild : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private readonly string _key;
    private readonly string _packagesPath;
    private readonly string _buildPath;
    private readonly Action _onDisposed;
    private readonly ILogger? _logger;
    private bool _completed;
    private bool _disposed;

    /// <param name="onDisposed">
    /// Zählt den Bau beim Speicher ab (<see cref="PrefixPackageStore.RemoveStaleBuilds"/>) —
    /// einmalig ausgelöst, egal wie oft <see cref="Dispose"/> gerufen wird (Spec S3.9 E-7).
    /// </param>
    internal PrefixPackageBuild(
        string key,
        string packagesPath,
        string buildPath,
        string prefixPath,
        string packagePath,
        Action onDisposed,
        ILogger? logger)
    {
        _key = key;
        _packagesPath = packagesPath;
        _buildPath = buildPath;
        PrefixPath = prefixPath;
        PackagePath = packagePath;
        _onDisposed = onDisposed;
        _logger = logger;
    }

    /// <summary>Der Wegwerf-Prefix, in den das Rezept baut — <c>&lt;Bau&gt;/prefix/</c>.</summary>
    public string PrefixPath { get; }

    /// <summary>Die entstehende Nutzlast, mit <c>files/</c> darunter — <c>&lt;Bau&gt;/package/</c>.</summary>
    public string PackagePath { get; }

    /// <summary>Wo die geernteten Dateien landen — <c>&lt;Bau&gt;/package/files/</c>.</summary>
    public string FilesPath => Path.Combine(PackagePath, PrefixPackage.FilesFolderName);

    /// <summary>
    /// Schreibt das Manifest nach <c>package/package.json</c> und benennt <c>package/</c> dann
    /// auf den Schlüssel um.
    ///
    /// <para>
    /// <b>Zielt der Schlüssel bereits auf ein vorhandenes Verzeichnis, scheitert der Umzug</b>
    /// (<see cref="Directory.Move(string, string)"/> wirft eine <see cref="IOException"/>, wenn
    /// das Ziel schon existiert): Das Nebenverzeichnis bleibt liegen und wird von
    /// <see cref="Dispose"/> geräumt. <b>Das ist der erwartete Weg</b>, kein zu behebender
    /// Fehler dieser Klasse — der Bauer prüft „liegt das Paket schon da" als allererste, billige
    /// Prüfung und meldet dafür <c>AlreadyPresent</c>, lange bevor er hier ankäme (Spec §3.6).
    /// </para>
    /// </summary>
    public void Complete(PrefixPackageManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        var manifestPath = Path.Combine(PackagePath, PrefixPackage.ManifestFileName);
        AtomicFile.WriteAllText(manifestPath, JsonSerializer.Serialize(manifest, JsonOptions));

        var targetPath = Path.Combine(_packagesPath, _key);
        Directory.Move(PackagePath, targetPath);
        _completed = true;
        _logger?.LogInformation("package built: {Path}", targetPath);
    }

    /// <summary>
    /// Löscht, was vom Nebenverzeichnis übrig ist — nach <see cref="Complete"/> nur noch
    /// <c>prefix/</c> und die leere Hülle, ohne <see cref="Complete"/> das Ganze. <b>Wirft nie</b>:
    /// Ein Fehlschlag beim Aufräumen ist eine Log-Zeile, keine Ausnahme — sonst überschriebe eine
    /// Aufräum-Ausnahme im <c>finally</c> des Bauers dessen eigentliche Fehlermeldung, und genau
    /// die soll beim Nutzer ankommen, nicht ein Nebenschauplatz.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _onDisposed();

        try
        {
            if (Directory.Exists(_buildPath))
            {
                Directory.Delete(_buildPath, recursive: true);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _logger?.LogWarning(
                exception,
                _completed
                    ? "leftover build directory could not be cleaned up: {Path}"
                    : "aborted build could not be cleaned up: {Path}",
                _buildPath);
        }
    }
}
