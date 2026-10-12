using Anvil.App.Bridges;
using Anvil.App.Components;
using Anvil.App.ViewModels;
using Anvil.Core.Modules;
using Anvil.Core.Packages;
using Anvil.Core.Prefixes;
using Anvil.Core.Settings;
using Anvil.Core.Steam;
using Microsoft.Extensions.Logging;

namespace Anvil.App.Modules;

/// <summary>
/// The module <b>Wand Bridge</b>.
///
/// <para>
/// <b>Why the shell already in M1:</b> without a module that is <see cref="IsPerGame"/> and
/// not Core, there would be nothing to demonstrate in M1 — both existing modules are Core and
/// cannot be switched off. It costs a few lines and is the vessel M2–M5 grow into.
/// </para>
///
/// <para>
/// <b>Wand Bridge, not Wand:</b> Wand is the application being attached; Wand Bridge is the
/// module that attaches it. The key follows the module name because it doubles as the folder
/// name (S1.3), the log category, and the settings section.
/// </para>
///
/// <para>
/// <b>It lives in <c>Anvil.App</c></b>, like <see cref="ProtonModule"/>: the Core knows only
/// the contract, never a concrete module.
/// </para>
/// </summary>
public sealed class WandBridgeModule : IModule
{
    private readonly ComponentInventory _components;
    private readonly PackageStore _application;
    private readonly PackageFetcher _fetcher;
    private readonly Action<Action> _post;
    private readonly Action _requestReadinessCheck;
    private readonly Action<string> _openFolder;
    private readonly Func<uint, ModuleGameOption, bool> _shownOption;
    private readonly Func<uint, ModuleGameOption, bool> _savedOption;
    private readonly Action<uint, ModuleGameOption, bool> _stageOption;
    private readonly ILogger? _logger;
    private readonly WandApplication _wand;
    private readonly WandCatalog _catalog;
    private readonly LookUpOnlineSetting _lookUpOnline;

    /// <summary>
    /// The module key as a constant — <b>one</b> source for a string needed in several places:
    /// here itself (<see cref="Key"/>), in the Composition Root for binding its own store
    /// (<c>PackageStores.ApplicationFor</c>), and in tests. <see cref="Key"/> now just points
    /// here instead of carrying the string a second time.
    /// </summary>
    public const string ModuleKey = "wand_bridge";

    /// <summary>
    /// The subfolder name of its own store: <c>&lt;data root&gt;/wand_bridge/wand/</c>. The
    /// module names it itself (Spec E-11) — the Core never invents a name for a concrete module
    /// (see the <see cref="PackageStores"/> class comment). Bound in the Composition Root via
    /// <c>PackageStores.ApplicationFor(ModuleKey, ApplicationFolder)</c>.
    /// </summary>
    public const string ApplicationFolder = "wand";

    /// <param name="components">
    /// The one truth about the current version and availability of components (Spec S3.5b
    /// §3.3) — <b>handed over, not built itself</b>: the <c>Dependencies</c> page reads the
    /// same finding this module records. The shared store sits inside it
    /// (<see cref="ComponentInventory.Store"/>); a module that found its own way there would
    /// violate the base rule "never by self-lookup" (Vision §3).
    /// </param>
    /// <param name="application">
    /// Its own store under <see cref="ApplicationFolder"/>. <b>Why separate from the shared
    /// one:</b> decision 9 places dependencies in the shared store, the application with the
    /// module — two bridges each demanding their own copy of the same installers would be two
    /// truths about one thing, but the Wand application itself belongs only to this one module.
    /// </param>
    /// <param name="fetcher">
    /// The download with which the <c>Wand version</c> selection row fetches the vendor's
    /// builds (Spec S3.5b §3.7) — the one instance from the Composition Root, handed over like
    /// everything else.
    /// </param>
    /// <param name="post">Marshals the fetch progress onto the UI thread.</param>
    /// <param name="requestReadinessCheck">
    /// Triggers its own readiness check — <b>the coupling</b> (Spec §3.7): when the user picks
    /// a package in the settings card, the finding must be re-run so the badge and module card
    /// flip.
    ///
    /// <para>
    /// <b>Why a delegate and not the <see cref="ModuleRegistry"/>:</b> the module and its
    /// section must be able to trigger exactly <i>one</i> thing. Whoever holds the registry can
    /// flip other modules' switches. It is bound in the Composition Root as a lambda that only
    /// fetches the registry on <i>call</i> — it is built from the modules and does not exist yet
    /// at this module's construction time; the same construction as <c>ProtonModule</c> with its
    /// <c>Func&lt;ModuleRegistry&gt;</c>.
    /// </para>
    /// </param>
    /// <param name="openFolder">
    /// Opens a folder in the file manager — at runtime <c>DesktopShell.OpenFolder</c>, as with
    /// the log folder (S3.7). <b>Handed over, not called directly:</b> a module knows no UI and
    /// no desktop.
    /// </param>
    /// <param name="moduleFolder">
    /// <c>&lt;data root&gt;/wand_bridge/</c>, as a delegate (architecture.md §4.1) — the user can
    /// change the data root at runtime. The module builds its <see cref="WandApplication"/> from
    /// it: that belongs to it, like its settings section, and is therefore built here rather
    /// than handed over.
    /// </param>
    /// <param name="shownOption">
    /// What the card section shows: the pending choice, otherwise the saved value
    /// (<see cref="ModuleGameOptions.Displayed"/>) — narrowed to the module's own key.
    ///
    /// <para>
    /// <b>Why three narrowed delegates and not <see cref="ModuleGameOptions"/> itself</b>
    /// (Spec E-8): the same coupling concern as for <paramref name="requestReadinessCheck"/>
    /// above — if this module held the service as an object, it could reach the switches of
    /// <b>other</b> modules (architecture.md §4.2). Each delegate can do exactly one thing: read
    /// what is displayed; read what applies; stage a pending choice — all three bound fixedly to
    /// <see cref="ModuleKey"/>.
    /// </para>
    /// </param>
    /// <param name="savedOption">
    /// What applies, without the pending choice (<see cref="ModuleGameOptions.Saved"/>) — asked
    /// by the chip in <see cref="ChipFor"/>: attaching only happens after saving, a merely
    /// checked box does not yet change the badge. Also read by
    /// <see cref="PrepareAttachAsync"/> for "Open this game in Wand" (s7.1).
    /// </param>
    /// <param name="stageOption">
    /// Stages a pending choice (<see cref="ModuleGameOptions.Stage"/>) — called by the card
    /// section when the user checks a box.
    /// </param>
    /// <param name="fetchDocument">
    /// The narrowed network access of architecture.md §4.3 — the module can fetch a document
    /// conditionally and nothing else; it refreshes Wand's catalog with it (story s6.1).
    /// </param>
    /// <param name="logger">Category <see cref="Key"/>; null means silent.</param>
    public WandBridgeModule(
        ComponentInventory components,
        PackageStore application,
        PackageFetcher fetcher,
        Action<Action> post,
        Action requestReadinessCheck,
        Action<string> openFolder,
        Func<string> moduleFolder,
        Func<uint, ModuleGameOption, bool> shownOption,
        Func<uint, ModuleGameOption, bool> savedOption,
        Action<uint, ModuleGameOption, bool> stageOption,
        FetchDocument fetchDocument,
        ILogger? logger = null)
    {
        _components = components;
        _application = application;
        _fetcher = fetcher;
        _post = post;
        _requestReadinessCheck = requestReadinessCheck;
        _openFolder = openFolder;
        _shownOption = shownOption;
        _savedOption = savedOption;
        _stageOption = stageOption;
        _logger = logger;
        _wand = new WandApplication(application, moduleFolder, logger);
        _catalog = new WandCatalog(fetchDocument, moduleFolder, TimeProvider.System, logger);
        _lookUpOnline = new LookUpOnlineSetting(new ModuleSettings(moduleFolder, logger), logger);
    }

    public string Key => ModuleKey;

    public string DisplayName => "Wand Bridge";

    public string Description => "Attaches Wand to the running process.";

    public bool IsCore => false;

    public bool IsPerGame => true;

    /// <summary>
    /// The module card section (Spec S5.4 §3.6): the three per-game options "Attach on game
    /// launch", "Quit Wand with the game" and "Open this game in Wand". The Core draws the
    /// card's frame (S1.4); this module only supplies the content — the same division of labor
    /// as in <see cref="CreateSettingsSection"/>.
    /// </summary>
    public object? CreateSection(GameProfile game)
        => new BridgeSectionViewModel(game.Game.AppId, "Wand", true, _shownOption, _stageOption);

    /// <summary>
    /// The sentence for when no application is selected (Spec S3.5b §3.4, check 1). As a
    /// constant so tests can reach it verbatim.
    /// </summary>
    public const string ApplicationMissingReason =
        "The app package is missing — pick it in Settings → Wand Bridge.";

    /// <summary>
    /// The sentence for when a component of the current version is missing (Spec S3.5b §3.4,
    /// E-12): it points to the <c>Dependencies</c> page, from which the component can be
    /// fetched with a click — before S3.5b it pointed to
    /// <c>docs/referenz/pakete-packen.md</c>.
    /// </summary>
    /// <param name="ingredientDisplayName">
    /// <see cref="RecipeIngredient.DisplayName"/> — meant as part of a sentence (S3.5 §3.3),
    /// here at the start of the sentence, hence capitalized: "the .NET 4.0 installer" →
    /// "The .NET 4.0 installer".
    /// </param>
    public static string IngredientMissingReason(string ingredientDisplayName)
        => $"{Capitalize(ingredientDisplayName)} is missing — get it in Settings → Dependencies.";

    /// <summary>
    /// Wand Bridge's readiness finding (Spec S3.5b §3.4): ready when a file is selected in its
    /// own store <b>and</b> the current version of <see cref="Dotnet48"/> is complete in the
    /// shared store; otherwise the sentence for the missing thing — checked in this order.
    ///
    /// <para>
    /// <b>Checked is <see cref="PackageStore.Selected"/>, not <see cref="PackageStore.List"/></b>
    /// (S3.4 E-12): something lying in the folder does not mean the user has selected it.
    /// <c>Selected</c> already reconciles against the list (S3.3, E-4); a selected but vanished
    /// file therefore counts as missing on its own.
    /// </para>
    ///
    /// <para>
    /// <b>The shared store's selection is no longer checked</b> (Spec S3.5b §3.4): the
    /// deliberate redundancy from S3.5a E-5 has lapsed because the <c>Dependencies</c> selection
    /// row gave way to the <c>Dependencies</c> page. The saved selection stays in the settings
    /// file and is no longer read — reading must not write.
    /// </para>
    ///
    /// <para>
    /// <b>Components are asked for by the finding via <see cref="ComponentInventory.CheckAsync"/></b>
    /// (Spec S3.5b §3.3): the current version against the saved choice, detection by name, then
    /// size and checksum. The result then lives in the inventory, where the page reads it — one
    /// truth instead of two calculations. The <b>first</b> missing component is named, in
    /// recipe order. Without a recipe there are no missing components
    /// (<see cref="ComponentAvailabilityState.NotBuildable"/>); this method does not penalize
    /// that.
    /// </para>
    ///
    /// <para>
    /// <b>Now with <c>await</c>:</b> detection runs on the thread pool because stage 2 reads up
    /// to 120 MB (S3.5a §3.4). The asynchronous contract from S3.4 carries this without change.
    /// </para>
    /// </summary>
    public async Task<ModuleReadiness> CheckReadinessAsync(CancellationToken cancellationToken)
    {
        if (_application.Selected is null)
        {
            return ModuleReadiness.NotReady(ApplicationMissingReason);
        }

        var availability = await _components.CheckAsync(Dotnet48.Component, cancellationToken);
        var missing = availability.Missing;
        return missing.Count > 0
            ? ModuleReadiness.NotReady(IngredientMissingReason(missing[0].Ingredient.DisplayName))
            : ModuleReadiness.Ready;
    }

    private static string Capitalize(string text)
        => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];

    /// <summary>
    /// This module's settings card: the <c>Wand version</c> selection row, the switch
    /// <c>Look up games online</c> (story s7.2), and the <c>Storage location</c> action row. The
    /// Core draws the frame, this module supplies the content.
    ///
    /// <para>
    /// <b>Only its own store now</b> (Spec S3.5b §3.4, E-10): the dependencies have their own
    /// page with their own door, so <c>Open folder</c> here opens exactly
    /// <c>&lt;data root&gt;/wand_bridge/wand/</c>.
    /// </para>
    ///
    /// <para>
    /// <b>The section does not get the registry</b> (S3.4 §3.7), but the narrow check delegate
    /// from the constructor — it should be able to trigger exactly one thing.
    /// </para>
    ///
    /// <para>
    /// <b>The module hands over the vendor builds</b> (<see cref="PackageCatalog.Wand"/>, Spec
    /// S3.5b §3.7): it is the module that knows them — the catalog has no registry (class
    /// comment of <see cref="PackageCatalog"/>).
    /// </para>
    /// </summary>
    public object? CreateSettingsSection()
        => new WandBridgeSettingsViewModel(
            _application, PackageCatalog.Wand, _fetcher, _post, _requestReadinessCheck, _openFolder,
            _lookUpOnline, _logger);

    public string? IconKey => "IconModuleWand";

    /// <summary>
    /// Exactly one contribution, independent of the game — the argument therefore stays unused,
    /// and that is the answer, not a shortcoming (design answer to 32 a). Every bridge makes
    /// the same one (<see cref="LauncherService"/>): Steam then starts the game with the
    /// launcher service in the container, which attaching uses.
    /// </summary>
    public IReadOnlyList<LaunchOptionContribution> LaunchOptionsFor(GameProfile game)
        => [LauncherService.For(Key)];

    /// <summary>
    /// The one component this module needs in the prefix: .NET 4.8 (S3.2).
    ///
    /// <para>
    /// <b>The same for every game</b> — as with the launch options contribution already; the
    /// argument therefore stays unused, and that is the answer, not a shortcoming. Whether a
    /// prefix can even exist for this game at all is answered by the Core (S3.1), not the
    /// module.
    /// </para>
    ///
    /// <para>
    /// <b>The same list as <see cref="RequiredComponents"/></b> (Spec S3.5b §3.1): the
    /// redundancy from S3.4 is gone — the global need is the one truth here, nothing different
    /// is demanded per game.
    /// </para>
    /// </summary>
    public IReadOnlyList<PrefixComponent> PrefixComponentsFor(GameProfile game) => RequiredComponents;

    /// <summary>
    /// Which components this module needs at all — globally, without a game (Spec S3.5b E-1):
    /// .NET 4.8. The <c>Dependencies</c> page asks here, not at
    /// <see cref="PrefixComponentsFor"/>.
    /// </summary>
    public IReadOnlyList<PrefixComponent> RequiredComponents { get; } = [Dotnet48.Component];

    /// <summary>
    /// What Wand Bridge wants started in the container: the unpacked application from the
    /// module folder (Spec S4.4 §3.2) — and, when "Open this game in Wand" is saved for this game
    /// and the catalog maps it, the address that opens Wand on it (story s7.1).
    ///
    /// <para>
    /// <b>Without the option, no network</b> (spec s7.1 E-2): a copy nobody reads would be a
    /// network access without a click and without a purpose. The saved value decides, not the
    /// displayed one — a ticked but unsaved box must not change the next attach (E-3).
    /// </para>
    ///
    /// <para>
    /// <b>On the thread pool</b>, because the first unpacking writes about 160 MB. The same
    /// reasoning as in <see cref="CheckReadinessAsync"/>, where detection reads up to 120 MB.
    /// </para>
    ///
    /// <para>
    /// The network is used only when "Look up games online" is on as well (story s7.2).
    /// </para>
    ///
    /// <para>
    /// <b>Never asks <paramref name="findGameProcessId"/></b>: Wand opens a game by its catalog
    /// address, not by a process (spec s10.1 #12).
    /// </para>
    /// </summary>
    public async Task<AttachDeclaration> PrepareAttachAsync(
        GameProfile game,
        Func<CancellationToken, Task<int?>> findGameProcessId,
        CancellationToken cancellationToken)
    {
        var appId = game.Game.AppId;
        if (!_savedOption(appId, ModuleGameOption.OpenOnGame))
        {
            _logger?.LogInformation(
                "Wand opens on its home page: \"{Option}\" is off for {AppId}",
                BridgeSectionViewModel.OpenOnGameLabelFor("Wand"), appId);
            return await Task.Run(() => _wand.Prepare(cancellationToken), cancellationToken);
        }

        // Runs alongside unpacking, so a refresh on a healthy network costs the attach nothing.
        var refresh = RefreshIfAllowed();
        var declaration = await Task.Run(() => _wand.Prepare(cancellationToken), cancellationToken);

        // Looked up only when Wand will actually start (spec s7.1 E-6).
        if (declaration.State != AttachDeclarationState.Program)
        {
            return declaration;
        }

        var address = await AddressForAsync(appId, refresh);
        if (address is null)
        {
            return declaration;
        }

        _logger?.LogInformation("Wand opens on the game: {Address}", address);
        return AttachDeclaration.Start(declaration.Program! with { Arguments = [address] });
    }

    /// <summary>
    /// Refreshes the catalog unless "Look up games online" is off (story s7.2). Off blocks only
    /// the network: a copy already on disk is still looked up (spec E-3), and with none there is
    /// no running refresh, so the lookup does not wait.
    /// </summary>
    private Task RefreshIfAllowed()
    {
        if (_lookUpOnline.IsOn)
        {
            return _catalog.RefreshAsync();
        }

        _logger?.LogInformation(
            "Wand catalog: not refreshed, \"{Setting}\" is off", WandBridgeSettingsViewModel.LookUpOnlineLabel);
        return Task.CompletedTask;
    }

    private async Task<string?> AddressForAsync(uint appId, Task refresh)
    {
        var title = await _catalog.LookupAsync(appId, refresh);
        if (title is null)
        {
            return null;
        }

        var address = WandGameAddress.For(title);
        if (address is null)
        {
            _logger?.LogWarning("Wand catalog: mapping for {AppId} is malformed, no mapping", appId);
        }

        return address;
    }

    /// <summary>The application's process images — the module passes through what
    /// <see cref="WandApplication"/> knows.</summary>
    public IReadOnlyList<string> AttachProcessImages => WandApplication.ProcessImages;

    /// <summary>
    /// <c>false</c>: WeMod hides its window on the polite request and keeps running — measured in
    /// s9.2 (#21) and again on 2026-10-02, when all its processes outlasted the 15 s grace. Asking
    /// would only delay the detach, so the core forces it at once (s10.2 E-5).
    /// </summary>
    public bool ClosesOnRequest => false;

    /// <summary>
    /// The login folder — the module passes through what <see cref="WandApplication"/> knows,
    /// as already with <see cref="AttachProcessImages"/>.
    /// </summary>
    public IReadOnlyList<AttachStateFolder> AttachStateFolders => [_wand.LoginState];

    /// <summary>
    /// What the game card shows about <b>Wand</b> (Spec S5.3 §3.4).
    ///
    /// <para>
    /// <b>"Wand", not "Wand Bridge":</b> the chip carries the application's state, not the
    /// module's — the card shows whether Wand is ready for this game; the module is only the
    /// path there (element description §3). <see cref="DisplayName"/> is therefore explicitly
    /// not the source.
    /// </para>
    ///
    /// <para>
    /// <b>The third state <c>· auto</c></b> (Spec S5.4 §3.6): it hangs on the per-game switch
    /// "Attach on game launch" and asks what <b>applies</b> — <see cref="ModuleGameOptions.Saved"/>,
    /// not <see cref="ModuleGameOptions.Displayed"/>. Attaching only happens after saving; a
    /// checked but not yet saved box must not flip the chip, or the badge would promise
    /// something that does not yet apply.
    /// </para>
    ///
    /// <para>
    /// <c>default</c> is unreachable: the Core only asks for <c>Ready</c> and <c>Attached</c>
    /// (§3.3). The line stays anyway, because an enum can grow and a silent fallthrough would
    /// be worse than a missing chip.
    /// </para>
    /// </summary>
    public ModuleChip? ChipFor(GameProfile game, ModuleGameState state) => state.Status switch
    {
        ModuleGameStatus.Attached => new ModuleChip("Wand · running", ModuleChipTone.Attached),
        ModuleGameStatus.Ready => new ModuleChip(
            _savedOption(game.Game.AppId, ModuleGameOption.AttachOnLaunch)
                ? "Wand · auto"
                : "Wand · ready",
            ModuleChipTone.Accent),
        _ => null,
    };

    /// <summary>
    /// The card's button for <b>Wand</b> (Spec S5.4 §3.1): attach while ready; detach while
    /// attached.
    ///
    /// <para>
    /// <b>"ATTACH WAND" / "DETACH WAND", not the module name:</b> the button speaks of the
    /// application, not the module — the same rule as in <see cref="ChipFor"/>.
    /// </para>
    ///
    /// <para>
    /// <c>default</c> is unreachable: the Core only asks for <c>Ready</c> and <c>Attached</c>
    /// (contract comment on <c>IModule.ActionFor</c>). The line stays anyway, for the same
    /// reason as in <see cref="ChipFor"/>.
    /// </para>
    /// </summary>
    public ModuleAction? ActionFor(GameProfile game, ModuleGameState state) => state.Status switch
    {
        ModuleGameStatus.Attached => new ModuleAction("DETACH WAND", ModuleActionKind.Detach),
        ModuleGameStatus.Ready => new ModuleAction("ATTACH WAND", ModuleActionKind.Attach),
        _ => null,
    };
}
