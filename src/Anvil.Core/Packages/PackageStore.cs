using Microsoft.Extensions.Logging;

namespace Anvil.Core.Packages;

/// <summary>
/// A store (S3.3): a folder the user drops files into, and exactly one selection over it. Anvil
/// lists what is there — it copies nothing in and clears nothing away.
///
/// <para>
/// <b>Why delegates and not an interface with two implementations</b> (Spec §3.2): because it is
/// the house style. The Proton catalog, the data root and the module folder are all passed as
/// <c>Func&lt;…&gt;</c>, for the same reason — the value can change at runtime, and whoever holds
/// on to it works with a snapshot afterwards. The two owners of a store (the Core for the shared
/// dependencies, the module for its application) are therefore <b>two pairs of delegates</b>
/// here, not two classes. <see cref="IPackageChoices"/> is a different seam: it is about what
/// kind of entries a selection row lists (s9.1), not about who owns a store.
/// </para>
///
/// <para>
/// <b>The folder is created on access, not in the constructor.</b> A constructor would create it
/// where the data root lay at <i>start</i> — and the data root is a setting the user changes at
/// runtime. The same reasoning as for <see cref="ModuleDataFolders"/>, and the same consequence:
/// a second call does not create anew and does not throw.
/// </para>
///
/// <para>
/// <b><see cref="Selected"/> consults the list</b> (Spec E-4) instead of passing the stored name
/// through: the selection must never point to a file that does not exist. The stored name stays
/// — <b>reading must not write</b>; the reasoning sits at the line itself.
/// </para>
///
/// <para>
/// This class has state only in the form of its four constructor arguments: no cached path, no
/// remembered list, no remembered name.
/// </para>
/// </summary>
public sealed class PackageStore : IPackageChoices
{
    private readonly Func<string> _folder;
    private readonly Func<string?> _readSelection;
    private readonly Action<string?> _writeSelection;
    private readonly ILogger? _logger;

    /// <param name="folder">
    /// The store's folder, resolved anew on every access — at runtime bound from
    /// <see cref="ModuleDataFolders"/>, in tests a temp path.
    /// </param>
    /// <param name="readSelection">The user's stored intent; null means "none".</param>
    /// <param name="writeSelection">Writes the same intent back; null clears it.</param>
    /// <param name="logger">The owner decides the category; null means silent.</param>
    public PackageStore(
        Func<string> folder,
        Func<string?> readSelection,
        Action<string?> writeSelection,
        ILogger? logger = null)
    {
        _folder = folder;
        _readSelection = readSelection;
        _writeSelection = writeSelection;
        _logger = logger;
    }

    /// <summary>
    /// The store's folder, created if it is missing. The path holds for <i>this</i> call — it is
    /// not kept.
    /// </summary>
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
                // Whoever cannot create their store cannot store anything — the exception goes
                // up (Plan P-3). But under its own name and with a log line before it, just like
                // in ModuleDataFolders.For: if a file is in the way, the runtime reports "Could
                // not find a part of the path" and sends whoever searches down a false trail.
                _logger?.LogError(exception, "cannot create package folder: {Path}", path);
                throw new IOException($"cannot create package folder: {path}", exception);
            }

            _logger?.LogInformation("package folder created: {Path}", path);
            return path;
        }
    }

    /// <summary>
    /// What lies in the store <i>now</i>, in stable order. If the folder is missing or not
    /// readable, the list is empty — without an exception (Spec §3.5).
    /// </summary>
    public IReadOnlyList<StoredFile> List()
    {
        var path = _folder();

        // Looking is not an action: List() does not create the folder. A missing folder is the
        // normal case before the first drop, hence Debug and not a warning.
        if (!Directory.Exists(path))
        {
            _logger?.LogDebug("package folder does not exist: {Path}", path);
            return [];
        }

        try
        {
            return new DirectoryInfo(path)
                // Not recursive, and directories drop out: a folder is not a package — a
                // directory that happens to be named "wemod-11.6.0.zip" is not a file. S3.1
                // handled the same trap explicitly in the component finding.
                .EnumerateFiles()
                // Dot files drop out: on Linux the form for "this is nobody's business" — and so
                // the place S3.3a needs for half-finished downloads.
                .Where(file => !file.Name.StartsWith('.'))
                // No extension filter (Spec E-2): the Core does not know that a dependency is
                // .exe and an application .zip. The consequence is intended — whoever drops a
                // README.txt in sees it in the selection. That is more honest than a file that
                // lies in the folder and does not show up in Anvil.
                .Select(file => new StoredFile(file.Name, file.FullName, file.Length))
                // Sorted, because the file system's order is no order: Directory.EnumerateFiles
                // returns hash order on ext4, and the selection row in S3.4 would look different
                // after every start.
                .OrderBy(file => file.Name, StringComparer.OrdinalIgnoreCase)
                // The tie-breaker: without it the order is not total for two names that differ
                // only in case — the list flickers.
                .ThenBy(file => file.Name, StringComparer.Ordinal)
                .ToList();
        }
        catch (Exception exception)
            when (exception is IOException or UnauthorizedAccessException)
        {
            // Whoever only looks at what lies there gets the honest answer "nothing" (Plan P-3).
            // A listing that throws would crash the settings page in S3.4 because an external
            // drive is not mounted.
            _logger?.LogWarning(exception, "cannot list package folder: {Path}", path);
            return [];
        }
    }

    /// <summary>The names of <see cref="List"/>, in its order — what a selection row offers
    /// (<see cref="IPackageChoices"/>).</summary>
    public IReadOnlyList<string> Names()
        => List().Select(file => file.Name).ToList();

    /// <summary>
    /// The valid selection, or null for "none". Reads the stored name and checks it against
    /// <see cref="List"/> — the selection never points to a file that does not exist.
    /// </summary>
    public string? Selected
    {
        get
        {
            var stored = _readSelection();

            // An empty string is not a choice but a leftover in the file — the same reading as for
            // AppSettings.DataLocation in ModuleDataFolders.Root.
            if (string.IsNullOrEmpty(stored))
            {
                return null;
            }

            if (Contains(stored))
            {
                return stored;
            }

            // Reading must not write (Spec E-4). The data root can sit on an external drive. If
            // this wrote back, the user's choice would be gone for good after a single start
            // without the drive mounted — deleted by a glance, not by an action. The stored name
            // therefore stays; if the file comes back, it counts again. A Select(…) overwrites
            // the leftover at any time.
            _logger?.LogWarning("selected package is not in the store: {Name}", stored);
            return null;
        }
    }

    /// <summary>
    /// Chooses a file of the store; null clears the selection. A name that does not lie in the
    /// store is not taken over.
    /// </summary>
    public void Select(string? name)
    {
        // Treat empty like null, so reading and writing share one reading: what Selected reads as
        // "none", Select must be able to write as "none" too.
        if (string.IsNullOrEmpty(name))
        {
            _writeSelection(null);
            _logger?.LogInformation("package selection cleared");
            return;
        }

        if (!Contains(name))
        {
            // Not a runtime case via the UI, but the place where a composed name ("catalog entry
            // instead of file", S3.3a) would otherwise run silently into the settings.
            _logger?.LogWarning("package is not in the store, selection unchanged: {Name}", name);
            return;
        }

        _writeSelection(name);
        _logger?.LogInformation("package selected: {Name}", name);
    }

    // The name comparison is exact to the character: a file system that tells WeMod.zip and
    // wemod.zip apart does so here too. Deliberately a different choice than for sorting —
    // sorting is presentation, comparing is identity.
    private bool Contains(string name)
        => List().Any(file => string.Equals(file.Name, name, StringComparison.Ordinal));
}
