using Anvil.Core.Packages;
using Anvil.Core.Settings;
using Microsoft.Extensions.Logging;

namespace Anvil.App.Modules;

/// <summary>A complete release in the store. Name: folder name, or zip name without ".zip".
/// Path: full path. IsArchive: it is a zip.</summary>
public sealed record CeRelease(string Name, string Path, bool IsArchive);

/// <summary>
/// The store of placed CE releases (<c>&lt;data root&gt;/ce_bridge/</c>) and the user's choice
/// over it. Like <see cref="PackageStore"/> it lists what is there and copies nothing in; unlike
/// it, an entry is a folder or a zip and only a <i>complete</i> one (<see cref="CeInstallation"/>)
/// counts as a release. Nothing is cached: the folder, its content and the saved name are read
/// anew on every access, and reading never writes.
/// </summary>
public sealed class CeReleaseStore : IPackageChoices
{
    /// <summary>Reserved for the taken-over releases of s9.2/s9.3; never a candidate.</summary>
    public const string ReservedFolder = "app";

    /// <summary>Key in the module's settings.json, the same form as PackageStores'
    /// "selected.&lt;folder&gt;".</summary>
    public const string SelectionKey = "selected.release";

    private const string ArchiveExtension = ".zip";

    private readonly Func<string> _folder;
    private readonly ModuleSettings _settings;
    private readonly ILogger? _logger;

    /// <param name="folder">The store's folder, resolved anew on every access.</param>
    /// <param name="settings">Holds the user's choice under <see cref="SelectionKey"/>.</param>
    /// <param name="logger">The owner decides the category; null means silent.</param>
    public CeReleaseStore(Func<string> folder, ModuleSettings settings, ILogger? logger = null)
    {
        _folder = folder;
        _settings = settings;
        _logger = logger;
    }

    /// <summary>The folder, created if missing; an <see cref="IOException"/> with the path if it
    /// cannot be created (same behaviour as <see cref="PackageStore.Folder"/>).</summary>
    public string Folder
    {
        get
        {
            var path = _folder();
            if (Directory.Exists(path))
            {
                return path;
            }

            try
            {
                Directory.CreateDirectory(path);
            }
            catch (Exception exception)
                when (exception is IOException or UnauthorizedAccessException)
            {
                // Named and logged here because a file in the way makes the runtime report
                // "Could not find a part of the path", which sends whoever searches down a false
                // trail.
                _logger?.LogError(exception, "cannot create CE release folder: {Path}", path);
                throw new IOException($"cannot create CE release folder: {path}", exception);
            }

            _logger?.LogInformation("CE release folder created: {Path}", path);
            return path;
        }
    }

    /// <summary>
    /// The complete releases now present, sorted ignoring case. Incomplete or unreadable entries
    /// are left out with one warning each (per call) naming entry and problem. A missing or
    /// unreadable folder gives an empty list without an exception and without creating it.
    /// </summary>
    public IReadOnlyList<CeRelease> List()
    {
        var path = _folder();

        // Looking is not an action: a missing folder is the normal case before the first drop.
        if (!Directory.Exists(path))
        {
            _logger?.LogDebug("CE release folder does not exist: {Path}", path);
            return [];
        }

        List<FileSystemInfo> candidates;
        try
        {
            candidates = new DirectoryInfo(path)
                .EnumerateFileSystemInfos()
                .Where(IsCandidate)
                .ToList();
        }
        catch (Exception exception)
            when (exception is IOException or UnauthorizedAccessException)
        {
            _logger?.LogWarning(exception, "cannot list CE release folder: {Path}", path);
            return [];
        }

        var releases = new List<CeRelease>();

        // Folders first: when a folder and a zip share a name the folder wins, and a zip is only
        // judged if its name is still free.
        foreach (var folder in candidates.OfType<DirectoryInfo>())
        {
            var check = CeInstallation.CheckFolder(folder.FullName);
            if (check.IsComplete)
            {
                releases.Add(new CeRelease(folder.Name, folder.FullName, IsArchive: false));
            }
            else
            {
                LogLeftOut(folder.Name, Describe(check));
            }
        }

        foreach (var archive in candidates.OfType<FileInfo>())
        {
            var name = Path.GetFileNameWithoutExtension(archive.Name);
            if (releases.Any(release => release.Name == name))
            {
                LogLeftOut(archive.Name, $"same name as another release {name}");
                continue;
            }

            var check = CeInstallation.CheckArchive(archive.FullName);
            if (check.IsComplete)
            {
                releases.Add(new CeRelease(name, archive.FullName, IsArchive: true));
            }
            else
            {
                LogLeftOut(archive.Name, Describe(check));
            }
        }

        // The file system's order is no order; the tie-break keeps two names that differ only in
        // case from flickering.
        return releases
            .OrderBy(release => release.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(release => release.Name, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>The names of <see cref="List"/>, in its order.</summary>
    public IReadOnlyList<string> Names()
        => List().Select(release => release.Name).ToList();

    /// <summary>The saved choice if it is among <see cref="Names"/>, otherwise null. Never
    /// writes: the stored name stays, so a release on a drive that is not mounted counts again
    /// once it is back.</summary>
    public string? Selected
    {
        get
        {
            var stored = _settings.Get(SelectionKey);
            if (string.IsNullOrEmpty(stored))
            {
                return null;
            }

            if (Contains(stored))
            {
                return stored;
            }

            _logger?.LogWarning("selected CE release is not in the store: {Name}", stored);
            return null;
        }
    }

    /// <summary>Saves <paramref name="name"/> as the choice; null or "" clears it. A name that is
    /// not among <see cref="Names"/> is ignored (logged, choice unchanged).</summary>
    public void Select(string? name)
    {
        if (string.IsNullOrEmpty(name))
        {
            _settings.Set(SelectionKey, null);
            _logger?.LogInformation("release selection cleared");
            return;
        }

        if (!Contains(name))
        {
            _logger?.LogWarning("CE release is not in the store, selection unchanged: {Name}", name);
            return;
        }

        _settings.Set(SelectionKey, name);
        _logger?.LogInformation("release selected: {Name}", name);
    }

    // Dot entries are nobody's business (half-finished downloads, caches); the reserved name is
    // kept free for the taken-over releases whether it is a folder or a zip.
    private static bool IsCandidate(FileSystemInfo entry)
    {
        if (entry.Name.StartsWith('.'))
        {
            return false;
        }

        if (entry is DirectoryInfo)
        {
            return entry.Name != ReservedFolder;
        }

        return entry.Extension.Equals(ArchiveExtension, StringComparison.OrdinalIgnoreCase)
            && Path.GetFileNameWithoutExtension(entry.Name) != ReservedFolder;
    }

    private static string Describe(CeInstallationCheck check)
        => check.Detail is null ? check.Problem.ToString() : $"{check.Problem}: {check.Detail}";

    private void LogLeftOut(string entry, string reason)
        => _logger?.LogWarning("CE release left out: {Entry} ({Reason})", entry, reason);

    // Exact comparison: identity, not presentation (the sorting ignores case, this does not).
    private bool Contains(string name)
        => List().Any(release => string.Equals(release.Name, name, StringComparison.Ordinal));
}
