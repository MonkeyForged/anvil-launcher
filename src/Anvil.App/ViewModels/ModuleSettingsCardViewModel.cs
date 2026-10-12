using Anvil.Core.Modules;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Anvil.App.ViewModels;

/// <summary>
/// The frame of a module card on the Settings page (S3.4, Spec E-8): chevron, title, key, tag —
/// and inside it the section the module delivered.
///
/// <para>
/// <b>The Core draws the frame, the module the content.</b> The same division of labour as the
/// module card of the right column (<see cref="ModuleCardViewModel"/>, S1.4): what every card
/// has, the Core knows; what stands in it, only the module knows. That is why
/// <see cref="Section"/> is an <c>object</c> — the <c>DataTemplate</c> maps it to the view by its
/// concrete type, as with the sections of the GAME card.
/// </para>
///
/// <para>
/// <b>No state except the view's:</b> title, key and tag are read from the module, not copied.
/// The tag text is the constant <c>Module</c> — a card the Core provides itself is not this class
/// (the Core card stands fixed in the view, with <c>Core</c> as its tag). The tag's <i>tone</i>
/// is state: <see cref="IsModuleEnabled"/>.
/// </para>
/// </summary>
public sealed partial class ModuleSettingsCardViewModel : ObservableObject
{
    /// <summary>
    /// The tag text of every module card (mockup, <c>tag:'Module'</c>). Constant: the value only
    /// tells the module cards from the Core card above them — it carries no state.
    /// </summary>
    public const string ModuleTag = "Module";

    private readonly IModule _module;

    /// <summary>
    /// Reads the module's switch from the registry (<c>ModuleRegistry.IsEnabled</c>), the same
    /// source the Modules page reads — no second copy of the switch lives here.
    /// </summary>
    private readonly Func<string, bool> _isModuleEnabled;

    private ModuleSettingsCardViewModel(
        IModule module, object? section, IReadOnlyList<NeedLineViewModel> needLines,
        Func<string, bool> isModuleEnabled)
    {
        _module = module;
        Section = section;
        NeedLines = needLines;
        _isModuleEnabled = isModuleEnabled;
    }

    public string Title => _module.DisplayName;

    public string Key => _module.Key;

    public string Tag => ModuleTag;

    /// <summary>
    /// Is the module switched on? Drives the tag's tone: forge when on, neutral grey when off —
    /// grey means off, everywhere (s15.7). Read from the registry on every access; the switch is
    /// flipped on the Modules page, so <see cref="OnShown"/> announces a possible change.
    /// </summary>
    public bool IsModuleEnabled => _isModuleEnabled(Key);

    /// <summary>
    /// The module's section — in operation a <see cref="WandBridgeSettingsViewModel"/>. Fetched
    /// once when the page is built: the Settings page is not rebuilt per game, and the section
    /// holds its stores as delegates — it also survives a change of the data root.
    ///
    /// <para>
    /// <c>null</c> when the module has no section but reports demand (Spec S3.5b §3.7) — the card
    /// then carries only its need lines.
    /// </para>
    /// </summary>
    public object? Section { get; }

    /// <summary>Does the module have a section of its own? The view hides it otherwise.</summary>
    public bool HasSection => Section is not null;

    /// <summary>
    /// The need lines, one per <see cref="IModule.RequiredComponents"/> entry, <b>above</b> the
    /// section (Spec S3.5b §3.7, E-5). Empty for a module without demand — no empty line.
    /// </summary>
    public IReadOnlyList<NeedLineViewModel> NeedLines { get; }

    /// <summary>
    /// Is the card open? <b>Module cards stand collapsed</b> (mockup, line 1090: only
    /// <c>core.app</c> is open). The reason is space: with five modules the page should still be
    /// easy to survey, and the card one looks for is expanded.
    ///
    /// <para><b>Volatile</b> like the Core card's state: not saved.</para>
    /// </summary>
    [ObservableProperty]
    private bool _isCardExpanded;

    /// <summary>
    /// Builds the card list from the registered modules — <b>in registration order</b>, as
    /// everywhere (Modules page, right column). It is called in the Composition Root; the
    /// <see cref="SettingsViewModel"/> is handed the result and does not know the registry.
    ///
    /// <para>
    /// <b>A module gets a card if it has a section or demand</b> (Spec S3.5b §3.7). <c>null</c>
    /// without demand is the normal case (<see cref="IModule.CreateSettingsSection"/>): such a
    /// module would otherwise have an empty frame one can expand to see nothing.
    /// </para>
    ///
    /// <para>
    /// <b>A static factory and no service</b>: it is a calculation without state. It stands here
    /// instead of in the Composition Root because a test could not reach it there — and the order
    /// of the cards is a promise a test should hold.
    /// </para>
    /// </summary>
    /// <param name="modules">The registered modules, in registration order.</param>
    /// <param name="needLinesFor">
    /// Builds a module's need lines. As a delegate because the lines need the inventory, the UI
    /// thread and the link to the <c>Dependencies</c> card — the Composition Root knows those.
    /// </param>
    /// <param name="isModuleEnabled">
    /// Tells whether a module (by key) is switched on — the registry's <c>IsEnabled</c>, so the
    /// card's tag tone follows the same switch as the Modules page.
    /// </param>
    public static IReadOnlyList<ModuleSettingsCardViewModel> CardsFor(
        IEnumerable<IModule> modules, Func<IModule, IReadOnlyList<NeedLineViewModel>> needLinesFor,
        Func<string, bool> isModuleEnabled)
        => modules
            .Select(module => new ModuleSettingsCardViewModel(
                module, module.CreateSettingsSection(), needLinesFor(module), isModuleEnabled))
            .Where(card => card.HasSection || card.NeedLines.Count > 0)
            .ToList();

    /// <summary>
    /// The Settings page is shown: the card passes that on to its section when it wants to read
    /// again (<see cref="IShowable"/>, Spec S3.5b §3.7), and announces the module's switch anew —
    /// it may have been flipped on the Modules page. That way the page has the sections and tags
    /// re-read without knowing a module; the need lines are re-read by the <c>Dependencies</c>
    /// card.
    /// </summary>
    public void OnShown()
    {
        OnPropertyChanged(nameof(IsModuleEnabled));
        (Section as IShowable)?.OnShown();
    }

    /// <summary>Opens and closes the card — the view's state, never a value (like the Core card).</summary>
    [RelayCommand]
    private void ToggleCard() => IsCardExpanded = !IsCardExpanded;
}
