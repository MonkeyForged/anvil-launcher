using System.Collections.ObjectModel;
using Anvil.Core;
using Anvil.Core.Modules;
using Anvil.Core.Sessions;
using Anvil.Core.Steam;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace Anvil.App.ViewModels;

/// <summary>
/// The right column of a game (element description §4 and §5): header row, Proton state, launch
/// options. A plain translation of <see cref="GameProfile"/> into display texts.
///
/// <para>
/// <b>Why next to the card and not inside it:</b> the tile shows four things, the right column
/// twelve. Still it stays <b>one object per game</b> — the card holds its detail, and
/// <c>SelectedCard</c> stays the only truth about the selection (Spec §5.1, E1).
/// </para>
///
/// <para>
/// <b>Almost everything computed.</b> A <see cref="GameProfile"/> never changes; a rescan builds
/// new cards and with them new details. Since S1.4 exactly one thing is observable: the list of
/// module cards. It hangs on the global switch of the Modules page and can change while the card
/// stands.
/// </para>
/// </summary>
public sealed partial class GameDetailViewModel : ObservableObject
{
    /// <summary>
    /// Opens a path or an address through the system (<c>DesktopShell.Open</c>). Until S3.3b it was
    /// called <c>openFolder</c>; since "Show in Steam" it also passes Steam addresses on, and the
    /// name must not lie (Spec S3.4 E-F).
    /// </summary>
    private readonly Action<string> _open;

    /// <summary>
    /// Brings Steam's window to the front (<c>SteamWindow.Raise</c>, S3.8). KWin refuses Steam's own
    /// request for it; the card knows nothing about that — it says "Steam to the front", the Core
    /// knows how (Spec S3.8 E-A).
    /// </summary>
    private readonly Func<RaiseOutcome> _raiseSteamWindow;

    private readonly ILogger? _logger;

    /// <summary>
    /// The one truth about modules. <b>As an object, not as a finished list</b> (Plan S1.4, E-5):
    /// a card's section list never changes during its lifetime, the list of module cards does — it
    /// hangs on the global switch.
    /// </summary>
    private readonly ModuleRegistry _modules;

    /// <summary>
    /// The one truth about "how does this module stand on this game" (S5.2) — handed on to every
    /// module card, which since S5.4 no longer judges by itself.
    /// </summary>
    private readonly ModuleGameStates _states;

    /// <summary>
    /// Runs a module action (in the root <c>ModuleActionRunner.RunAsync</c>) — handed on to every
    /// module card. The <c>ModuleAttacher</c> stays internal to the Core (architecture.md §4.2).
    /// </summary>
    private readonly Func<GameProfile, string, ModuleActionKind, Task> _runModuleAction;

    /// <summary>
    /// The library's one fold memory, or <c>null</c> — handed on to every module card (s11.1).
    /// </summary>
    private readonly FoldMemory? _folds;

    /// <summary>
    /// The wishes behind the options folds' dots, or <c>null</c> — handed on to every module card
    /// (s11.1).
    /// </summary>
    private readonly PendingChanges? _changes;

    internal GameDetailViewModel(
        GameProfile profile,
        Action<string> open,
        Func<RaiseOutcome> raiseSteamWindow,
        IReadOnlyList<object> sections,
        ModuleRegistry modules,
        ModuleGameStates states,
        Func<GameProfile, string, ModuleActionKind, Task> runModuleAction,
        ILogger? logger = null,
        FoldMemory? folds = null,
        PendingChanges? changes = null)
    {
        Profile = profile;
        _open = open;
        _raiseSteamWindow = raiseSteamWindow;
        Sections = sections;
        _modules = modules;
        _states = states;
        _runModuleAction = runModuleAction;
        _logger = logger;
        _folds = folds;
        _changes = changes;
        SyncModuleCards();
    }

    /// <summary>The whole profile — M4 fetches the write path here.</summary>
    public GameProfile Profile { get; }

    /// <summary>
    /// The sections of the active modules for this game (S4.6). The Core draws them without knowing
    /// them — a <c>DataTemplate</c> over the concrete type does the mapping.
    /// </summary>
    public IReadOnlyList<object> Sections { get; }

    /// <summary>
    /// The module cards of the right column (S1.4), under the divider <c>MODULES</c> — one per
    /// module that is switchable per game and globally active. Globally disabled modules do
    /// <b>not appear at all</b>; the per-game switch leaves the card standing and is itself off
    /// (S1.2, <c>IsEnabledFor_DefaultsToFalse</c>).
    /// </summary>
    public ObservableCollection<ModuleCardViewModel> ModuleCards { get; } = [];

    /// <summary>Carries the divider's visibility — no cards, no divider.</summary>
    public bool HasModuleCards => ModuleCards.Count > 0;

    /// <summary>
    /// Rebuilds the module cards. The library calls it when a module's global switch was flipped or
    /// its finding changed (S3.4). <b>Empty and refill</b>, like <c>Refresh</c> in the
    /// <see cref="LibraryViewModel"/>: with at most a handful of cards, a second path that inserts
    /// single ones is more code than benefit.
    ///
    /// <para>
    /// <b>Three conditions, not two</b> (Vision §4.8, Spec S3.4 §3.5): a globally enabled but not
    /// ready module shows <b>no card at all</b> here — the same rule as for globally disabled. The
    /// reason for the missing readiness stands once, globally, on the Modules page; repeating it
    /// here per game would be a second signal for one value.
    /// </para>
    ///
    /// <para>
    /// <b>A pending finding (<c>null</c>) does not count as ready.</b> The card appears when the
    /// check is through, not before — a card that disappears again after a few seconds is worse
    /// than one that arrives a few seconds later.
    /// </para>
    /// </summary>
    public void SyncModuleCards()
    {
        ModuleCards.Clear();

        foreach (var module in _modules.Modules)
        {
            if (module.IsPerGame
                && _modules.IsEnabled(module.Key)
                && _modules.ReadinessOf(module.Key) is { IsReady: true })
            {
                ModuleCards.Add(
                    new ModuleCardViewModel(
                        module, _modules, Profile, _states, _runModuleAction, _folds, _changes));
            }
        }

        OnPropertyChanged(nameof(HasModuleCards));
    }

    /// <summary>
    /// Brings the sections' fields and the module cards up to date after the set of wishes
    /// changed — the way back after <c>Discard</c>.
    ///
    /// <para>
    /// <b>The cards have been part of it since S3.8</b> (Spec §3.3): a per-game switch is now a
    /// wish, and <c>Discard</c> empties it without the registry reporting anything. The card
    /// already reads the value that fell back — but the surface must hear about it from here.
    /// Since s11.1 the same holds for the dot on a card's options fold.
    /// </para>
    ///
    /// <para>
    /// <b>The pattern match on the concrete type is deliberate</b> (Spec S4.6 E-J): the Core does
    /// not know the section type, the App does. An <c>ISyncable</c> interface in the contract
    /// would be the second empty interface for the same problem — it comes into being when a
    /// second module needs it.
    /// </para>
    /// </summary>
    public void SyncSections()
    {
        foreach (var section in Sections)
        {
            if (section is ProtonSectionViewModel proton)
            {
                proton.Sync();
            }
        }

        foreach (var card in ModuleCards)
        {
            card.Refresh();
            card.OptionsFold.Sync();

            // Since S5.4 (Spec E-1) the section of a module card belongs to the card — and with it
            // its way back after Discard. The same pattern match as above, for the same reason:
            // the Core does not know the section type, the App does.
            if (card.Section is BridgeSectionViewModel section)
            {
                section.Sync();
            }
        }
    }

    /// <summary>
    /// The one truth reported a change to this module — this module's cards redraw (S5.4). Called
    /// by the library, the only one listening to the source.
    ///
    /// <para>
    /// <b>Per module, not per card:</b> the report names game and module; the library has already
    /// picked the game when it called in here.
    /// </para>
    /// </summary>
    public void SyncModuleState(string moduleKey)
    {
        foreach (var card in ModuleCards)
        {
            if (card.Key == moduleKey)
            {
                card.SyncState();
            }
        }
    }

    /// <summary>
    /// A module was toggled for this game — the sections redraw their wish (S2.3). Called by the
    /// library, the only one listening to the registry's event. <b>The same pattern match as
    /// <see cref="SyncSections"/></b> and for the same reason (Spec S4.6 E-J): the Core does not
    /// know the section type, the App does.
    ///
    /// <para>
    /// <b>Two receivers since S2.3a:</b> the sections, which redraw, and this module's cards. The
    /// card is called here because it stages by itself and other cards of the same module know
    /// nothing about it. Resetting via <c>Discard</c> no longer runs through here since S3.8, but
    /// through <see cref="SyncSections"/>.
    /// </para>
    /// </summary>
    public void ModuleToggled(string moduleKey)
    {
        foreach (var section in Sections)
        {
            if (section is ProtonSectionViewModel proton)
            {
                proton.ModuleToggled(moduleKey);
            }
        }

        foreach (var card in ModuleCards)
        {
            if (card.Key == moduleKey)
            {
                card.Refresh();
            }
        }
    }

    /// <summary>
    /// The header row of the right column, with the Proton state as a suffix. Until S2.5 it stood
    /// on the card as <c>AppIdLine</c> — since S2.6 it exists only here (Spec E11).
    /// </summary>
    public string MetaLine => $"appid {Profile.Game.AppId} · {ProtonState}";

    /// <summary>Without a proven prefix, "Open prefix" is locked (Spec §7.3).</summary>
    public bool HasPrefix => Profile.PrefixPath is not null;

    /// <summary>
    /// The suffix in the header row. <b>The order is the point</b>, not the set: a native game with
    /// <c>compatdata</c> is possible, and then "native" wins, not the version (Spec §3).
    /// </summary>
    private string ProtonState => Profile.Platform switch
    {
        GamePlatform.Unknown => "platform unknown",
        GamePlatform.NativeLinux => "native",
        _ => Profile.Proton is { } proton ? proton.Version : "no proton set",
    };

    /// <summary>
    /// Opens the game folder. <b>Without an existence check</b> (Spec E3): the manifest says
    /// "installed"; if the folder is missing, the Steam installation is broken, and a grey button
    /// explains that worse than a line in the log.
    /// </summary>
    [RelayCommand]
    private void OpenInstallFolder() => Open(Profile.Game.InstallDirectory, "install folder");

    /// <summary>
    /// Opens the Wine prefix. Locked where there is none — unlike the game folder this is a
    /// measured normal state (five games of the owner's library), not a defect.
    /// </summary>
    [RelayCommand(CanExecute = nameof(HasPrefix))]
    private void OpenPrefix()
    {
        if (Profile.PrefixPath is { } path)
        {
            Open(path, "prefix");
        }
    }

    /// <summary>
    /// The game page in Steam's library (S3.4), and since S3.8 its window to the front. Never
    /// locked — the address exists for every game of the library.
    ///
    /// <para>
    /// <b>Open first, then raise, immediately</b> (Spec S3.8 E-D): if Steam runs, the window is
    /// already there, and activation does not depend on navigation. If it does not run, the
    /// address starts Steam, and the new window takes the focus by itself — then the delegate
    /// reports <c>NoWindow</c>, and that is not an error.
    /// </para>
    /// </summary>
    [RelayCommand]
    private void ShowInSteam()
    {
        var appId = Profile.Game.AppId;
        _logger?.LogInformation("show in steam: appid {AppId}", appId);
        Open(SteamUrl.GameDetails(appId), "steam page");
        RaiseSteamWindow();
    }

    /// <summary>
    /// The delegate talks to the X server — if it fails, it must not take the application down
    /// with it (pattern <see cref="Open"/>). Every outcome has its line: the owner sees in the log
    /// why Steam is in front or not.
    /// </summary>
    private void RaiseSteamWindow()
    {
        try
        {
            switch (_raiseSteamWindow())
            {
                case RaiseOutcome.Raised:
                    _logger?.LogDebug("raised the steam window");
                    break;
                case RaiseOutcome.NoWindow:
                    // Exactly one thing is measured: no matching window is in _NET_CLIENT_LIST. The
                    // old parenthetical guessed "steam not running yet?" and was wrong for the owner
                    // — Steam was running (docs/architecture.md §5, side finding of 2026-09-12).
                    _logger?.LogInformation(
                        "no steam window in the x11 client list (starting up, or not an x11 client)");
                    break;
                default:
                    _logger?.LogWarning("cannot raise windows: no x11 display");
                    break;
            }
        }
        catch (Exception error)
        {
            _logger?.LogWarning("could not raise the steam window: {Reason}", error.Message);
        }
    }

    /// <summary>
    /// The delegate starts a foreign process — if it fails, it must not take the application down
    /// with it. The same pattern as <c>LibraryViewModel.LoadOrNothing</c>.
    /// </summary>
    private void Open(string path, string what)
    {
        try
        {
            _open(path);
            _logger?.LogDebug("opened {What}: {Path}", what, path);
        }
        catch (Exception error)
        {
            _logger?.LogWarning("could not open {Path}: {Reason}", path, error.Message);
        }
    }
}
