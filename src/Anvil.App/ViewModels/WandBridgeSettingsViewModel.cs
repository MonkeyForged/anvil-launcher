using System.ComponentModel;
using Anvil.App.Modules;
using Anvil.Core.Packages;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace Anvil.App.ViewModels;

/// <summary>
/// The settings section of Wand Bridge (S3.4 §3.6, S3.5b §3.4): the application selection row
/// and the action row that opens its folder. <b>The surface where the user fixes a finding</b> —
/// as far as it concerns the application; missing components are fixed on the <c>Dependencies</c>
/// surface.
///
/// <para>
/// <b>The Core draws the card's frame</b> — chevron, title, key, badge
/// (<see cref="ModuleSettingsCardViewModel"/>); this ViewModel only supplies the content. The
/// same division of labor as with the module card of the right column (S1.4).
/// </para>
///
/// <para>
/// <b>It does not get the <see cref="Anvil.Core.Modules.ModuleRegistry"/></b> (Spec §3.7), but a
/// narrow delegate: the section must be able to trigger exactly <i>one</i> thing — the check of
/// its own module. A module section holding the registry could flip other modules' switches.
/// </para>
///
/// <para>
/// <b>The <c>Dependencies</c> selection row is gone</b> (Spec S3.5b §3.4): the shared store has
/// its own surface, and nobody reads its selection anymore. What remains is a selection row with
/// state, <see cref="Pick"/> (Spec S3.5b §3.7).
/// </para>
/// </summary>
public sealed partial class WandBridgeSettingsViewModel : ObservableObject, IShowable
{
    /// <summary>
    /// The row texts sit as constants on the ViewModel, not as strings in the <c>.axaml</c>
    /// (Plan P-6): no test would reach them there. Verbatim from Spec S3.5b §3.7 (Plan §8.2).
    /// </summary>
    public const string ApplicationLabel = "Wand version";

    /// <inheritdoc cref="ApplicationLabel"/>
    public const string ApplicationSub = "Which build of your program to attach. Anvil cannot know this — it asks.";

    /// <inheritdoc cref="ApplicationLabel"/>
    public const string ApplicationBoxName = "Wand application";

    /// <summary>The folder, as the selection row's sentences name it — relative, not the full
    /// path (the same trade-off as in <see cref="StorageSub"/>).</summary>
    public const string ApplicationFolderText = "wand_bridge/wand/";

    /// <inheritdoc cref="ApplicationLabel"/>
    public const string StorageLabel = "Storage location";

    /// <summary>
    /// The subline names <b>only the target folder</b>, not the full path (design follow-up of
    /// 2026-09-13): that stands under <c>Data location</c> and would be a second place to keep in
    /// sync here.
    ///
    /// <para>
    /// <b>Only one folder since S3.5b</b> (E-10): every door opens its own folder. Before that,
    /// the sentence named <c>dependencies/</c> and <c>wand_bridge/wand/</c>, because one button
    /// had to reach both stores.
    /// </para>
    /// </summary>
    public const string StorageSub = "Put the app package into wand_bridge/wand/.";

    /// <inheritdoc cref="ApplicationLabel"/>
    public const string LookUpOnlineLabel = "Look up games online";

    /// <inheritdoc cref="ApplicationLabel"/>
    public const string LookUpOnlineSub =
        "Anvil downloads Wand's public game list so Wand can open on the game you are playing.";

    private readonly PackageStore _application;
    private readonly Action<string> _openFolder;
    private readonly LookUpOnlineSetting _lookUpOnlineSetting;
    private readonly ILogger? _logger;

    [ObservableProperty]
    private bool _lookUpOnline;

    /// <param name="application">The module's own store — the selection row and the door.</param>
    /// <param name="vendorBuilds">
    /// The vendor's builds (at runtime <c>PackageCatalog.Wand</c>) that the selection row can
    /// fetch (Spec S3.5b §3.7).
    /// </param>
    /// <param name="fetcher">The download — one instance for everyone who fetches (Composition
    /// Root).</param>
    /// <param name="post">Marshals the progress onto the UI thread.</param>
    /// <param name="requestCheck">
    /// Triggers its own module's check — <b>the coupling</b> (Spec §3.7): without it the badge on
    /// the Modules page would stay stuck, and the module card of the right column would not
    /// appear even though the user just fixed what was missing.
    /// </param>
    /// <param name="openFolder">At runtime <c>DesktopShell.OpenFolder</c>, as with the log
    /// folder.</param>
    /// <param name="lookUpOnline">The switch "Look up games online" (story s7.2) — read at
    /// construction and again in <see cref="OnShown"/>, written back at once on every
    /// change.</param>
    /// <param name="logger">Category <c>wand_bridge</c>; null means silent.</param>
    public WandBridgeSettingsViewModel(
        PackageStore application,
        IReadOnlyList<CatalogEntry> vendorBuilds,
        PackageFetcher fetcher,
        Action<Action> post,
        Action requestCheck,
        Action<string> openFolder,
        LookUpOnlineSetting lookUpOnline,
        ILogger? logger = null)
    {
        _application = application;
        _openFolder = openFolder;
        _lookUpOnlineSetting = lookUpOnline;
        _logger = logger;
        LookUpOnline = lookUpOnline.IsOn;

        // The store is created here, when the section is built — a fix from S3.4's live
        // verification (2026-09-15). List() deliberately creates nothing (S3.3); building the
        // section is the moment the user gets the prompt to read, to drop packages into it.
        Ensure(application);

        // The row gets everything Wand has (s9.1 I-3): its store as the choices and as the target
        // of the vendor builds, the empty sentence and the file list — the behaviour of S3.5b.
        // Wand obtains a vendor build by fetching it with its checksum (s9.3 E-11).
        Pick = new PackagePickRowViewModel(
            ApplicationLabel, ApplicationSub, ApplicationBoxName, ApplicationFolderText,
            emptyNote: PackagePickRowViewModel.EmptyFolderNoteFor(ApplicationFolderText),
            choices: application,
            vendor: new FetchedVendorBuilds(
                application, vendorBuilds, ApplicationFolderText,
                new FetchProgressViewModel(fetcher, post, logger)),
            showsFileList: true,
            post, requestCheck, logger);
    }

    /// <summary>The selection row <c>Wand version</c> (Spec S3.5b §3.7).</summary>
    public PackagePickRowViewModel Pick { get; }

    /// <summary>
    /// The settings page is shown: the selection row re-reads the folder (Spec S3.5b §3.7) —
    /// there is no watcher (S3.3), and whoever just copied a file in the file manager should see
    /// it without first expanding the list. The switch is re-read the same way (spec E-5): the
    /// file can change while the page is not open.
    /// </summary>
    public void OnShown()
    {
        Pick.Reload();
        LookUpOnline = _lookUpOnlineSetting.IsOn;
    }

    /// <summary>Writes the switch back at once (spec s7.2 E-2), like the Core card's switches: the
    /// save bar is for changes that land in Steam, and this one does not touch Steam.</summary>
    partial void OnLookUpOnlineChanged(bool value) => _lookUpOnlineSetting.Set(value);

    /// <summary>
    /// Creates a store's folder if it is missing. More than reading
    /// <see cref="PackageStore.Folder"/> is not needed — the store creates itself on access
    /// (S3.3).
    ///
    /// <para>
    /// <b>Why the catch:</b> <see cref="PackageStore.Folder"/> deliberately throws an
    /// <see cref="IOException"/> when the folder cannot be created (S3.3, Plan P-3) — the data
    /// root can sit on an external drive that is not mounted. Whoever cannot store anything
    /// should learn that; but <b>building a settings page must not die from it</b>. The same
    /// trade-off as in <see cref="PackageStore.List"/>, which for the same reason returns
    /// "nothing" instead of throwing. The card then stands with an empty selection row, and that
    /// is the honest answer.
    /// </para>
    ///
    /// <para>
    /// The path is read <b>into its own variable</b> and not passed directly to
    /// <c>LogDebug</c>: with <c>_logger is null</c> the argument would never be evaluated — and
    /// then this method would create nothing at all.
    /// </para>
    /// </summary>
    private void Ensure(PackageStore store)
    {
        try
        {
            var path = store.Folder;
            _logger?.LogDebug("package folder ready: {Path}", path);
        }
        catch (Exception exception)
            when (exception is IOException or UnauthorizedAccessException)
        {
            _logger?.LogWarning(exception, "could not create a package folder");
        }
    }

    /// <summary>
    /// Opens the application's folder in the file manager (Spec S3.5b E-10) —
    /// <c>&lt;data root&gt;/wand_bridge/wand/</c>. <see cref="PackageStore.Folder"/> creates it on
    /// read, so whoever opens it finds what the subline sends them to. <b>The catch is the same
    /// as for the log folder</b> (<see cref="LogViewModel"/>): a desktop without <c>xdg-open</c>
    /// must not crash the settings.
    /// </summary>
    [RelayCommand]
    private void OpenFolder()
    {
        try
        {
            var path = _application.Folder;
            _openFolder(path);
            _logger?.LogDebug("application folder opened: {Path}", path);
        }
        catch (Exception exception)
            when (exception is Win32Exception or InvalidOperationException or IOException
                or UnauthorizedAccessException)
        {
            _logger?.LogWarning(exception, "could not open the application folder");
        }
    }
}
