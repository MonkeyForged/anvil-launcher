using Anvil.Core.Modules;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Anvil.App.ViewModels;

/// <summary>
/// The section of the module card about a bridge's per-game options (Spec S5.4 §3.6):
/// "Attach on game launch", "Open this game in &lt;App&gt;" and "Quit &lt;App&gt; with the
/// game" — the order is the statement — when the application comes, where it opens, when it
/// goes; that is why none carries a sub-line. <b>The Core draws the frame of the card</b> —
/// this view model only supplies the content, the same division of labour as
/// <see cref="WandBridgeSettingsViewModel"/> for the settings card.
///
/// <para>
/// <b>One content for every bridge:</b> the labels are built from the application's name, and
/// the middle box is shown only when the bridge offers it (<see cref="OffersOpenOnGame"/>) —
/// both bridges do today; the parameter lets a bridge that cannot open its application on a game
/// hide the box.
/// </para>
///
/// <para>
/// <b>No own state</b> — following the pattern of
/// <see cref="ModuleCardViewModel.IsEnabledForGame"/>: the getter asks the delegate, the setter
/// compares against the current value and only stages a wish on a real change.
/// <b>The comparison is necessary, not just convenient:</b> a bound checkbox also reports the
/// value it already has when the view is created — without the comparison, every game switch
/// would leave a wish and a message on the save bar.
/// </para>
/// </summary>
public sealed class BridgeSectionViewModel : ObservableObject
{
    private readonly uint _appId;
    private readonly Func<uint, ModuleGameOption, bool> _shownOption;
    private readonly Action<uint, ModuleGameOption, bool> _stageOption;

    /// <inheritdoc cref="AttachOnLaunch"/>
    public const string AttachOnLaunchLabel = "Attach on game launch";

    /// <summary>The label of <see cref="OpenOnGame"/> for an application — also used by log lines.</summary>
    public static string OpenOnGameLabelFor(string applicationName)
        => $"Open this game in {applicationName}";

    /// <summary>The label of <see cref="QuitWithGame"/> for an application.</summary>
    public static string QuitWithGameLabelFor(string applicationName)
        => $"Quit {applicationName} with the game";

    /// <param name="appId">The game this section belongs to — one card per game (S1.4).</param>
    /// <param name="applicationName">The application the bridge attaches ("Wand", "CE").</param>
    /// <param name="offersOpenOnGame">
    /// Whether the bridge can open its application on the game — the middle box is shown only
    /// then.
    /// </param>
    /// <param name="shownOption">
    /// What the checkboxes show — the wish, else the stored value
    /// (<see cref="ModuleGameOptions.Displayed"/>), narrowed to the module's key and handed in
    /// by the module (Spec E-8).
    /// </param>
    /// <param name="stageOption">
    /// Stages a wish (<see cref="ModuleGameOptions.Stage"/>) when a checkbox is ticked — handed
    /// in narrowed the same way.
    /// </param>
    public BridgeSectionViewModel(
        uint appId,
        string applicationName,
        bool offersOpenOnGame,
        Func<uint, ModuleGameOption, bool> shownOption,
        Action<uint, ModuleGameOption, bool> stageOption)
    {
        _appId = appId;
        ApplicationName = applicationName;
        OffersOpenOnGame = offersOpenOnGame;
        OpenOnGameLabel = OpenOnGameLabelFor(applicationName);
        QuitWithGameLabel = QuitWithGameLabelFor(applicationName);
        _shownOption = shownOption;
        _stageOption = stageOption;
    }

    /// <summary>
    /// The application the bridge attaches ("Wand", "CE") — the module card names its action
    /// after it (s11.1: "Attach CE", not "Attach CE Bridge").
    /// </summary>
    public string ApplicationName { get; }

    /// <summary>"Open this game in &lt;App&gt;" — the label of <see cref="OpenOnGame"/>.</summary>
    public string OpenOnGameLabel { get; }

    /// <summary>"Quit &lt;App&gt; with the game" — the label of <see cref="QuitWithGame"/>.</summary>
    public string QuitWithGameLabel { get; }

    /// <summary>Whether the bridge offers <see cref="OpenOnGame"/> — the box is hidden otherwise.</summary>
    public bool OffersOpenOnGame { get; }

    /// <summary>"Attach on game launch" — attach by itself when the game starts. Default: off.</summary>
    public bool AttachOnLaunch
    {
        get => _shownOption(_appId, ModuleGameOption.AttachOnLaunch);
        set => Stage(ModuleGameOption.AttachOnLaunch, value, nameof(AttachOnLaunch));
    }

    /// <summary>"Open this game in &lt;App&gt;" — attaching opens the application on this game's page. Default: off.</summary>
    public bool OpenOnGame
    {
        get => _shownOption(_appId, ModuleGameOption.OpenOnGame);
        set => Stage(ModuleGameOption.OpenOnGame, value, nameof(OpenOnGame));
    }

    /// <summary>"Quit &lt;App&gt; with the game" — detach by itself when the session ends. Default: on.</summary>
    public bool QuitWithGame
    {
        get => _shownOption(_appId, ModuleGameOption.DetachOnExit);
        set => Stage(ModuleGameOption.DetachOnExit, value, nameof(QuitWithGame));
    }

    private void Stage(ModuleGameOption option, bool value, string propertyName)
    {
        if (value == _shownOption(_appId, option))
        {
            return;
        }

        _stageOption(_appId, option, value);
        OnPropertyChanged(propertyName);
    }

    /// <summary>
    /// Reports all three properties anew, because their value changed <b>outside</b> of this
    /// section — the way back after <c>Discard</c>, the same role as
    /// <see cref="ModuleCardViewModel.Refresh"/>.
    /// </summary>
    public void Sync()
    {
        OnPropertyChanged(nameof(AttachOnLaunch));
        OnPropertyChanged(nameof(OpenOnGame));
        OnPropertyChanged(nameof(QuitWithGame));
    }
}
