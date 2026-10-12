using System.Globalization;
using Anvil.App.Bridges;
using Anvil.App.ViewModels;
using Anvil.Core.Installers;
using Anvil.Core.Modules;
using Anvil.Core.Packages;
using Anvil.Core.Prefixes;
using Anvil.Core.Steam;
using Microsoft.Extensions.Logging;

namespace Anvil.App.Modules;

/// <summary>
/// The module <b>CE Bridge</b>: the second bridge module, built like <see cref="WandBridgeModule"/>.
///
/// <para>
/// <b>It finds, offers and attaches:</b> it says whether a CE release is placed and chosen, lets
/// the user place and choose one (s9.1) or get the standard release from the vendor (s9.3,
/// <see cref="CeReleaseGetter"/>), and answers every question of attaching (s9.2) — the
/// card's content, chip and button, the launch option, and the program to start, which is the
/// chosen release taken over into <c>app/</c> (<see cref="CeApplication"/>). The same for every
/// game: CE attaches to whichever process the program is started in.
/// </para>
///
/// <para>
/// <b>It lives in <c>Anvil.App</c></b>, like the other modules: the Core knows only the contract,
/// never a concrete module.
/// </para>
/// </summary>
public sealed class CeBridgeModule : IModule
{
    /// <summary>
    /// The module key as a constant — one source for a string needed in several places: here
    /// (<see cref="Key"/>), in the Composition Root for the module folder and the log category,
    /// and in the tests. It doubles as the folder name, so it follows the module name.
    /// </summary>
    public const string ModuleKey = "ce_bridge";

    /// <summary>
    /// What starts out "on" for CE (s10.1): opening the game in CE — the user attaches CE to a
    /// game to look at it. Handed to <see cref="ModuleGameOptions"/> by the Composition Root.
    /// </summary>
    public static IReadOnlySet<ModuleGameOption> OptionsOnByDefault { get; }
        = new HashSet<ModuleGameOption> { ModuleGameOption.OpenOnGame };

    /// <summary>Nothing complete lies in the store. Names the door the user fixes it at.</summary>
    public const string NotInstalledReason = "CE is not installed yet — get it in Settings → CE Bridge.";

    /// <summary>Something lies in the store, but the user has not chosen it (or what they chose is
    /// gone).</summary>
    public const string NothingSelectedReason = "No CE version selected — pick one in Settings → CE Bridge.";

    private readonly CeReleaseStore _releases;
    private readonly PackageFetcher _fetcher;
    private readonly Action<Action> _post;
    private readonly Action _requestReadinessCheck;
    private readonly Action<string> _openFolder;
    private readonly Func<ConfirmationRequest, Task<bool>> _ask;
    private readonly Func<bool> _isBubblewrapAvailable;
    private readonly Func<CompatTool?> _chooseProton;
    private readonly Func<CagedInstallRequest, CancellationToken, Task<CagedInstallResult>> _runCaged;
    private readonly Func<uint, ModuleGameOption, bool> _shownOption;
    private readonly Func<uint, ModuleGameOption, bool> _savedOption;
    private readonly Action<uint, ModuleGameOption, bool> _stageOption;
    private readonly ILogger? _logger;
    private readonly CeApplication _application;

    /// <param name="releases">The module's own store; the finding and the settings card read it.</param>
    /// <param name="fetcher">The download — one instance for everyone who fetches (Composition
    /// Root). Handed on to the settings card.</param>
    /// <param name="post">Marshals the progress onto the UI thread.</param>
    /// <param name="requestReadinessCheck">
    /// Triggers exactly this module's check — the coupling: a choice in the settings card flips the
    /// badge and the module card. The module never holds the registry itself (architecture.md §4.2).
    /// </param>
    /// <param name="openFolder">At runtime <c>DesktopShell.OpenFolder</c>, as with the log
    /// folder.</param>
    /// <param name="ask">The settings page's confirmation (s9.3 I-3) — the module asks, the Core
    /// draws.</param>
    /// <param name="isBubblewrapAvailable">Is <c>bwrap</c> on the search path?</param>
    /// <param name="chooseProton">The Proton whose Wine runs the vendor's installer; null = none.</param>
    /// <param name="runCaged">The Core's cage for a foreign installer. These four only travel on to
    /// the settings card, where CE is obtained.</param>
    /// <param name="shownOption">What the card section shows: the pending choice, otherwise the
    /// saved value — narrowed to this module's key, as at <see cref="WandBridgeModule"/> (a module
    /// must not reach another module's switches).</param>
    /// <param name="savedOption">What applies, without the pending choice — asked by the chip: a
    /// merely ticked box does not yet change the badge.</param>
    /// <param name="stageOption">Stages a pending choice — called by the card section when the
    /// user ticks a box.</param>
    /// <param name="logger">Category <see cref="Key"/>; null means silent.</param>
    public CeBridgeModule(
        CeReleaseStore releases,
        PackageFetcher fetcher,
        Action<Action> post,
        Action requestReadinessCheck,
        Action<string> openFolder,
        Func<ConfirmationRequest, Task<bool>> ask,
        Func<bool> isBubblewrapAvailable,
        Func<CompatTool?> chooseProton,
        Func<CagedInstallRequest, CancellationToken, Task<CagedInstallResult>> runCaged,
        Func<uint, ModuleGameOption, bool> shownOption,
        Func<uint, ModuleGameOption, bool> savedOption,
        Action<uint, ModuleGameOption, bool> stageOption,
        ILogger? logger = null)
    {
        _releases = releases;
        _fetcher = fetcher;
        _post = post;
        _requestReadinessCheck = requestReadinessCheck;
        _openFolder = openFolder;
        _ask = ask;
        _isBubblewrapAvailable = isBubblewrapAvailable;
        _chooseProton = chooseProton;
        _runCaged = runCaged;
        _shownOption = shownOption;
        _savedOption = savedOption;
        _stageOption = stageOption;
        _logger = logger;
        _application = new CeApplication(releases, logger);
    }

    public string Key => ModuleKey;

    public string DisplayName => "CE Bridge";

    public string Description => "Attaches CE to the running process.";

    public bool IsCore => false;

    public bool IsPerGame => true;

    public string? IconKey => "IconModuleCe";

    /// <summary>
    /// The finding: ready when a release is chosen. Otherwise the reason that tells what is
    /// missing — nothing placed at all, or placed but not chosen.
    ///
    /// <para>
    /// <b>Checked is <see cref="CeReleaseStore.Selected"/>, not the list:</b> something lying in
    /// the folder is not chosen. <c>Selected</c> already reconciles against the list, so a chosen
    /// but vanished release counts as nothing chosen.
    /// </para>
    ///
    /// <para>
    /// <b>On the thread pool:</b> listing opens zips and reads folders; the contract's asynchronous
    /// check keeps that off the UI thread.
    /// </para>
    /// </summary>
    public Task<ModuleReadiness> CheckReadinessAsync(CancellationToken cancellationToken)
        => Task.Run(Check, cancellationToken);

    private ModuleReadiness Check()
    {
        var readiness = _releases.Selected is not null ? ModuleReadiness.Ready
            : _releases.List().Count == 0 ? ModuleReadiness.NotReady(NotInstalledReason)
            : ModuleReadiness.NotReady(NothingSelectedReason);

        _logger?.LogInformation(
            "CE readiness: {Result}",
            readiness.IsReady ? "ready" : "not ready — " + readiness.Reason);
        return readiness;
    }

    /// <summary>This module's settings card: the release selection row with <c>Get CE 7.7</c> and
    /// the door to its folder. The Core draws the frame.</summary>
    public object? CreateSettingsSection()
        => new CeBridgeSettingsViewModel(
            _releases, _fetcher, _post, _requestReadinessCheck, _openFolder,
            _ask, _isBubblewrapAvailable, _chooseProton, _runCaged, _logger);

    /// <summary>
    /// The card section: "Attach on game launch", "Open this game in CE" (on by default, see
    /// <see cref="OptionsOnByDefault"/>) and "Quit CE with the game".
    /// </summary>
    public object? CreateSection(GameProfile game)
        => new BridgeSectionViewModel(game.Game.AppId, "CE", true, _shownOption, _stageOption);

    /// <summary>
    /// The one contribution every bridge makes (<see cref="LauncherService"/>), independent of
    /// the game — the argument stays unused, and that is the answer, not a shortcoming.
    /// </summary>
    public IReadOnlyList<LaunchOptionContribution> LaunchOptionsFor(GameProfile game)
        => [LauncherService.For(Key)];

    /// <summary>CE needs nothing in the prefix: it is a program that runs next to the game.</summary>
    public IReadOnlyList<PrefixComponent> PrefixComponentsFor(GameProfile game) => [];

    public IReadOnlyList<PrefixComponent> RequiredComponents { get; } = [];

    /// <summary>
    /// The chosen release taken over into <c>app/</c> and named as the program to start. With
    /// "Open this game in CE" saved on, the program gets <c>--OPENPID &lt;pid&gt;</c> so CE opens
    /// the game's process by itself; the autorun script that names it is placed either way.
    ///
    /// <para>
    /// <b>On the thread pool:</b> the first copy of a release writes a whole Cheat Engine folder.
    /// The same reasoning as at <see cref="WandBridgeModule.PrepareAttachAsync"/>.
    /// </para>
    /// </summary>
    public async Task<AttachDeclaration> PrepareAttachAsync(
        GameProfile game,
        Func<CancellationToken, Task<int?>> findGameProcessId,
        CancellationToken cancellationToken)
    {
        var declaration = await Task.Run(() => _application.Prepare(cancellationToken), cancellationToken);
        if (declaration.Program is not { } program)
        {
            return declaration;
        }

        _logger?.LogInformation(
            "CE starts {Program} in {Folder}", program.ExecutablePath, program.WorkingDirectory);
        CeOpenGameScript.Ensure(program.WorkingDirectory, _logger);

        var appId = game.Game.AppId;
        if (!_savedOption(appId, ModuleGameOption.OpenOnGame))
        {
            _logger?.LogInformation(
                "CE opens without a process: \"{Option}\" is off for {AppId}",
                BridgeSectionViewModel.OpenOnGameLabelFor("CE"), appId);
            return declaration;
        }

        var processId = await findGameProcessId(cancellationToken);
        if (processId is not { } pid)
        {
            _logger?.LogInformation("CE opens without a process: none found for {AppId}", appId);
            return declaration;
        }

        _logger?.LogInformation("CE opens the game with process id {ProcessId}", pid);
        return AttachDeclaration.Start(
            program with { Arguments = ["--OPENPID", pid.ToString(CultureInfo.InvariantCulture)] });
    }

    /// <summary>The application's process images — passed through from <see cref="CeApplication"/>.</summary>
    public IReadOnlyList<string> AttachProcessImages => CeApplication.ProcessImages;

    /// <summary>CE keeps no state that travels between prefixes.</summary>
    public IReadOnlyList<AttachStateFolder> AttachStateFolders { get; } = [];

    /// <summary>
    /// What the game card shows about <b>CE</b> — the application's state, not the module's.
    /// "auto" hangs on the <i>saved</i> "Attach on game launch": attaching only happens after
    /// saving, so a merely ticked box must not flip the badge. <c>default</c> is unreachable (the
    /// Core only asks for <c>Ready</c> and <c>Attached</c>) but stays, because a silent fallthrough
    /// on a grown enum would be worse than a missing chip.
    /// </summary>
    public ModuleChip? ChipFor(GameProfile game, ModuleGameState state) => state.Status switch
    {
        ModuleGameStatus.Attached => new ModuleChip("CE · running", ModuleChipTone.Attached),
        ModuleGameStatus.Ready => new ModuleChip(
            _savedOption(game.Game.AppId, ModuleGameOption.AttachOnLaunch) ? "CE · auto" : "CE · ready",
            ModuleChipTone.Accent),
        _ => null,
    };

    /// <summary>The card's button: attach while ready, detach while attached. Speaks of the
    /// application, like <see cref="ChipFor"/>; <c>default</c> as unreachable as there.</summary>
    public ModuleAction? ActionFor(GameProfile game, ModuleGameState state) => state.Status switch
    {
        ModuleGameStatus.Attached => new ModuleAction("DETACH CE", ModuleActionKind.Detach),
        ModuleGameStatus.Ready => new ModuleAction("ATTACH CE", ModuleActionKind.Attach),
        _ => null,
    };
}
