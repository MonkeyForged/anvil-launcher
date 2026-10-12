using System.ComponentModel;
using Anvil.App.Modules;
using Anvil.Core.Installers;
using Anvil.Core.Packages;
using Anvil.Core.Steam;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace Anvil.App.ViewModels;

/// <summary>
/// The settings section of CE Bridge (s9.1): the release selection row and the action row that
/// opens the store's folder. <b>The surface where the user fixes a finding</b> of
/// <see cref="CeBridgeModule"/> — by placing a release, or by getting the standard release from
/// the vendor (s9.3, <see cref="CeReleaseGetter"/>).
///
/// <para>
/// <b>The Core draws the card's frame</b> (<see cref="ModuleSettingsCardViewModel"/>); this
/// ViewModel only supplies the content, the same division of labor as with Wand Bridge. It does
/// not get the registry but a narrow delegate that triggers exactly its own module's check.
/// </para>
/// </summary>
public sealed partial class CeBridgeSettingsViewModel : ObservableObject, IShowable
{
    /// <summary>The row texts sit as constants on the ViewModel, not as strings in the
    /// <c>.axaml</c>: no test would reach them there.</summary>
    public const string ReleaseLabel = "CE version";

    /// <inheritdoc cref="ReleaseLabel"/>
    public const string ReleaseSub = "Which release to attach. Anvil gets the standard release from the vendor.";

    /// <inheritdoc cref="ReleaseLabel"/>
    public const string ReleaseBoxName = "CE application";

    /// <summary>The folder as the row's sentences name it — relative, not the full path (the full
    /// path stands under <c>Data location</c>).</summary>
    public const string FolderText = "ce_bridge/";

    /// <inheritdoc cref="ReleaseLabel"/>
    public const string StorageLabel = "Storage location";

    /// <inheritdoc cref="ReleaseLabel"/>
    public const string StorageSub = "Or put a complete installation into ce_bridge/.";

    private readonly CeReleaseStore _releases;
    private readonly Action<string> _openFolder;
    private readonly ILogger? _logger;

    /// <param name="releases">The module's own store — the row's choices and the door.</param>
    /// <param name="fetcher">The download — one instance for everyone who fetches.</param>
    /// <param name="post">Marshals the progress and the stages onto the UI thread.</param>
    /// <param name="requestCheck">Triggers its own module's check — without it the badge on the
    /// Modules page would stay stuck and the module card would not appear after the user fixed
    /// what was missing.</param>
    /// <param name="openFolder">At runtime <c>DesktopShell.OpenFolder</c>.</param>
    /// <param name="ask">The settings page's confirmation, before the vendor's installer opens.</param>
    /// <param name="isBubblewrapAvailable">Is <c>bwrap</c> on the search path? Asked again
    /// whenever the page is shown.</param>
    /// <param name="chooseProton">The Proton whose Wine runs the installer; null = none.</param>
    /// <param name="runCaged">The Core's cage for the installer.</param>
    /// <param name="logger">Category <c>ce_bridge</c>; null means silent.</param>
    public CeBridgeSettingsViewModel(
        CeReleaseStore releases,
        PackageFetcher fetcher,
        Action<Action> post,
        Action requestCheck,
        Action<string> openFolder,
        Func<ConfirmationRequest, Task<bool>> ask,
        Func<bool> isBubblewrapAvailable,
        Func<CompatTool?> chooseProton,
        Func<CagedInstallRequest, CancellationToken, Task<CagedInstallResult>> runCaged,
        ILogger? logger = null)
    {
        _releases = releases;
        _openFolder = openFolder;
        _logger = logger;

        // Built when the section is: the user is told where to put a release, so the folder has to
        // be there. List() deliberately creates nothing.
        Ensure();

        var getter = new CeReleaseGetter(
            releases,
            new FetchProgressViewModel(fetcher, post, logger),
            post,
            ask,
            isBubblewrapAvailable,
            chooseProton,
            runCaged,
            CeStandardReleaseFiles.Sha256ByPath,
            logger);

        // No file list: a release is a folder or a zip, not a file the user wants to inspect one
        // by one.
        Pick = new PackagePickRowViewModel(
            ReleaseLabel, ReleaseSub, ReleaseBoxName, FolderText,
            emptyNote: CeReleaseGetter.EmptyNote,
            choices: releases,
            vendor: getter,
            showsFileList: false,
            post, requestCheck, logger)
        {
            IsEmptyNoteMuted = true,
        };
    }

    /// <summary>The selection row <c>CE version</c>.</summary>
    public PackagePickRowViewModel Pick { get; }

    /// <summary>The settings page is shown: the row re-reads the folder — there is no watcher, and
    /// whoever just copied a release in the file manager should see it at once.</summary>
    public void OnShown() => Pick.Reload();

    /// <summary>
    /// Creates the store's folder if it is missing. <b>Why the catch:</b>
    /// <see cref="CeReleaseStore.Folder"/> throws an <see cref="IOException"/> when the folder
    /// cannot be created (the data root can sit on a drive that is not mounted); building a
    /// settings page must not die from it. The card then stands with an empty row, which is the
    /// honest answer.
    /// </summary>
    private void Ensure()
    {
        try
        {
            var path = _releases.Folder;
            _logger?.LogDebug("CE release folder ready: {Path}", path);
        }
        catch (Exception exception)
            when (exception is IOException or UnauthorizedAccessException)
        {
            _logger?.LogWarning(exception, "could not create the CE release folder");
        }
    }

    /// <summary>
    /// Opens the store's folder in the file manager. <see cref="CeReleaseStore.Folder"/> creates
    /// it on read, so whoever opens it finds what the subline sends them to. The catch is the same
    /// as for the log folder: a desktop without <c>xdg-open</c> must not crash the settings.
    /// </summary>
    [RelayCommand]
    private void OpenFolder()
    {
        try
        {
            var path = _releases.Folder;
            _openFolder(path);
            _logger?.LogDebug("CE release folder opened: {Path}", path);
        }
        catch (Exception exception)
            when (exception is Win32Exception or InvalidOperationException or IOException
                or UnauthorizedAccessException)
        {
            _logger?.LogWarning(exception, "could not open the CE release folder");
        }
    }
}
