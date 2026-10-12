using Anvil.Core.Settings;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace Anvil.App.ViewModels;

/// <summary>
/// App settings: the Core card (Spec S1.6 §4). Every change goes at once into the one
/// <see cref="AppSettings"/> instance and via <see cref="SettingsStore.Save"/> to disk — the same
/// pattern as <c>ModuleRegistry.SetEnabled</c>. The app passes the folder dialog as a delegate,
/// so that no Avalonia stands here.
/// The module cards below (S3.4) come in fully built: the Core draws their frame, the content is
/// supplied by the respective module — this class knows none of them.
/// The autostart entry likewise (S3.6, Spec E-G): the ViewModel reports the new value, the app
/// mirrors it into the file.
/// </summary>
public sealed partial class SettingsViewModel : PageViewModel
{
    private readonly AppSettings _settings;
    private readonly SettingsStore _store;
    private readonly string _defaultDataDirectory;
    private readonly Func<Task<string?>> _pickFolder;
    private readonly Action<bool> _applyStartOnBoot;
    private readonly ILogger? _logger;

    /// <param name="dependencies">
    /// The <c>Dependencies</c> card between the Core card and the module cards (Spec S3.5b §3.6)
    /// — passed in like the module cards; the need lines refer to the same instance.
    /// </param>
    /// <param name="moduleCards">
    /// The modules' cards, fully built (S3.4, Spec E-8). <b>Passed in, not built from the
    /// registry here:</b> this page knows no module to this day, and a dependency on the registry
    /// only to build a list the Composition Root can build anyway would be state without reason.
    /// The order is that of registration — it is built in
    /// <see cref="ModuleSettingsCardViewModel.CardsFor"/>.
    /// </param>
    /// <param name="confirmation">
    /// The page's confirmation (s9.3, design 61), drawn over the settings content. One instance
    /// for the page; a module asks through a delegate to it and never draws the dialog itself.
    /// </param>
    public SettingsViewModel(
        AppSettings settings, SettingsStore store, string defaultDataDirectory,
        Func<Task<string?>> pickFolder, Action<bool> applyStartOnBoot,
        BackupsViewModel backups,
        DependenciesViewModel dependencies,
        IReadOnlyList<ModuleSettingsCardViewModel> moduleCards,
        SettingsConfirmationViewModel confirmation,
        ILogger? logger = null)
    {
        _settings = settings;
        _store = store;
        _defaultDataDirectory = defaultDataDirectory;
        _pickFolder = pickFolder;
        _applyStartOnBoot = applyStartOnBoot;
        Backups = backups;
        Dependencies = dependencies;
        ModuleCards = moduleCards;
        Confirmation = confirmation;
        _logger = logger;
        _startOnBoot = settings.App.StartOnBoot;
        _minimizeToTray = settings.App.MinimizeToTray;
    }

    public override string Title => "Settings";

    public override string Subtitle => "Base app on top, then one card per module.";

    /// <summary>Never empty: the Core card always stands.</summary>
    public override EmptyState? EmptyState => null;

    public string CardTitle => "Anvil Launcher";

    /// <summary>The section in settings.json — one truth per thing (Spec E10).</summary>
    public string CardKey => "app";

    public string CardTag => "Core";

    /// <summary>Transient: the card state is not saved.</summary>
    [ObservableProperty]
    private bool _isCardExpanded = true;

    [ObservableProperty]
    private bool _startOnBoot;

    [ObservableProperty]
    private bool _minimizeToTray;

    public string DataLocation => _settings.App.DataLocation ?? _defaultDataDirectory;

    /// <summary>The backup list behind the button "Manage" (S4.7).</summary>
    public BackupsViewModel Backups { get; }

    /// <summary>The <c>Dependencies</c> card, collapsed when built (Spec S3.5b §3.6).</summary>
    public DependenciesViewModel Dependencies { get; }

    /// <summary>
    /// The module cards below the Core card, in registration order (S3.4). Empty as long as no
    /// module supplies a section or a demand — then nothing stands there, and that is not an
    /// empty state: the page is never empty, the Core card always stands.
    /// </summary>
    public IReadOnlyList<ModuleSettingsCardViewModel> ModuleCards { get; }

    /// <summary>The confirmation over the settings content (s9.3, design 61).</summary>
    public SettingsConfirmationViewModel Confirmation { get; }

    public string BackupsLabel => "Backups";

    public string BackupsSub => "Copies of Steam files, taken before every save.";

    /// <summary>
    /// Re-reads the <c>Dependencies</c> card when the page is shown (Spec S3.5b §3.6) — there is
    /// no watcher on the folder (S3.3). Fire and forget is safe here:
    /// <c>ComponentAvailability.For</c> does not throw, and <c>PackageStore.List</c> returns an
    /// empty list on errors.
    ///
    /// <para>
    /// The module sections re-read too (Spec S3.5b §3.7) — via the cards, which pass it on to an
    /// <see cref="IShowable"/> section. This page knows no module in doing so.
    /// </para>
    /// </summary>
    public override void OnShown()
    {
        _ = Dependencies.RefreshAsync();
        foreach (var card in ModuleCards)
        {
            card.OnShown();
        }
    }

    partial void OnStartOnBootChanged(bool value)
    {
        _settings.App.StartOnBoot = value;
        Persist("startOnBoot", value);
        // After saving: the file is the mirror of the saved truth.
        _applyStartOnBoot(value);
    }

    partial void OnMinimizeToTrayChanged(bool value)
    {
        _settings.App.MinimizeToTray = value;
        Persist("minimizeToTray", value);
    }

    [RelayCommand]
    private void ToggleCard() => IsCardExpanded = !IsCardExpanded;

    [RelayCommand]
    private async Task ChangeDataLocationAsync()
    {
        var folder = await _pickFolder();
        if (string.IsNullOrEmpty(folder))
        {
            return;
        }

        _settings.App.DataLocation = folder;
        OnPropertyChanged(nameof(DataLocation));
        Persist("dataLocation", folder);
    }

    private void Persist(string name, object value)
    {
        _store.Save(_settings);
        _logger?.LogInformation("setting changed: {Name} = {Value}", name, value);
    }
}
