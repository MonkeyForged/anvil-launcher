using System.ComponentModel;
using Anvil.Core.Prefixes;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace Anvil.App.ViewModels;

/// <summary>
/// Die Karte <c>Dependencies</c> der Settings-Seite (Spec S3.5b §3.6): jede Komponente, die die
/// installierten Module brauchen, genau einmal — und die Tür zu ihrem Ordner.
///
/// <para>
/// <b>Zugeklappt beim Bau</b> (Konzept: offen ist allein <c>core.app</c>). Aufklappen liest neu,
/// weil es keinen Wächter auf dem Ordner gibt (S3.3) und der Nutzer im Dateimanager ablegt,
/// während Anvil läuft.
/// </para>
///
/// <para>
/// Bauzustand: ohne Holen. Neu gelesen wird außer beim Aufklappen auch, wenn die Settings-Seite
/// gezeigt wird (<see cref="SettingsViewModel.OnShown"/>).
/// </para>
/// </summary>
public sealed partial class DependenciesViewModel : ObservableObject
{
    /// <summary>
    /// Die Texte der Karte, wörtlich aus Spec §3.6 — als Konstanten am ViewModel, nicht im
    /// <c>.axaml</c>, wo sie kein Test erreicht (Plan P-6 aus S3.4).
    /// </summary>
    public const string CardTitle = "Dependencies";

    /// <inheritdoc cref="CardTitle"/>
    public const string CardKey = "dependencies";

    /// <inheritdoc cref="CardTitle"/>
    public const string CardTag = "Core";

    /// <inheritdoc cref="CardTitle"/>
    public const string ComponentsLabel = "Components";

    /// <inheritdoc cref="CardTitle"/>
    public const string ComponentsSubBase = "Everything the installed modules need. Each component appears once.";

    /// <inheritdoc cref="CardTitle"/>
    public const string StorageLabel = "Storage location";

    /// <inheritdoc cref="CardTitle"/>
    public const string StorageSub = "Put the files into dependencies/.";

    /// <inheritdoc cref="CardTitle"/>
    public const string OpenFolderLabel = "Open folder";

    private readonly Func<string> _storageFolder;
    private readonly Action<string> _openFolder;
    private readonly ILogger? _logger;

    /// <summary>Steht die Karte offen? Vorgabe zu; das Aufklappen liest neu.</summary>
    [ObservableProperty]
    private bool _isCardExpanded;

    /// <param name="entries">Ein Eintrag je Bedarf, in der Reihenfolge von <c>ComponentDemands()</c>.</param>
    /// <param name="storageFolder">Der Ordner der geteilten Ablage, bei jedem Öffnen neu aufgelöst (E-10).</param>
    /// <param name="openFolder">Im Betrieb <c>DesktopShell.OpenFolder</c>.</param>
    /// <param name="logger">Null heißt stumm.</param>
    public DependenciesViewModel(
        IReadOnlyList<DependencyEntryViewModel> entries,
        Func<string> storageFolder,
        Action<string> openFolder,
        ILogger? logger = null)
    {
        Entries = entries;
        _storageFolder = storageFolder;
        _openFolder = openFolder;
        _logger = logger;

        foreach (var entry in entries)
        {
            entry.PropertyChanged += OnEntryChanged;
        }
    }

    /// <summary>Gemeldet von <see cref="Reveal"/>: Der Code-Behind rollt die Karte ins Bild.</summary>
    public event EventHandler? RevealRequested;

    /// <summary>Die Einträge, einer je Komponente.</summary>
    public IReadOnlyList<DependencyEntryViewModel> Entries { get; }

    /// <summary>
    /// Die Unterzeile von <c>Components</c>: die Basis, dahinter der Stand-Satz, wenn etwas fehlt
    /// (E-14 — gezeichnet ist nur das Beispiel, gebaut das Muster; Teil von Anforderung 44).
    /// </summary>
    public string ComponentsSub
    {
        get
        {
            var lacking = Entries
                .Select(entry => entry.Availability)
                .Where(availability => availability is
                {
                    State: ComponentAvailabilityState.Incomplete or ComponentAvailabilityState.Missing,
                })
                .ToList();

            return lacking.Count switch
            {
                0 => ComponentsSubBase,
                1 when lacking[0]!.Missing.Count == 1 => ComponentsSubBase + " One component is missing a file.",
                1 => ComponentsSubBase + " One component is missing files.",
                var count => ComponentsSubBase + $" {count} components are missing files.",
            };
        }
    }

    /// <summary>
    /// Klappt die Karte auf und bittet die Sicht, sie ins Bild zu rollen — der Verweis
    /// <c>Show in Dependencies</c> der Bedarfszeile (Spec §3.7).
    /// </summary>
    public void Reveal()
    {
        IsCardExpanded = true;
        RevealRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Liest alle Einträge neu.</summary>
    public Task RefreshAsync() => Task.WhenAll(Entries.Select(entry => entry.RefreshAsync()));

    [RelayCommand]
    private void ToggleCard() => IsCardExpanded = !IsCardExpanded;

    /// <summary>
    /// Öffnet <c>&lt;Datenwurzel&gt;/dependencies/</c> (E-10). Der Fang ist derselbe wie in
    /// <see cref="WandBridgeSettingsViewModel"/>: Ein Desktop ohne <c>xdg-open</c> oder ein nicht
    /// anlegbarer Ordner darf die Einstellungen nicht abstürzen lassen.
    /// </summary>
    [RelayCommand]
    private void OpenFolder()
    {
        try
        {
            var path = _storageFolder();
            _openFolder(path);
            _logger?.LogDebug("dependencies folder opened: {Path}", path);
        }
        catch (Exception exception)
            when (exception is Win32Exception or InvalidOperationException or IOException
                or UnauthorizedAccessException)
        {
            _logger?.LogWarning(exception, "could not open the dependencies folder");
        }
    }

    partial void OnIsCardExpandedChanged(bool value)
    {
        if (value)
        {
            _ = RefreshSafelyAsync();
        }
    }

    /// <summary>
    /// Startet das Neulesen als eigene Aufgabe und beobachtet ihre Ausnahme, statt <c>async
    /// void</c> — dieselbe Bauform wie <c>ModulesViewModel.CheckSafelyAsync</c>.
    /// </summary>
    private async Task RefreshSafelyAsync()
    {
        try
        {
            await RefreshAsync();
        }
        catch (Exception exception)
        {
            _logger?.LogError(exception, "dependencies refresh failed");
        }
    }

    private void OnEntryChanged(object? sender, PropertyChangedEventArgs args)
        => OnPropertyChanged(nameof(ComponentsSub));
}
