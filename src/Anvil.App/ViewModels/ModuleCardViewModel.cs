using Anvil.Core.Modules;
using Anvil.Core.Prefixes;
using Anvil.Core.Steam;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Anvil.App.ViewModels;

/// <summary>
/// A module card of the right column (S1.4): header row with name, state tag and the switch
/// "Enable module for this game" — since S5.4 with the module's section, the action and the
/// status line; since s11.1 the action is a compact button in the header row and the section
/// sits behind the options fold.
///
/// <para>
/// <b>The card no longer judges by itself</b> (Spec S5.4 §3.5): tag, reason, body, action and
/// status line read from <see cref="ModuleGameStates"/> — the same truth the chip on the game
/// card reads from. Two places that each judged on their own would be two answers, and they
/// would drift apart exactly when it matters.
/// </para>
///
/// <para>
/// <b>The per-game switch is the one exception</b> (Spec S5.4 E-4): it shows the <i>wish</i>
/// (<see cref="ModuleRegistry.IsEnabledForIncludingUnsaved"/>), because a switch that jumps back
/// after flipping would be unusable. Everything else shows what <b>holds</b>.
/// <b>The consequence is visible and intended:</b> whoever turns the switch on sees the body only
/// after saving — before that the prefix is not prepared, and the action could not do anything
/// anyway.
/// </para>
///
/// <para>
/// <b>No state in the view model</b> — the same shape as <see cref="ModuleItemViewModel"/> on the
/// Modules page: everything is read on every access, nothing is held. The source answers from
/// memory, the registry from its settings.
/// </para>
///
/// <para>
/// <b>A card belongs to one game.</b> The profile is fixed — every game has its own right column
/// and with it its own cards. Switching the selected game switches the objects, not their
/// content.
/// </para>
/// </summary>
public sealed class ModuleCardViewModel : ObservableObject
{
    private readonly IModule _module;
    private readonly ModuleRegistry _registry;
    private readonly GameProfile _game;
    private readonly uint _appId;

    /// <summary>
    /// The one truth about "how does this module stand on this game" (S5.2). <b>As an object,
    /// not as a handed-in record:</b> the card lives longer than one verdict, and a held record
    /// would be a second state next to the source.
    /// </summary>
    private readonly ModuleGameStates _states;

    /// <summary>
    /// The grip's run, narrowed to "game, module, kind" — in the root
    /// <c>ModuleActionRunner.RunAsync</c>. <b>The <c>ModuleAttacher</c> stays internal to the
    /// Core</b> (architecture.md §4.2): the UI says <i>what</i> should happen, the Core knows how.
    /// </summary>
    private readonly Func<GameProfile, string, ModuleActionKind, Task> _run;

    /// <summary>
    /// The wishes behind the options fold's dot, or <c>null</c> — then the fold never shows one
    /// (s11.1, TD-31).
    /// </summary>
    private readonly PendingChanges? _changes;

    /// <summary>
    /// The sentence endings of the <c>not possible</c> reason (Spec S3.9 E-12), one per case. The
    /// sentence before them names the module, not the application — the card builds it for every
    /// module.
    /// </summary>
    public const string NativeReasonTail = "this game runs as a native Linux build.";

    /// <inheritdoc cref="NativeReasonTail"/>
    public const string UnknownReasonTail = "the platform of this game could not be determined.";

    /// <inheritdoc cref="NativeReasonTail"/>
    public const string NeverStartedReasonTail = "start the game once so Steam creates it.";

    /// <summary>
    /// The sentence for a downgraded prefix, verbatim from answer 50 (c). <b>It does not name the
    /// module</b>, unlike the three sentence endings above: it is about the prefix, not the module.
    /// </summary>
    public const string MovedBackReason =
        "This game was moved back to an older Proton version. Anvil cannot undo that — "
            + "switch back in Steam, or let Steam rebuild the prefix.";

    /// <summary>The status line while the grip attaches (s11.1), with U+2026 as drawn.</summary>
    public const string AttachingLine = "Attaching…";

    /// <summary>
    /// Both display names, if this game was downgraded (Spec S4.2 §3.6) — <b>collected once in the
    /// constructor</b>. Properties Avalonia binds to never read the disk.
    /// </summary>
    private readonly PayloadStanding? _movedBack;

    /// <param name="states">
    /// The one truth about "how does this module stand on this game" (S5.2).
    /// </param>
    /// <param name="run">
    /// Runs a module action — in the root <c>ModuleActionRunner.RunAsync</c>. It never throws and
    /// reports by itself how it went.
    /// </param>
    /// <param name="folds">
    /// Where the options fold keeps its open state; <c>null</c> gives the card a private memory.
    /// </param>
    /// <param name="changes">
    /// The wishes the options fold's dot reads; <c>null</c> means the fold never shows a dot.
    /// </param>
    public ModuleCardViewModel(
        IModule module,
        ModuleRegistry registry,
        GameProfile game,
        ModuleGameStates states,
        Func<GameProfile, string, ModuleActionKind, Task> run,
        FoldMemory? folds = null,
        PendingChanges? changes = null)
    {
        _module = module;
        _registry = registry;
        _game = game;
        _appId = game.Game.AppId;
        _states = states;
        _run = run;
        _changes = changes;
        _movedBack = FindMovedBack(module, game);

        // Built once, not per access: a section has fields of its own, and a new object on every
        // read would lose them — the view would then bind to something already replaced.
        Section = module.CreateSection(game);
        ActionCommand = new AsyncRelayCommand(RunActionAsync, () => IsActionEnabled);
        OptionsFold = new FoldViewModel(
            folds ?? new FoldMemory(), FoldMemory.OptionsKeyFor(Key), "Options", HasUnsavedOption);
    }

    // Per component the module requires for this game: a single downgraded one makes the card
    // unusable — the sentence needs no more.
    private static PayloadStanding? FindMovedBack(IModule module, GameProfile game)
    {
        var report = PrefixReport.Of(game);
        if (report is not { State: PrefixState.Present, Path: { } prefixPath, ProtonMajor: { } major })
        {
            return null;
        }

        foreach (var component in module.PrefixComponentsFor(game))
        {
            var standing = PrefixPayloadStanding.Of(prefixPath, component.Key, major);
            if (standing.Kind == PayloadStandingKind.MovedBack)
            {
                return standing;
            }
        }

        return null;
    }

    /// <summary>
    /// The verdict on this module for this game. <b>Fetched on every access</b>, never held (S1.4
    /// line): the source answers from memory, and what it says right now is the only right answer.
    /// </summary>
    private ModuleGameState State => _states.StateOf(_appId, Key);

    public string Key => _module.Key;

    public string DisplayName => _module.DisplayName;

    /// <summary>
    /// The resource key of the header row's icon, or <c>null</c> (S2.2). The view resolves it via
    /// <c>ResourceLookup</c>; here it stays a string so the card is testable without a UI (Spec
    /// E-A).
    /// </summary>
    public string? IconKey => _module.IconKey;

    /// <summary>
    /// Does this module have an icon? A <c>bool</c> of its own, because Avalonia cannot bind "is
    /// not null", and a visible <c>Path</c> without <c>Data</c> would otherwise leave an empty spot
    /// including its spacing.
    /// </summary>
    public bool HasIcon => IconKey is not null;

    /// <summary>
    /// This module's section for this game, or <c>null</c> (Spec S5.4 §3.6). <b>The Core draws the
    /// frame</b>, the module supplies the content; a <c>DataTemplate</c> over the concrete type
    /// maps it to the view — the same shape as the sections of the GAME card (S4.6).
    ///
    /// <para>
    /// <b>Here and no longer in the GAME card</b> (Spec S5.4 E-1): the section of a per-game module
    /// belongs under its module's tag, not under "Proton version".
    /// </para>
    /// </summary>
    public object? Section { get; }

    /// <summary>
    /// Can the module work for this game? (Spec S3.9 E-12, S4.2 E-2, S5.4 §3.5) — <b>no longer
    /// computed here since S5.4</b>: <see cref="ModuleGameStates"/> answers the question for chip
    /// and card together.
    ///
    /// <para>
    /// The profile is the snapshot the card was built from; after the first start the rescan
    /// builds new cards.
    /// </para>
    /// </summary>
    public bool IsPossible => State.Status != ModuleGameStatus.NotPossible;

    /// <summary>
    /// Is the switch usable? The same as <see cref="IsPossible"/>; a name of its own, because the
    /// view asks the switch a question, not the module.
    /// </summary>
    public bool IsSwitchEnabled => IsPossible;

    /// <summary>
    /// The switch's tooltip, or <c>null</c> when it is not usable — as drawn (design answer,
    /// follow-up 2026-09-13): the reason stands in plain text in the card, and tooltips are slow
    /// under Wayland.
    /// </summary>
    public string? SwitchTooltip => IsPossible ? "Enable module for this game" : null;

    /// <summary>
    /// The reason line under the header row, or <c>null</c> when the module can work (Spec S3.9
    /// E-12). <b>The reason comes from the state</b> (Spec S5.4 §3.5): the source carries the kind,
    /// the card the English sentences — that way no user text travels into the Core.
    /// </summary>
    public string? ImpossibleReason => State.Block switch
    {
        ModuleGameBlock.MovedBack => MovedBackReason,
        ModuleGameBlock.NativeGame => Needs(NativeReasonTail),
        ModuleGameBlock.UnknownPlatform => Needs(UnknownReasonTail),
        ModuleGameBlock.NeverStarted => Needs(NeverStartedReasonTail),
        _ => null,
    };

    private string Needs(string tail) => $"{DisplayName} needs a Proton prefix — {tail}";

    /// <summary>Is there a reason? A <c>bool</c> of its own, like <see cref="HasIcon"/>.</summary>
    public bool HasImpossibleReason => ImpossibleReason is not null;

    /// <summary>
    /// The mono line under the sentence (answer 50 d): <c>prepared for Proton 11.0 · now running
    /// Proton 9.0 (Beta)</c>. <c>null</c>, except for a downgraded prefix.
    ///
    /// <para>
    /// <b>A query of its own, even after S5.4</b> (Spec E-6): <see cref="ModuleGameState"/> carries
    /// the <i>reason</i> (<see cref="ModuleGameBlock.MovedBack"/>), not the two display names.
    /// Lifting them into the state would mean widening a structure for two strings that exactly
    /// one surface reads. The two cannot drift apart: they read the same rule.
    /// </para>
    ///
    /// <para>
    /// The display names come from the record and from the game's tool. If one is missing, it
    /// reads <c>Proton &lt;number&gt;</c> — the number is always known in this state, otherwise the
    /// state would not exist.
    /// </para>
    /// </summary>
    public string? PreparedForLine
    {
        get
        {
            if (_movedBack is not { } standing)
            {
                return null;
            }

            var prepared = standing.PreparedToolName ?? $"Proton {standing.PreparedMajor}";
            var current = _game.Proton?.Tool?.Name ?? $"Proton {PrefixReport.Of(_game).ProtonMajor}";
            return $"prepared for {prepared} · now running {current}";
        }
    }

    /// <summary>Is the mono line there? A <c>bool</c> of its own, like <see cref="HasIcon"/>.</summary>
    public bool HasPreparedForLine => PreparedForLine is not null;

    /// <summary>
    /// The header row's tag, since S5.4 in all four values of the design — <c>off</c>,
    /// <c>ready</c>, <c>running</c>, <c>not possible</c>. It is a translation of
    /// <see cref="ModuleGameStatus"/> into the UI's words, nothing more.
    /// </summary>
    public string Tag => State.Status switch
    {
        ModuleGameStatus.NotPossible => "not possible",
        ModuleGameStatus.Attached => "running",
        ModuleGameStatus.Ready => "ready",
        _ => "off",
    };

    /// <summary>
    /// The tag class for the UI. Separate bools instead of a state text, because Avalonia binds
    /// via <c>Classes.x="{Binding …}"</c> — the same shape as
    /// <see cref="ModuleItemViewModel.IsTagOff"/>.
    /// </summary>
    public bool IsTagOff => State.Status == ModuleGameStatus.NotEnabled;

    /// <inheritdoc cref="IsTagOff"/>
    public bool IsTagReady => State.Status == ModuleGameStatus.Ready;

    /// <inheritdoc cref="IsTagOff"/>
    public bool IsTagRunning => State.Status == ModuleGameStatus.Attached;

    /// <inheritdoc cref="IsTagOff"/>
    public bool IsTagNotPossible => State.Status == ModuleGameStatus.NotPossible;

    /// <summary>
    /// Can the module act on this game at all? (Spec S5.4 §3.5) Only then the options stand — with
    /// <c>off</c> there would be nothing to operate, with <c>not possible</c> options under the
    /// reason would contradict it. Since s11.1 the action button stands regardless, greyed.
    /// </summary>
    public bool HasBody => State.Status is ModuleGameStatus.Ready or ModuleGameStatus.Attached;

    /// <summary>
    /// Does the card show its options fold? (s11.1) Only with an open body and a section to fold —
    /// a module without a section has no options.
    /// </summary>
    public bool HasOptions => HasBody && Section is not null;

    /// <summary>
    /// The heading of the module's section, closed by default (s11.1). Its open state lives in the
    /// fold memory, shared by every game; its dot reads this game's option wishes of this module.
    /// </summary>
    public FoldViewModel OptionsFold { get; }

    private bool HasUnsavedOption()
        => _changes?.Options.Any(wish => wish.Key == Key && wish.AppId == _appId) ?? false;

    /// <summary>
    /// This module's action on this game together with its usability. <b>The rule lives in the
    /// Core since S5.5</b> (<see cref="ModuleActionRule"/>), because the context menu is the same
    /// reader — two copies would drift apart exactly when it matters.
    /// </summary>
    private ModuleActionOffer? Offer => ModuleActionRule.Offer(_module, _game, State);

    /// <summary>
    /// Is the button the counter-action? Carries the signal tone in the view that the element
    /// description states for <i>every</i> release — the tone sits in the Core, not in the module
    /// (Spec S5.4 E-3).
    /// </summary>
    public bool IsActionRelease => Offer?.Action.Kind == ModuleActionKind.Detach;

    /// <summary>
    /// The name of the compact action button (s11.1): "Attach Wand", "Detach CE". It stands even
    /// where no action is offered — the greyed button keeps its name. A bridge is named after its
    /// application, any other module after its display name.
    /// </summary>
    public string ActionName
        => (IsActionRelease ? "Detach " : "Attach ")
            + (Section is BridgeSectionViewModel bridge ? bridge.ApplicationName : DisplayName);

    /// <summary>
    /// Is the grip attaching right now? Derived from the state, not held: ready plus busy is the
    /// phase between the click and the attached program (s11.1, TD-9).
    /// </summary>
    public bool IsAttaching => State is { Status: ModuleGameStatus.Ready, IsBusy: true };

    /// <summary>
    /// Is there a status line under the header row? The reason or <see cref="AttachingLine"/> —
    /// whichever applies (s11.1). <b>Not the attached line</b> since s13.1: tag and Stop button
    /// already say it is attached, so the PID moved into the options (<see cref="AttachedLine"/>).
    /// </summary>
    public bool HasStatusLine => HasImpossibleReason || IsAttaching;

    /// <summary>
    /// Is the button usable? <b>The answer is in the state, not in the module</b> — the rule for it
    /// lives in <see cref="ModuleActionRule"/>.
    /// </summary>
    public bool IsActionEnabled => Offer?.IsEnabled ?? false;

    /// <summary>
    /// The button itself. <b>It does not call the grip</b> but the handed-in run delegate — the
    /// <c>ModuleAttacher</c> stays internal to the Core (architecture.md §4.2).
    /// </summary>
    public IAsyncRelayCommand ActionCommand { get; }

    private async Task RunActionAsync()
    {
        // Asked again instead of read from the property: the game can end between drawing and
        // clicking. The run delegate never throws and reports by itself how it went.
        if (Offer is not { } offer)
        {
            return;
        }

        await _run(_game, Key, offer.Action.Kind);
    }

    /// <summary>
    /// The first row of the opened options (s13.1, owner 2026-10-04): <c>Attached · PID 24188</c>,
    /// or <c>null</c>. The start client is in the state; without it nothing is attached.
    /// </summary>
    public string? AttachedLine => State.Pid is { } pid ? $"Attached · PID {pid}" : null;

    /// <summary>Is the attached line there? A <c>bool</c> of its own, like <see cref="HasIcon"/>.</summary>
    public bool HasAttachedLine => AttachedLine is not null;

    /// <summary>
    /// The header row's switch: "Enable module for this game".
    ///
    /// <para>
    /// <b>The one exception</b> (Spec S5.4 E-4): it shows the <i>wish</i>, not the saved state — a
    /// switch that jumps back after flipping would be unusable. Tag and body therefore follow it
    /// only after saving.
    /// </para>
    ///
    /// <para>
    /// <b>The comparison before writing is necessary,</b> not convenient: a bound switch also
    /// reports the value it already has when the view is created. Without it every game switch
    /// would leave a wish and a message on the save bar.
    /// </para>
    ///
    /// <para>
    /// <b>Not possible means off, and the setter does nothing</b> (Spec S3.9 E-12): the registry has
    /// already switched the saved switch off on reading (E-13); the locked switch cannot stage a
    /// new wish.
    /// </para>
    /// </summary>
    public bool IsEnabledForGame
    {
        get => IsPossible && _registry.IsEnabledForIncludingUnsaved(Key, _appId);
        set
        {
            if (!IsPossible || value == IsEnabledForGame)
            {
                return;
            }

            _registry.StageEnabledFor(Key, _appId, value);
            Refresh();
        }
    }

    /// <summary>
    /// Re-announces the switch, because the <b>wish</b> changed outside the card (S2.3a, S3.8):
    /// <c>Discard</c> empties the wishes, and <see cref="GameDetailViewModel.SyncSections"/> calls
    /// in here.
    ///
    /// <para>
    /// <b>Only the switch</b> — since S5.4 a wish moves nothing else on the card (E-4). Whatever
    /// hangs on the valid state is announced by <see cref="SyncState"/>.
    /// </para>
    ///
    /// <para>
    /// <b>The card holds no state</b> (S1.4): everything is read on every access, so the value is
    /// already right. What is missing is only the announcement to the UI — the card otherwise gives
    /// it only when it staged the wish itself.
    /// </para>
    /// </summary>
    public void Refresh() => OnPropertyChanged(nameof(IsEnabledForGame));

    /// <summary>
    /// Re-announces everything that hangs on the verdict of <see cref="ModuleGameStates"/> — called
    /// when the source reported a change (S5.4). <b>All at once, not per property:</b> the state is
    /// one record, and whoever changes it changes it as a whole.
    /// </summary>
    public void SyncState()
    {
        OnPropertyChanged(nameof(Tag));
        OnPropertyChanged(nameof(IsTagOff));
        OnPropertyChanged(nameof(IsTagReady));
        OnPropertyChanged(nameof(IsTagRunning));
        OnPropertyChanged(nameof(IsTagNotPossible));
        OnPropertyChanged(nameof(IsPossible));
        OnPropertyChanged(nameof(IsSwitchEnabled));
        OnPropertyChanged(nameof(SwitchTooltip));
        OnPropertyChanged(nameof(ImpossibleReason));
        OnPropertyChanged(nameof(HasImpossibleReason));
        OnPropertyChanged(nameof(HasBody));
        OnPropertyChanged(nameof(HasOptions));
        OnPropertyChanged(nameof(IsActionRelease));
        OnPropertyChanged(nameof(ActionName));
        OnPropertyChanged(nameof(IsActionEnabled));
        OnPropertyChanged(nameof(IsAttaching));
        OnPropertyChanged(nameof(AttachedLine));
        OnPropertyChanged(nameof(HasAttachedLine));
        OnPropertyChanged(nameof(HasStatusLine));
        OnPropertyChanged(nameof(IsEnabledForGame));
        ActionCommand.NotifyCanExecuteChanged();
    }
}
