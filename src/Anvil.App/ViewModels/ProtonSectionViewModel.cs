using Anvil.Core.Steam;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;

namespace Anvil.App.ViewModels;

/// <summary>
/// One entry of the Proton list (element description §5): name on the left, source on the right.
/// <c>Key</c> is the key for <c>CompatToolMapping</c>; <c>null</c> means "Auto".
///
/// <para>
/// <b>The name collides with <see cref="Anvil.Core.Steam.ProtonChoice"/></b>, the wish type
/// from S4.3 (Spec S4.6 E-D). Both are named for what they are; the one place where both
/// occur qualifies. An artificial name would be worse than one qualified line.
/// </para>
/// </summary>
public sealed record ProtonChoice(string Name, string Source, string? Key)
{
    public bool IsAuto => Key is null;

    /// <summary>The closed field shows the name — a record's default ToString would name all
    /// three values.</summary>
    public override string ToString() => Name;
}

/// <summary>
/// The Proton module's section in the GAME card (Spec S4.6 §3.3): the choice of the Proton
/// version and the launch options — the <b>only source of changes</b> there is in v1.
///
/// <para>
/// <b>The field shows the configured choice, the line below it the version that ran</b>
/// (project plan M4, decision 10). That is two truths, and after a change they contradict each
/// other until the next game start — which is exactly what the origin line then says, too.
/// </para>
///
/// <para>
/// <b>The section does not listen to <see cref="PendingChanges.Changed"/></b> (Spec E-G): that
/// event comes from a background thread after a save, and the save bar is the only place that
/// posts to the UI thread (S4.5 E-M). Whoever wants the fields reset calls <see cref="Sync"/> —
/// the library does that, from the save bar's event.
/// </para>
///
/// <para>
/// <b>The three folds</b> (spec s11.1) read their unsaved dot straight from
/// <see cref="PendingChanges"/> — no bookkeeping of their own, so they cannot disagree with the
/// save bar. They hold no open state either; it lives in the shared <see cref="FoldMemory"/>.
/// </para>
/// </summary>
public sealed partial class ProtonSectionViewModel : ObservableObject
{
    private const string AutoName = "Auto";
    private const string AutoSource = "Steam decides";
    private const string ValveSource = "Valve";
    private const string ExternalSource = "compatibilitytools.d";
    private const string MissingSource = "not installed";

    private const string NativeReason = "Native Linux build — runs without Proton";
    private const string UnknownReason = "Platform unknown — rescan the library to determine it";
    private const string UnsetReason = "No Proton prefix yet — Steam picks one on the next launch";

    private const string UnsetHint =
        "No Proton version set yet — pick one to fix it before the next launch";

    private readonly GameProfile _game;
    private readonly PendingChanges _changes;

    /// <summary>
    /// The user's own input, as recognised from Steam's value at construction — and thereby the
    /// reset value for <see cref="Sync"/>. <b>Computed once</b> (plan P-2): it depends on the
    /// contributions of all registered modules, and those know no switch.
    /// </summary>
    private readonly string _userOptions;

    private readonly Func<IReadOnlyList<LaunchOptionContribution>> _activeContributions;

    /// <summary>The section's own module key (S4.8 §3.1) — every wish names it.</summary>
    private readonly string _moduleKey;

    private readonly ILogger? _logger;

    /// <summary>
    /// Stops the reporting paths while <see cref="Sync"/> resets the fields — otherwise the reset
    /// would report exactly the wish that was just discarded.
    /// </summary>
    private bool _syncing;

    /// <summary>The origin line hangs on the choice, not on the profile (Spec §3.3.5).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProtonOrigin))]
    private ProtonChoice _selectedChoice;

    [ObservableProperty]
    private string _launchOptions;

    /// <summary>
    /// What Anvil would write to Steam for this game — the read-only second field of the GAME
    /// card (S2.4, design requirement 31).
    ///
    /// <para>
    /// <b>It is not a second truth.</b> The value is set exclusively in <see cref="Recompose"/>,
    /// and the same computation delivers the wish to <see cref="PendingChanges"/>. Whoever adds a
    /// second writer here has two values that can drift apart — and the field exists precisely
    /// because one wants to see the one value.
    /// </para>
    ///
    /// <para>
    /// <b>Stored nowhere</b> (Spec E-A): it is recomputed on every card build. A copy on disk
    /// would be wrong after every outside intervention — the same reasoning as "recognised, not
    /// remembered" (project plan M2, decision 2).
    /// </para>
    /// </summary>
    [ObservableProperty]
    private string _generatedLaunchOptions;

    /// <param name="allContributions">
    /// What <b>all registered</b> modules could contribute for this game, without any switch —
    /// from it the user's own input is recognised at construction (S2.3). A fixed list, because
    /// it cannot change during the section's lifetime.
    /// </param>
    /// <param name="activeContributions">
    /// What the modules <b>active</b> for this game contribute — what gets drawn comes from it. A
    /// delegate, because it changes with every per-game switch while the section stands (Spec
    /// E-C).
    /// </param>
    /// <param name="moduleKey">
    /// The key of the module that provides this section — <c>ProtonModule.Key</c>. Every wish
    /// names it (S4.8 §3.1); the backup list later shows it as a column.
    /// </param>
    /// <param name="folds">
    /// Where the three folds keep their open state, shared with the other sections of the module
    /// so it survives a game switch. Null means a private memory of its own.
    /// </param>
    public ProtonSectionViewModel(
        GameProfile game,
        CompatToolCatalog catalog,
        PendingChanges changes,
        IReadOnlyList<LaunchOptionContribution> allContributions,
        Func<IReadOnlyList<LaunchOptionContribution>> activeContributions,
        string moduleKey,
        ILogger? logger = null,
        FoldMemory? folds = null)
    {
        _game = game;
        _changes = changes;
        _moduleKey = moduleKey;
        _logger = logger;

        Choices = BuildChoices(game, catalog);

        // The fields are set here and not through the properties: that would run the reporting
        // path, and building a card is not a wish of the user.
        _selectedChoice = ChoiceFor(game.CompatToolKey);
        _activeContributions = activeContributions;
        _userOptions = LaunchOptionsComposer.ExtractUserOptions(
            game.LaunchOptions ?? string.Empty, allContributions);
        _launchOptions = _userOptions;

        // Straight into the field, not through the property: building a card triggers no
        // reporting path (the same rule as two lines above). Still computed through Recompose —
        // one place that draws. The return value is irrelevant here; the side effect is what is
        // needed, which is exactly why the assignment stands here once more.
        _generatedLaunchOptions = Recompose(_userOptions);

        var memory = folds ?? new FoldMemory();
        ProtonFold = new FoldViewModel(
            memory,
            FoldMemory.ProtonKey,
            "Proton version",
            () => HasUnsavedWish(change => change.Proton is not null),
            () => IsProtonLocked ? NotApplicableValue : SelectedChoice.Name,
            () => IsProtonLocked);
        CustomLaunchOptionsFold = new FoldViewModel(
            memory,
            FoldMemory.CustomLaunchOptionsKey,
            "Custom launch options",
            HasUnsavedLaunchOptions);
        GeneratedLaunchOptionsFold = new FoldViewModel(
            memory,
            FoldMemory.GeneratedLaunchOptionsKey,
            "Generated launch options",
            HasUnsavedLaunchOptions);

        _logger?.LogDebug(
            "section built: appid {AppId} ({Count} choices)", game.Game.AppId, Choices.Count);
    }

    /// <summary>What the Proton fold shows while closed for a game that does not use Proton.</summary>
    public const string NotApplicableValue = "not applicable";

    /// <summary>The fold of the Proton version choice.</summary>
    public FoldViewModel ProtonFold { get; }

    /// <summary>The fold of the user's own launch options field.</summary>
    public FoldViewModel CustomLaunchOptionsFold { get; }

    /// <summary>The fold of the generated launch options field.</summary>
    public FoldViewModel GeneratedLaunchOptionsFold { get; }

    /// <summary>"Auto" on top, then the selectable tools by name.</summary>
    public IReadOnlyList<ProtonChoice> Choices { get; }

    /// <summary>The design's lock template: native and unknown games choose nothing.</summary>
    public bool IsProtonLocked => _game.Platform is not GamePlatform.Windows;

    /// <summary>
    /// The line below the field — the <b>second truth</b>: which version actually ran. It is
    /// never empty; every case has its sentence (element description §5).
    /// </summary>
    public string ProtonOrigin => _game.Platform switch
    {
        GamePlatform.Unknown => UnknownReason,
        GamePlatform.NativeLinux => NativeReason,
        _ => WindowsOrigin(),
    };

    /// <summary>
    /// The tooltip on the field. <c>null</c> in the normal case — an empty tooltip box is worse
    /// than none (S2.6).
    /// </summary>
    public string? ProtonTooltip => _game.Platform switch
    {
        GamePlatform.Unknown => UnknownReason,
        GamePlatform.NativeLinux => NativeReason,
        _ => _game.Proton is null ? UnsetHint : null,
    };

    /// <summary>
    /// A module was toggled for this game — the wish is redrawn (S2.3).
    ///
    /// <para>
    /// <b>The text field is not touched:</b> the user's own input does not change through a
    /// module switch, only the drawn value does. That is why this path needs no
    /// <c>_syncing</c> guard either.
    /// </para>
    ///
    /// <para>
    /// <b>The library calls this</b>, not the registry's event (Spec E-D): the registry lives as
    /// long as the application, a section only until the next rescan — if every section hung on
    /// the event itself, the sections of past rescans would keep reporting forever.
    /// </para>
    /// </summary>
    public void ModuleToggled(string moduleKey)
    {
        Stage(moduleKey, LaunchOptions.Trim());
        SyncFolds();
        _logger?.LogInformation(
            "launch options recomposed: appid {AppId} after {Module} was toggled",
            _game.Game.AppId, moduleKey);
    }

    /// <summary>
    /// Resets the fields to the profile when this game has no open wish any more — the way back
    /// after <c>Discard</c>. While a wish still stands, everything stays.
    /// </summary>
    public void Sync()
    {
        // Before the early return: a wish that is still open changes the dots all the same, and
        // the folds are the place that has to hear about it.
        SyncFolds();

        if (_changes.Contains(_game.Game.AppId))
        {
            return;
        }

        // With no wish open any more, the generated value is by definition the one from the own
        // input and the now active contributions (S2.4). Before the early return below, because
        // the case "only a module switch was flipped and then discarded" leaves choice and text
        // untouched and would otherwise not be cleaned up.
        Recompose(_userOptions);

        var choice = ChoiceFor(_game.CompatToolKey);
        var options = _userOptions;
        if (choice == SelectedChoice && options == LaunchOptions)
        {
            return;
        }

        _syncing = true;
        try
        {
            SelectedChoice = choice;
            LaunchOptions = options;
        }
        finally
        {
            _syncing = false;
        }

        _logger?.LogDebug("fields reset: appid {AppId}", _game.Game.AppId);
    }

    partial void OnSelectedChoiceChanged(ProtonChoice value)
    {
        if (value is null)
        {
            // The ComboBox reports null when its selection falls away — that means "Auto"
            // (the same pattern as the library's filter pill, S2.5).
            SelectedChoice = Choices[0];
            return;
        }

        if (_syncing)
        {
            // The value shown after Discard has to be the restored one.
            ProtonFold.Sync();
            return;
        }

        _changes.SetProton(_moduleKey, _game.Game.AppId, new Anvil.Core.Steam.ProtonChoice(value.Key));
        ProtonFold.Sync();
        _logger?.LogInformation(
            "proton choice: appid {AppId} → {Key}", _game.Game.AppId, value.Key ?? "auto");
    }

    partial void OnLaunchOptionsChanged(string value)
    {
        if (_syncing)
        {
            return;
        }

        // Trimmed, otherwise verbatim (project plan M4, decision 11). The field keeps what was
        // typed — taking the space from under the user's finger would be rude. What gets
        // reported from now on is the drawn value, not the typed one (S2.3).
        Stage(_moduleKey, value.Trim());
        SyncFolds();
        _logger?.LogDebug(
            "launch options changed: appid {AppId} ({Length} chars)",
            _game.Game.AppId,
            value.Length);
    }

    /// <summary>
    /// Draws the value for Steam from the own input and the active contributions, puts it into
    /// <see cref="GeneratedLaunchOptions"/> and returns it (S2.4).
    ///
    /// <para>
    /// <b>The one place where drawing happens.</b> Shown and reported value therefore cannot
    /// drift apart: it is the same call. Whoever later computes the display "quickly" elsewhere
    /// builds the second truth this field is meant to make visible.
    /// </para>
    ///
    /// <para>
    /// <b>Warnings go to the log on card build, too</b> (Spec E-B). A contradiction needs two
    /// modules that set the same variable differently — in M2 there is a single contribution in
    /// the whole program. An exception for the build would be a second behaviour for a case that
    /// does not exist.
    /// </para>
    /// </summary>
    private string Recompose(string userOptions)
    {
        var warnings = new List<string>();
        var composed = LaunchOptionsComposer.Compose(
            userOptions, _activeContributions(), warnings);

        // A surface for this would be a surface for a case that does not exist (Spec S2.3 §3.5).
        foreach (var warning in warnings)
        {
            _logger?.LogWarning("launch option conflict: {Warning}", warning);
        }

        GeneratedLaunchOptions = composed;
        return composed;
    }

    /// <summary>
    /// Draws the value for Steam from the own input and the active contributions and reports it
    /// as a wish (S2.3). <b>The one path for both triggers</b> — typing and toggling (plan P-3):
    /// two copies would be two places where the drawing can go wrong.
    ///
    /// <para>
    /// <b>Always drawn anew from the own input</b>, never from the last drawn value. That is why
    /// switching on twice cannot double anything: it is the same computation twice with the same
    /// input.
    /// </para>
    ///
    /// <para>
    /// <b>No comparison with the current state</b> (Spec E-H):
    /// <see cref="PendingChanges.SetLaunchOptions"/> sorts that out itself — and the save bar
    /// disappearing on its own after switching on and off again hangs on exactly that.
    /// </para>
    ///
    /// <para>
    /// <b>The computing happens in <see cref="Recompose"/></b>, which also makes the value
    /// displayable (S2.4). This method only passes it on.
    /// </para>
    /// </summary>
    /// <param name="moduleKey">
    /// Who reports the wish: when typing, this module; when toggling, the <b>toggled</b> one
    /// (project plan M2, decision 4 — the backup note answers "who touched this").
    /// </param>
    private void Stage(string moduleKey, string userOptions)
        => _changes.SetLaunchOptions(moduleKey, _game.Game.AppId, Recompose(userOptions));

    /// <summary>
    /// The three sentences from element description §5, in this order. <b>Tools</b> are compared,
    /// not versions: if the tool that ran cannot be resolved, the third sentence applies and names
    /// the version from <c>config_info</c> — the best information available.
    /// </summary>
    private string WindowsOrigin()
    {
        if (_game.Proton is not { } proton)
        {
            return UnsetReason;
        }

        var used = proton.Tool?.Name ?? proton.Version;

        if (SelectedChoice.IsAuto)
        {
            return $"Steam picked {used} on the last launch";
        }

        if (proton.Tool is { Key: { } key }
            && string.Equals(key, SelectedChoice.Key, StringComparison.Ordinal))
        {
            return "In use since the last launch";
        }

        return $"Not in use yet — last launch used {used}";
    }

    /// <summary>
    /// "Auto", then — if the choice points at an uninstalled tool — that key itself, then the
    /// catalogue (Spec E-E). Without the middle entry the field could not show its own current
    /// value.
    /// </summary>
    private static IReadOnlyList<ProtonChoice> BuildChoices(
        GameProfile game, CompatToolCatalog catalog)
    {
        var choices = new List<ProtonChoice> { new(AutoName, AutoSource, null) };

        if (game.CompatToolKey is { } key && catalog.FindByKey(key) is null)
        {
            choices.Add(new ProtonChoice(key, MissingSource, key));
        }

        foreach (var tool in catalog.Selectable)
        {
            choices.Add(new ProtonChoice(
                tool.Name,
                tool.Source == CompatToolSource.SteamApp ? ValveSource : ExternalSource,
                tool.Key));
        }

        return choices;
    }

    /// <summary>Tells the three folds that a source behind their delegates changed.</summary>
    private void SyncFolds()
    {
        ProtonFold.Sync();
        CustomLaunchOptionsFold.Sync();
        GeneratedLaunchOptionsFold.Sync();
    }

    private bool HasUnsavedLaunchOptions()
        => HasUnsavedWish(change => change.LaunchOptions is not null);

    private bool HasUnsavedWish(Func<SteamGameChange, bool> touchesField)
        => _changes.Current.Any(
            change => change.AppId == _game.Game.AppId && touchesField(change));

    /// <summary>The entry for a key; without a key and without a match "Auto".</summary>
    private ProtonChoice ChoiceFor(string? key)
        => key is null
            ? Choices[0]
            : Choices.FirstOrDefault(
                choice => string.Equals(choice.Key, key, StringComparison.Ordinal)) ?? Choices[0];
}
