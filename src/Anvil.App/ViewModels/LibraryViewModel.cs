using System.Collections.ObjectModel;
using Anvil.Core;
using Anvil.Core.Modules;
using Anvil.Core.Prefixes;
using Anvil.Core.Sessions;
using Anvil.Core.Settings;
using Anvil.Core.Steam;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace Anvil.App.ViewModels;

/// <summary>
/// The home page: the game grid above the snapshot, and the selection that fills the right
/// column.
///
/// <para>
/// <b>The image loader is handed in, not built</b> (Spec §7). That keeps the grid testable
/// without a GUI: the test plugs in a stub and needs neither Skia nor a real JPEG file. The
/// same seam as with FolderPicker and DesktopShell.Open.
/// </para>
///
/// <para>
/// Search, filter, favorite and rescan are S2.5; the start button and GAME card are S2.6.
/// </para>
/// </summary>
public sealed partial class LibraryViewModel : PageViewModel
{
    /// <summary>The mockup's two pills. There are no more filters than this (project plan §1).</summary>
    public const string AllGames = "All";

    public const string FavoritesOnly = "Favorites";

    public static readonly IReadOnlyList<string> Filters = [AllGames, FavoritesOnly];

    private readonly GameLibrary _library;
    private readonly Func<string, IImage?> _loadArtwork;
    private readonly AppSettings _settings;
    private readonly SettingsStore _store;
    private readonly Action<string> _open;

    /// <summary>S3.8: handed to every detail, like <c>_open</c>.</summary>
    private readonly Func<RaiseOutcome> _raiseSteamWindow;

    private readonly GameSessionWatcher _watcher;
    private readonly GameRunController _run;
    private readonly SaveBarViewModel _saveBar;
    private readonly ModuleRegistry _modules;
    private readonly ModuleGameStates _states;

    /// <summary>
    /// Runs a module action (in the composition root, <c>ModuleActionRunner.RunAsync</c>) —
    /// handed to the detail and from there to every module card (S5.4). The library never calls
    /// it itself.
    /// </summary>
    private readonly Func<GameProfile, string, ModuleActionKind, Task> _runModuleAction;
    private readonly Action<Action> _dispatch;

    /// <summary>
    /// The open preparation (Spec S3.9 §3.2) — the library triggers it wherever the actual state
    /// changes: on build, after every rescan, at session end. <c>null</c> means "does not exist"
    /// (existing callers).
    /// </summary>
    private readonly PrefixWork? _prefixWork;

    /// <summary>
    /// The one memory of open folds (s11.1) — one for every game, so a fold opened on one game
    /// stands open on the next. Handed to every detail; it outlives the rescan's new cards.
    /// </summary>
    private readonly FoldMemory _folds;

    /// <summary>The wishes behind the options folds' dots, or <c>null</c> — handed to every detail.</summary>
    private readonly PendingChanges? _changes;

    private readonly ILogger? _logger;

    /// <summary>All cards of the current snapshot — the truth behind the display.</summary>
    private readonly List<GameCardViewModel> _all = [];

    private EmptyState? _emptyState;

    /// <summary>The selected card, or <c>null</c> — then the empty state stands on the right.</summary>
    [ObservableProperty]
    private GameCardViewModel? _selectedCard;

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private string _selectedFilter = AllGames;

    /// <summary>
    /// The card waiting for confirmation of the stop, or <c>null</c> (Spec S3.4 §3.4). As long as
    /// it is set, the dialog stands; the game keeps running and is not "busy" — only the
    /// confirmation begins the stop phase.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsStopPending))]
    [NotifyPropertyChangedFor(nameof(PendingStopTitle))]
    private GameCardViewModel? _pendingStop;

    public LibraryViewModel(
        GameLibrary library,
        Func<string, IImage?> loadArtwork,
        AppSettings settings,
        SettingsStore store,
        Action<string> open,
        Func<RaiseOutcome> raiseSteamWindow,
        GameSessionWatcher watcher,
        GameRunController run,
        SaveBarViewModel saveBar,
        ModuleRegistry modules,
        ModuleGameStates states,
        Func<GameProfile, string, ModuleActionKind, Task> runModuleAction,
        Action<Action> dispatch,
        ILogger? logger = null,
        PrefixWork? prefixWork = null,
        Action<EventHandler<ModuleGameToggle>>? onAttachRefused = null,
        FoldMemory? folds = null,
        PendingChanges? changes = null)
    {
        _library = library;
        _loadArtwork = loadArtwork;
        _settings = settings;
        _store = store;
        _open = open;
        _raiseSteamWindow = raiseSteamWindow;
        _watcher = watcher;
        _run = run;
        _saveBar = saveBar;
        _modules = modules;
        _states = states;
        _runModuleAction = runModuleAction;
        _dispatch = dispatch;
        _logger = logger;
        _prefixWork = prefixWork;
        _folds = folds ?? new FoldMemory();
        _changes = changes;

        // Subscribe first, then build the cards — the same pattern as in LogViewModel: this way
        // no event is lost. An event from the gap in between finds no card yet and does
        // nothing; the card built right after asks the watcher and gets the same answer. There
        // is only one truth, so both paths end the same.
        _watcher.SessionStarted += OnSessionStarted;
        _watcher.SessionEnded += OnSessionEnded;

        // s8.1: the runner refused to attach because the prefix is not prepared. The owner's rule
        // (2026-09-23) is that such a switch goes off; the rescan redraws cards and states the way
        // a save does. Dispatched: the refusal arrives on the watcher's or a pool thread.
        onAttachRefused?.Invoke((_, toggle) => _dispatch(() => SwitchOffAfterRefusal(toggle.AppId)));

        // The controller runs the phases, the card carries the badge — the same division of
        // roles as with the watcher (Spec S4.5 §3.2). It notifies on the UI thread; it posts
        // itself.
        _run.BusyChanged += (_, appId) =>
        {
            if (CardFor(appId) is { } card)
            {
                card.IsBusy = _run.IsBusy(appId);
            }
        };

        // The bar is the truth about "what is open"; the card only carries the badge. The
        // direction is unambiguous: the library listens to the bar, never the other way around
        // — otherwise the rescan would be a circle (Spec S4.5 §3.7).
        //
        // One event, two consequences (Spec S4.6 E-G): the bar notifies on the UI thread that
        // the set of wishes or the save state has changed. The cards' locks depend on this —
        // and since S4.6, the sections' fields that must point back at the profile after a
        // discard.
        _saveBar.StateChanged += (_, _) =>
        {
            UpdateLocks();
            foreach (var card in _all)
            {
                card.Detail.SyncSections();
            }
        };
        _saveBar.Saved += (_, _) => Rescan();

        // The global switch sits on the Modules page; the module cards stand here. Without this
        // line, a disabled module's card would keep standing until a rescan builds new cards
        // (S1.4). The switch is operated on the UI thread — no dispatch needed.
        _modules.EnabledChanged += (_, _) =>
        {
            foreach (var card in _all)
            {
                card.Detail.SyncModuleCards();
            }
        };

        // The same path for the finding (S3.4, Spec §3.5): when a globally enabled module
        // becomes ready or loses readiness, its card in the right column appears or disappears
        // exactly the way it already does for the switch itself. Unlike the switch,
        // <c>ReadinessChanged</c> comes from a foreign thread — the check runs fire-and-forget
        // from the Modules page or the settings section —, hence the <c>_dispatch</c> to the UI
        // thread here, which <c>_watcher</c> above needs just the same.
        _modules.ReadinessChanged += (_, _) =>
            _dispatch(() =>
            {
                foreach (var card in _all)
                {
                    card.Detail.SyncModuleCards();
                }
            });

        // The per-game switch sits on the module card, the launch options field beside it in
        // the same right column — without this line the wish would fail to show (S2.3). The
        // library listens, not the section: it knows which cards currently exist, and disposes
        // of them too (Spec E-D). Only the affected card has anything to do; the app id sits in
        // the notification.
        _modules.EnabledForGameChanged += (_, toggle) =>
            _all.Find(card => card.Profile.Game.AppId == toggle.AppId)
                ?.Detail.ModuleToggled(toggle.Key);

        // The sixth listener (S5.3): the one truth about "how does this module stand on this
        // game" notifies every real change once, and the affected card's chips follow.
        //
        // The dispatch is not optional: StateChanged notifies on the thread of the triggering
        // source — attacher, watcher or registry —, house style since S3.3. Same reasoning as
        // for the ReadinessChanged listener above.
        //
        // Notified per pair, computed per card: the card rebuilds all its chips instead of
        // replacing the one. A second path would be a second truth about the chips' order, and
        // with at most a handful per card the gain is zero.
        //
        // One listener, two readers (S5.4): since the module card reads from the same source, it
        // depends on the same notification. A second subscriber for the same notification would
        // be a second truth about their order.
        //
        // One listener, three readers (S5.5): the context menu entry reads from the same source
        // as chip and module card.
        _states.StateChanged += (_, state) =>
            _dispatch(() =>
            {
                if (CardFor(state.AppId) is { } card)
                {
                    SyncChips(card);
                    SyncMenu(card);
                    card.Detail.SyncModuleState(state.ModuleKey);
                }
            });

        // Spec S3.9 E-13, extended s8.1: a game can have lost its prefix, or its preparation —
        // before the cards are built, so no card shows a switch that is about to go off.
        _modules.SwitchOffUnprepared(library.Current.Games, _watcher.IsRunning);
        _all.AddRange(BuildCards(library.Current));
        Refresh();
        LogLibrary();
        RefreshWork();
    }

    public override string Title => "Library";

    public override string Subtitle => string.Empty;

    /// <summary>
    /// The visible cards: running, favorites, rest — within each group as in the snapshot
    /// (element description §3, S3.7). <b>No second list</b> — the same objects as in
    /// <c>_all</c>, just fewer of them and in a different order.
    /// </summary>
    public ObservableCollection<GameCardViewModel> Cards { get; } = [];

    /// <summary>
    /// The number beside the title: the library's <b>total count</b>, not the matches (concept:
    /// <c>countLine</c>). It changes at exactly one place — on rescan —, and is notified there by
    /// hand; <c>_all</c> is a plain list and notifies nothing on its own.
    /// </summary>
    public string CountLine => _all.Count == 1 ? "1 game" : $"{_all.Count} games";

    public override EmptyState? EmptyState => _emptyState;

    /// <summary>Right column without a selection (Spec S1.5 E4: it belongs to the Library page).</summary>
    public EmptyState DetailEmptyState { get; } = new(
        "IconDetailPanel",
        "No game selected",
        "Pick a game in the library to see its Proton settings and modules.");

    /// <summary>The dialog's visibility — the view binds to this.</summary>
    public bool IsStopPending => PendingStop is not null;

    /// <summary>
    /// The dialog's title with the game's name (owner decision 2026-09-06). Empty without a
    /// dialog — the text is then not drawn.
    /// </summary>
    public string PendingStopTitle
        => PendingStop is { } card ? $"Stop {card.Name}?" : string.Empty;

    partial void OnSearchTextChanged(string value) => Refresh();

    partial void OnSelectedFilterChanged(string value)
    {
        if (value is null)
        {
            // The ListBox reports null when its selection is cleared — that means "All".
            SelectedFilter = AllGames;
            return;
        }

        Refresh();
    }

    /// <summary>Left click on a card. Exactly one card carries the badge.</summary>
    [RelayCommand]
    private void Select(GameCardViewModel? card)
    {
        if (card is null)
        {
            return;
        }

        if (SelectedCard is not null)
        {
            SelectedCard.IsSelected = false;
        }

        card.IsSelected = true;
        SelectedCard = card;
    }

    /// <summary>
    /// Without a path, nothing is loaded at all — no artwork is the normal case for a freshly
    /// installed game. And a loader that unexpectedly throws must not take the whole grid down
    /// with it (Spec §13.3).
    /// </summary>
    private static IImage? LoadOrNothing(
        string? path, Func<string, IImage?> loadArtwork, ILogger? logger)
    {
        if (path is null)
        {
            return null;
        }

        try
        {
            return loadArtwork(path);
        }
        catch (Exception error)
        {
            logger?.LogWarning("could not load artwork {Path}: {Reason}", path, error.Message);
            return null;
        }
    }

    /// <summary>
    /// Builds a snapshot's cards, each with image, favorite badge, running state and detail.
    ///
    /// <para>
    /// <b>The running state is asked here, not remembered.</b> It sits nowhere in the
    /// ViewModel; every freshly built card fetches it from the watcher. That is exactly why it
    /// survives the rescan without any line carrying it across.
    /// </para>
    /// </summary>
    private IEnumerable<GameCardViewModel> BuildCards(SteamSnapshot snapshot)
        => snapshot.Games.Select(profile =>
        {
            var appId = profile.Game.AppId;
            var card = new GameCardViewModel(
                profile,
                LoadOrNothing(profile.ArtworkPath, _loadArtwork, _logger),
                _settings.Games.TryGetValue(appId, out var section) && section.Favorite,
                new GameDetailViewModel(
                    profile, _open, _raiseSteamWindow, _modules.SectionsFor(profile),
                    _modules, _states, _runModuleAction, _logger, _folds, _changes),
                ToggleRunCommand)
            {
                IsRunning = _watcher.IsRunning(appId),
                IsBusy = _run.IsBusy(appId),
                IsLocked = IsLocked(appId),
            };

            // The card does not fetch the chips itself — it knows neither registry nor state
            // source (S1.4 line, Spec S5.3 §3.6). Since S5.5, the same holds for the module rows
            // of its context menu.
            SyncChips(card);
            SyncMenu(card);
            return card;
        });

    /// <summary>
    /// Rebuilds a card's chips. <b>Clear and refill</b>, like
    /// <c>GameDetailViewModel.SyncModuleCards</c>: with at most a handful of chips, a second
    /// path that inserts individual ones is more code than benefit.
    /// </summary>
    private void SyncChips(GameCardViewModel card)
    {
        card.Chips.Clear();
        foreach (var chip in ChipsFor(card.Profile))
        {
            card.Chips.Add(chip);
        }
    }

    /// <summary>
    /// The rule that sits in the Core and not in the module (Spec S5.3 §3.3): asked <b>only</b>
    /// for <see cref="ModuleGameStatus.Ready"/> and <see cref="ModuleGameStatus.Attached"/>.
    ///
    /// <para>
    /// "If the module cannot take hold here, the card carries no chip" is a statement about
    /// <i>every</i> module, not about a specific one. If it sat in the module, every future one
    /// would have to rewrite it — and one would forget it. Then a card would carry a chip for a
    /// native game, and the absence would no longer be a statement.
    /// </para>
    ///
    /// <para>
    /// <b>The module may still deliver <c>null</c>.</b> The two rules do not clash: the Core
    /// says "a chip may stand here at all", the module says "I have one".
    /// </para>
    /// </summary>
    private IEnumerable<ModuleChipViewModel> ChipsFor(GameProfile game)
    {
        foreach (var module in _modules.Modules)
        {
            if (!module.IsPerGame)
            {
                continue;
            }

            var state = _states.StateOf(game.Game.AppId, module.Key);
            if (state.Status is not (ModuleGameStatus.Ready or ModuleGameStatus.Attached))
            {
                continue;
            }

            if (module.ChipFor(game, state) is { } chip)
            {
                yield return new ModuleChipViewModel(chip);
            }
        }
    }

    /// <summary>
    /// Rebuilds a card's context menu module rows (Spec S5.5 §3.3) — <b>at the same place and by
    /// the same rule as the chips</b>. The card knows neither registry nor state source; it gets
    /// the finished rows.
    /// </summary>
    private void SyncMenu(GameCardViewModel card)
        => card.SetModuleRows([.. ModuleRowsFor(card.Profile)]);

    /// <summary>
    /// One group per module that has something to offer here: divider, caption, action (element
    /// description §9). <b>A module without an action gets no caption</b> — an empty group would
    /// be worse than none.
    ///
    /// <para>
    /// <b>The filter rule does not sit here, but in the Core</b> (<see cref="ModuleActionRule"/>,
    /// Spec S5.5 §3.4): the same answer that the module card's button also reads from.
    /// </para>
    ///
    /// <para>
    /// <b>The caption names the module, not the application</b> (Spec S5.5 E-2) — unlike the
    /// chip beside it, and deliberately: it assigns a group to its module.
    /// </para>
    /// </summary>
    private IEnumerable<GameMenuRowViewModel> ModuleRowsFor(GameProfile game)
    {
        foreach (var module in _modules.Modules)
        {
            if (!module.IsPerGame)
            {
                continue;
            }

            var state = _states.StateOf(game.Game.AppId, module.Key);
            if (ModuleActionRule.Offer(module, game, state) is not { } offer)
            {
                continue;
            }

            yield return new GameMenuRuleViewModel();
            yield return new GameMenuCaptionViewModel(module.DisplayName.ToUpperInvariant());
            yield return new GameMenuActionViewModel(
                offer.Action.Label,
                new AsyncRelayCommand(() => RunModuleActionAsync(game, module.Key)),
                parameter: null,
                offer.IsEnabled,
                isRelease: offer.Action.Kind == ModuleActionKind.Detach,
                isStart: offer.Action.Kind == ModuleActionKind.Attach);
        }
    }

    /// <summary>
    /// What a module row triggers. <b>Asked again instead of read from the drawn row</b> (Spec
    /// S5.5 §3.5): between drawing and clicking, the game can end — the same caution as in
    /// <c>ModuleCardViewModel.RunActionAsync</c>.
    ///
    /// <para>
    /// <b>The guard sits here even though the row is locked</b> (the same stance as in
    /// <see cref="ToggleRun"/>, S3.3 E-G): the lock is a display, not a protection.
    /// </para>
    /// </summary>
    private async Task RunModuleActionAsync(GameProfile game, string moduleKey)
    {
        var module = _modules.Modules.FirstOrDefault(entry => entry.Key == moduleKey);
        if (module is null)
        {
            return;
        }

        var state = _states.StateOf(game.Game.AppId, moduleKey);
        if (ModuleActionRule.Offer(module, game, state) is not { IsEnabled: true } offer)
        {
            return;
        }

        await _runModuleAction(game, moduleKey, offer.Action.Kind);
    }

    /// <summary>
    /// Resets the lock on all cards. <b>All, not just the affected ones</b>: going through sixty
    /// cards costs nothing, and a path without a special case is worth more than the saved
    /// comparison.
    /// </summary>
    private void UpdateLocks()
    {
        foreach (var card in _all)
        {
            card.IsLocked = IsLocked(card.Profile.Game.AppId);
        }
    }

    // S3.9 E-16: needs preparing locks the launch just like an unsaved wish —
    // WorkGames names the affected games as long as the bar shows the state.
    private bool IsLocked(uint appId)
        => _saveBar.IsSaving || _saveBar.ChangedGames.Contains(appId) || _saveBar.WorkGames.Contains(appId);

    /// <summary>
    /// Refills the visible collection from <c>_all</c> — search AND filter, in the order of
    /// <see cref="Rank"/>. <b>Clear and refill, no move</b> (Spec S3.7 E-F): a second path that
    /// moves individual cards would be more code for 18 cards. The scroll position resets in the
    /// process (E-K) — the card jumps to the top anyway.
    /// </summary>
    private void Refresh()
    {
        Cards.Clear();
        foreach (var card in _all.OrderBy(Rank))
        {
            if (Matches(card))
            {
                Cards.Add(card);
            }
        }

        UpdateEmptyState();
    }

    /// <summary>
    /// The grid's order (element description §3, Spec S3.7 §3.4): running, favorites, rest.
    /// Within a group, the snapshot's order holds — <c>OrderBy</c> is stable, and the alphabet is
    /// the Core's business (E-H). A running favorite stands under "running", not twice (E-G).
    /// </summary>
    private static int Rank(GameCardViewModel card)
        => card.IsRunning ? 0 : card.IsFavorite ? 1 : 2;

    /// <summary>
    /// The concept's filter rule, literally: substring in the name, case does not matter,
    /// AND-combined with the pill. <c>OrdinalIgnoreCase</c> like on the log page — this way the
    /// result does not depend on the system language (Spec E3).
    /// </summary>
    private bool Matches(GameCardViewModel card)
        => (SelectedFilter == AllGames || card.IsFavorite)
           && (string.IsNullOrWhiteSpace(SearchText)
               || card.Name.Contains(SearchText, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Three cases instead of two (Spec §6.4): matches, empty library, filtered out. The
    /// comparison at the end holds back unnecessary notifications —
    /// <see cref="ViewModels.EmptyState"/> is a record, whose value comparison carries this.
    /// </summary>
    private void UpdateEmptyState()
    {
        EmptyState? next;
        if (Cards.Count > 0)
        {
            next = null;
        }
        else if (_all.Count == 0)
        {
            next = new EmptyState(
                "IconLibrary",
                "No games in the library",
                "Anvil reads the games from Steam. Rescan after installing one.",
                "Rescan library",
                RescanCommand);
        }
        else
        {
            next = new EmptyState(
                "IconSearch",
                "Nothing matches",
                SelectedFilter == AllGames
                    ? "No game matches the search in the current filter."
                    : "No game matches the search in the Favorites filter.",
                "Clear search",
                ClearSearchCommand);
        }

        if (!Equals(next, _emptyState))
        {
            _emptyState = next;
            OnPropertyChanged(nameof(EmptyState));
        }
    }

    /// <summary>The way back out of "Nothing matches" — both reset, as in the concept.</summary>
    [RelayCommand]
    private void ClearSearch()
    {
        SearchText = string.Empty;
        SelectedFilter = AllGames;
    }

    /// <summary>
    /// The star on the card. Written immediately, without "unsaved" — the same pattern as
    /// <c>SettingsViewModel.Persist</c> and <c>ModuleRegistry.SetEnabled</c>. The project plan's
    /// global save bar belongs to Steam write operations (M4), not to Anvil's own file.
    /// </summary>
    [RelayCommand]
    private void ToggleFavorite(GameCardViewModel? card)
    {
        if (card is null)
        {
            return;
        }

        card.IsFavorite = !card.IsFavorite;
        SectionFor(card.Profile.Game.AppId).Favorite = card.IsFavorite;
        _store.Save(_settings);
        _logger?.LogDebug(
            "favorite {State}: {AppId}",
            card.IsFavorite ? "set" : "cleared",
            card.Profile.Game.AppId);

        // If the filter is set to Favorites, the card no longer belongs in the view.
        Refresh();
    }

    /// <summary>
    /// Fetches the game's section and creates it if it does not yet exist — the same lazy
    /// creation as in <c>ModuleRegistry.SetEnabled</c>.
    /// </summary>
    private GameSection SectionFor(uint appId)
    {
        if (!_settings.Games.TryGetValue(appId, out var section))
        {
            section = new GameSection();
            _settings.Games[appId] = section;
        }

        return section;
    }

    /// <summary>
    /// Rescans the Steam library.
    ///
    /// <para>
    /// <b>Synchronous, without progress and without a lock</b> (Spec E8): the scanner measures
    /// 0.04 s across the owner's whole library. A <c>Task.Run</c> plus reentrancy guard would
    /// cost more code than the problem is big.
    /// </para>
    ///
    /// <para>
    /// <b>The old images are disposed.</b> A <c>Bitmap</c> is <see cref="IDisposable"/>; without
    /// this, memory would grow by around 6.4 MB with every rescan. The <c>as</c> instead of a
    /// hard cast is deliberate — the seam's contract is <see cref="IImage"/>, and a test stub
    /// must not have to be able to dispose of anything.
    /// </para>
    /// </summary>
    [RelayCommand]
    private void Rescan()
    {
        var selectedAppId = SelectedCard?.Profile.Game.AppId;
        var snapshot = _library.Rescan();

        foreach (var card in _all)
        {
            (card.Artwork as IDisposable)?.Dispose();
        }

        SelectedCard = null;
        // The waiting card is about to no longer exist (Spec S3.4 E-K).
        PendingStop = null;
        _all.Clear();
        // Spec S3.9 E-13, extended s8.1: a game can have lost its prefix, or its preparation —
        // switch off before building the cards.
        _modules.SwitchOffUnprepared(snapshot.Games, _watcher.IsRunning);
        // S5.3, refined from S5.2: the state source remembers its verdicts and only
        // recomputes when one of its five seams reports in. Two things do not report in — a
        // rescan (a game has gained or lost its prefix) and a save
        // (SaveEnabledFor reports nothing by contract, S3.8 E-5; a prepared prefix
        // changes the marker on disk). Both run through here because the bar has hung
        // `_saveBar.Saved += Rescan` off this since S3.7 — one call covers both occasions.
        //
        // The spot is narrower than it looks: AFTER _library.Rescan(), because the delegate
        // profileOf only then points at the new profiles, and BEFORE BuildCards, because the
        // freshly built cards would otherwise fetch their chips from the source's stale memory.
        //
        // Not to be confused with Refresh() further below — that is the visible card list.
        _states.Refresh();
        _all.AddRange(BuildCards(snapshot));
        OnPropertyChanged(nameof(CountLine));
        Refresh();
        RefreshWork();

        // After the rescan, every card is a new object. The AppId is the key that
        // the whole application uses anyway — the user does not lose their place.
        if (selectedAppId is uint appId)
        {
            Select(_all.Find(card => card.Profile.Game.AppId == appId));
        }

        _logger?.LogInformation("library rescanned: {Count} games", _all.Count);
    }

    /// <summary>
    /// s8.1: the runner just watched the module fail to attach in this game's prefix — the
    /// switch goes off and the page redraws, the same way a save does.
    /// </summary>
    private void SwitchOffAfterRefusal(uint appId)
    {
        if (_library.Current.Games.FirstOrDefault(game => game.Game.AppId == appId) is not { } game)
        {
            return;
        }

        // Running on purpose: the runner has just seen the module fail in this very prefix.
        _modules.SwitchOffUnprepared([game], isRunning: _ => false);
        Rescan();
    }

    /// <summary>
    /// The watcher's events run on its own poll thread (Spec S3.1). The handed-in
    /// dispatcher brings them onto the UI thread — the same pattern as in
    /// <see cref="LogViewModel"/>, and no Avalonia type in this ViewModel.
    /// </summary>
    private void OnSessionStarted(object? sender, GameSession session)
        => _dispatch(() => SetRunning(session.AppId, running: true));

    private void OnSessionEnded(object? sender, GameSession session)
        => _dispatch(() =>
        {
            SetRunning(session.AppId, running: false);

            // E-5: the first launch creates the prefix, and only a scan learns of it. Likewise,
            // only a launch after a Proton switch rewrites config_info. Otherwise a
            // Refresh suffices — a rescan rebuilds every card and would take the focus from an
            // input field.
            var profile = CardFor(session.AppId)?.Profile;
            if (profile is { PrefixPath: null, Platform: not GamePlatform.NativeLinux })
            {
                _logger?.LogInformation("appid {AppId} ended without a known prefix — rescanning", session.AppId);
                Rescan();
            }
            else if (profile is not null && ProtonConfigInfo.Differs(profile))
            {
                _logger?.LogInformation("appid {AppId} ended with a changed config_info — rescanning", session.AppId);
                Rescan();
            }
            else
            {
                RefreshWork();
            }
        });

    /// <summary>Fire-and-forget is safe here: <see cref="PrefixWork.RefreshAsync"/> never throws (Spec S3.9 §3.2).</summary>
    private void RefreshWork() => _ = _prefixWork?.RefreshAsync();

    private void SetRunning(uint appId, bool running)
    {
        if (!running && PendingStop?.Profile.Game.AppId == appId)
        {
            // The dialog is asking about something that no longer exists (Spec S3.4 E-I).
            PendingStop = null;
            _logger?.LogDebug(
                "stop confirmation closed: appid {AppId} ended on its own", appId);
        }

        if (CardFor(appId) is { } card)
        {
            card.IsRunning = running;
            // The card jumps to the top on launch and back on end (Spec S3.7 §3.4).
            // Even if it is filtered out: a path without a special case (E-I).
            Refresh();
        }
    }

    /// <summary>
    /// The right column's one button — and, since S3.4, the same entry in the context menu:
    /// starts what stands there, and <b>asks first</b> before ending what is running.
    ///
    /// <para>
    /// <b>The guard sits here even though the button is locked</b> (Spec S3.3 E-G): the
    /// lock is a display, not a protection. The command must stay correct even when
    /// it is called from elsewhere.
    /// </para>
    ///
    /// <para>
    /// <b>The stop no longer runs here</b>, but in <see cref="ConfirmStopAsync"/>.
    /// Here only the dialog is opened; a second wish replaces the first (Spec S3.4
    /// E-J) — the newer one is the valid one.
    /// </para>
    /// </summary>
    [RelayCommand]
    private void ToggleRun(GameCardViewModel? card)
    {
        if (card is null)
        {
            return;
        }

        var appId = card.Profile.Game.AppId;
        if (!card.CanRun)
        {
            return;
        }

        if (card.IsRunning)
        {
            PendingStop = card;
            _logger?.LogDebug("stop confirmation opened: appid {AppId}", appId);
        }
        else
        {
            _run.Start(appId);
        }
    }

    /// <summary>
    /// "Stop game" in the dialog. <b>Checks once more what holds</b> (Spec S3.4 E-H): the dialog
    /// can stand open for a long time; in that time the game can have ended, or — via the
    /// command, not via the interface — a stop can already be running. A stop into the void would be
    /// harmless, but a log line for nothing.
    /// </summary>
    [RelayCommand]
    private async Task ConfirmStopAsync()
    {
        if (PendingStop is not { } card)
        {
            return;
        }

        PendingStop = null;
        var appId = card.Profile.Game.AppId;
        if (!card.IsRunning || !card.CanRun)
        {
            return;
        }

        await _run.StopAsync(appId).ConfigureAwait(false);
    }

    /// <summary>"Keep playing", or closing the confirmation's window (frame X, Esc, Alt+F4).</summary>
    [RelayCommand]
    private void KeepPlaying()
    {
        if (PendingStop is not { } card)
        {
            return;
        }

        PendingStop = null;
        _logger?.LogDebug(
            "stop confirmation dismissed: appid {AppId}", card.Profile.Game.AppId);
    }

    /// <summary>
    /// A game's card, or <c>null</c>. The search runs against <c>_all</c> and not
    /// <c>Cards</c>: a filtered-out card still runs, and it comes back as soon as
    /// the filter lifts.
    /// </summary>
    private GameCardViewModel? CardFor(uint appId)
        => _all.Find(card => card.Profile.Game.AppId == appId);

    private void LogLibrary() => _logger?.LogDebug(
        "library: {Count} cards, {Loaded} with artwork, {Favorites} favorite",
        _all.Count,
        _all.Count(card => card.Artwork is not null),
        _all.Count(card => card.IsFavorite));
}
