using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Input;
using Anvil.Core.Steam;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Anvil.App.ViewModels;

/// <summary>
/// A tile of the game grid (element description §3): name and platform word in its head, the
/// artwork, the module tags and the star in its status row. Neither the AppId nor the Proton
/// version are on the card — that belongs in the right column, deliberately.
///
/// <para>
/// <b>Three booleans instead of an icon key:</b> the right column still draws the platform as a
/// symbol — diamond and Tux are filled shapes, the question mark a line drawing. A single
/// <c>Path</c> with bound geometry cannot fill at one time and stroke at another; the mockup
/// solved it with three branches in the same way (Spec §9).
/// </para>
/// </summary>
public sealed partial class GameCardViewModel : ObservableObject
{
    /// <summary>The frame in the accent color follows this flag.</summary>
    [ObservableProperty]
    private bool _isSelected;

    /// <summary>
    /// The star in the footer follows this flag. The card <b>writes nothing</b> — the command
    /// sits on the <see cref="LibraryViewModel"/>, just like selection. That keeps it
    /// testable without a file system.
    /// </summary>
    [ObservableProperty]
    private bool _isFavorite;

    /// <summary>
    /// Is the game running right now? The card's status band follows it (spec s13.1), and on the
    /// right the state tag and the button.
    ///
    /// <para>
    /// <b>The card asks nothing.</b> The one truth about "what runs" is the watcher (Vision
    /// §3); the <see cref="LibraryViewModel"/> listens to it and sets the flag — just like
    /// <see cref="IsSelected"/> and <see cref="IsFavorite"/>. That keeps the card testable
    /// without /proc.
    /// </para>
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RunLabel))]
    [NotifyPropertyChangedFor(nameof(MenuRunLabel))]
    [NotifyPropertyChangedFor(nameof(StateLabel))]
    [NotifyPropertyChangedFor(nameof(CanRun))]
    [NotifyPropertyChangedFor(nameof(AccessibleName))]
    private bool _isRunning;

    /// <summary>
    /// Is a start or stop phase running for this game right now? Then the button is locked
    /// (project plan decision 3).
    ///
    /// <para>
    /// <b>One flag for both phases</b> (Spec E-E): for the display they are the same — both
    /// lock the button, neither changes its label. Which one is running is known only to the
    /// <see cref="LibraryViewModel"/>.
    /// </para>
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRun))]
    private bool _isBusy;

    /// <summary>
    /// The launch lock (Spec S4.5 §3.4): something is unsaved for this game, or it is being
    /// written right now.
    ///
    /// <para>
    /// <b>One flag for both occasions:</b> for the card they are the same — the reason is
    /// shown in the bar, and the button deliberately carries none under design requirement 21.
    /// Whoever sets the flag knows which of the two it was.
    /// </para>
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRun))]
    private bool _isLocked;

    /// <summary>
    /// May the one button be pressed? <b>The lock blocks starting, not ending</b> (Spec E-O): a
    /// running game with an open change must be endable — otherwise the user is stuck, because
    /// saving requires that no game runs.
    /// </summary>
    public bool CanRun => !IsBusy && (IsRunning || !IsLocked);

    /// <summary>
    /// What the base action triggers — in the application <c>LibraryViewModel.ToggleRunCommand</c>,
    /// with the card itself as the target. <b>Handed in as a value, not fetched through a
    /// <c>$parent</c> binding</b> (Spec S5.5 §3.2): exactly this climb out of the popup was the
    /// bug of 2026-09-06.
    /// </summary>
    private readonly ICommand _toggleRun;

    /// <summary>The module rows as the <see cref="LibraryViewModel"/> last handed them over.</summary>
    private IReadOnlyList<GameMenuRowViewModel> _moduleRows = [];

    /// <summary>
    /// The whole context menu of this card (Spec S5.5 §3.1): base action, the groups of the
    /// modules, "Show in Steam".
    ///
    /// <para>
    /// <b>The card builds only its base rows.</b> What the modules have to offer, it does not
    /// know — it knows neither the registry nor the state source and therefore stays testable
    /// without a file system (S1.4 line). The module rows are handed to it by the
    /// <see cref="LibraryViewModel"/>.
    /// </para>
    /// </summary>
    public ObservableCollection<GameMenuRowViewModel> MenuRows { get; } = [];

    internal GameCardViewModel(
        GameProfile profile, IImage? artwork, bool isFavorite, GameDetailViewModel detail,
        ICommand toggleRun)
    {
        Profile = profile;
        Artwork = artwork;
        _isFavorite = isFavorite;
        Detail = detail;
        _toggleRun = toggleRun;
        RebuildMenu();
    }

    /// <summary>The whole profile; the right column reads it through <see cref="Detail"/>.</summary>
    public GameProfile Profile { get; }

    /// <summary>The loaded image, or <c>null</c> for the neutral surface (Spec §8.3).</summary>
    public IImage? Artwork { get; }

    /// <summary>
    /// The right column of this game. The card itself shows none of it; it holds it so that
    /// there stays one object per game (Spec §5.1, E1).
    /// </summary>
    public GameDetailViewModel Detail { get; }

    /// <summary>
    /// The module chips of the footer, one per module that can act on this game (element
    /// description §3, Spec S5.3 §3.6).
    ///
    /// <para>
    /// <b>The card asks nothing</b>, just like <see cref="IsRunning"/>: it carries what is set
    /// on it. The one who fills the chips is the <see cref="LibraryViewModel"/> — it knows the
    /// registry and the state source, the card knows neither and therefore stays testable
    /// without a file system.
    /// </para>
    ///
    /// <para>
    /// <b>Empty means: no module can act here</b> — native game, unknown platform, never
    /// started, downgraded, or simply no module switched on. The absence is the most honest
    /// statement (element description §3).
    /// </para>
    /// </summary>
    public ObservableCollection<ModuleChipViewModel> Chips { get; } = [];

    public string Name => Profile.Game.Name;

    /// <summary>
    /// The card's name for a screen reader: <c>&lt;game&gt;, running</c> while it runs, else the
    /// game's name. The card carries no RUNNING tag since s13.1 — the green status band says it
    /// to the eye, this name says it to the ear (element description §3).
    /// </summary>
    public string AccessibleName => IsRunning ? $"{Name}, running" : Name;

    /// <summary>
    /// The platform as the word in the card's head (element description §3): the brand shows a
    /// state as a word, not as a symbol.
    /// </summary>
    public string PlatformWord => Profile.Platform switch
    {
        GamePlatform.Windows => "PROTON",
        GamePlatform.NativeLinux => "NATIVE",
        _ => "UNKNOWN",
    };

    public bool IsWindows => Profile.Platform == GamePlatform.Windows;

    public bool IsNativeLinux => Profile.Platform == GamePlatform.NativeLinux;

    public bool IsPlatformUnknown => Profile.Platform == GamePlatform.Unknown;

    /// <summary>The label of the one button in the right column.</summary>
    public string RunLabel => IsRunning ? "Stop game" : "Start game";

    /// <summary>
    /// The same entry in the context menu (S3.4), shorter than the button — the mockup labels
    /// it that way (<c>menuRunLabel</c>).
    /// </summary>
    public string MenuRunLabel => IsRunning ? "Stop" : "Start";

    /// <summary>
    /// The state pill at the top right of the artwork. It is <b>always</b> visible (element
    /// description §4) — "Stopped" is a statement too.
    /// </summary>
    public string StateLabel => IsRunning ? "Running" : "Stopped";

    /// <summary>What the icon means, in plain words — English, like the whole UI.</summary>
    public string PlatformTooltip => Profile.Platform switch
    {
        GamePlatform.Windows => "Runs through Proton",
        GamePlatform.NativeLinux => "Native Linux build",
        _ => "Platform could not be determined",
    };

    /// <summary>
    /// Takes the rows of the modules and rebuilds the menu (Spec S5.5 §3.3). <b>The card asks
    /// nothing</b>, just like with <see cref="Chips"/>: it carries what is set on it.
    /// </summary>
    internal void SetModuleRows(IReadOnlyList<GameMenuRowViewModel> rows)
    {
        _moduleRows = rows;
        RebuildMenu();
    }

    /// <summary>
    /// <b>One place instead of four</b>: label and operability of the base action hang on
    /// <see cref="MenuRunLabel"/> and <see cref="CanRun"/>, and those already report —
    /// <see cref="IsRunning"/>, <see cref="IsBusy"/> and <see cref="IsLocked"/> carry the
    /// references for it. Whoever rebuilt at each of the three places instead would forget one.
    /// </summary>
    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);

        if (e.PropertyName is nameof(MenuRunLabel) or nameof(CanRun))
        {
            RebuildMenu();
        }
    }

    /// <summary>
    /// <b>Clear and refill</b>, like with the chips (Spec S5.5 E-6): with a handful of rows, a
    /// second path that swaps single ones is more code than benefit. The visible price is in
    /// the spec — if the state changes while the menu is open, its rows are replaced under its
    /// hand.
    /// </summary>
    private void RebuildMenu()
    {
        MenuRows.Clear();

        // The base action, labelled briefly like in the mockup. Starting is ember, ending steel
        // blue — the same rule as for module rows ("ember starts, steel blue ends", design
        // delivery §2).
        MenuRows.Add(new GameMenuActionViewModel(
            MenuRunLabel, _toggleRun, this, CanRun,
            isRelease: IsRunning, isStart: !IsRunning));

        foreach (var row in _moduleRows)
        {
            MenuRows.Add(row);
        }

        MenuRows.Add(new GameMenuRuleViewModel());
        MenuRows.Add(new GameMenuActionViewModel(
            "Show in Steam", Detail.ShowInSteamCommand, null, isEnabled: true, isRelease: false));
    }
}
