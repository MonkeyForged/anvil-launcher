using Anvil.Core.Modules;
using Anvil.Core.Packages;
using Anvil.Core.Prefixes;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Anvil.App.ViewModels;

/// <summary>
/// Der Ton einer Zeile im Eintrag (Spec S3.5b §3.6) — die Sicht macht daraus einen Farbtoken.
/// Ein eigener Aufzählungstyp statt vier <c>bool</c>s: Ein Text hat genau einen Ton.
/// </summary>
public enum EntryTone
{
    /// <summary>Gedämpft — nichts zu tun.</summary>
    Muted,

    /// <summary>Sekundärtext — eine Auskunft, die der Nutzer lesen soll.</summary>
    Secondary,

    /// <summary>Warnung — es fehlt etwas.</summary>
    Warning,

    /// <summary>Ember — es läuft etwas.</summary>
    Active,
}

/// <summary>Ein Bestandteil im Aufklapper des Eintrags: Anzeigename und ob er in der Ablage liegt.</summary>
/// <param name="Name">Der Anzeigename der Zutat, großgeschrieben.</param>
/// <param name="IsPresent">Kreis mit Haken, sonst gestrichelt.</param>
public sealed record DependencyPartViewModel(string Name, bool IsPresent);

/// <summary>
/// Ein Eintrag der Fläche <c>Dependencies</c> (Spec S3.5b §3.6) — die Box aus Bauplan §8.1: eine
/// Komponente mit ihrer geltenden Fassung, ihrem Zustand, der Unterzeile, dem geteilt-Satz und den
/// Bestandteilen.
///
/// <para>
/// <b>Alles Abgeleitete wird bei jedem Zugriff aus dem <see cref="ComponentInventory"/>
/// gelesen</b>, nichts gemerkt: Der Bestand ist die eine Wahrheit (E-6), und ein Eintrag, der
/// Zustand spiegelte, könnte ihm widersprechen. Meldet der Bestand eine Änderung für den eigenen
/// Schlüssel, meldet der Eintrag <b>alle</b> Eigenschaften neu.
/// </para>
///
/// <para>
/// <b>Die Wahl einer Fassung ist ein Wunsch des Nutzers, das Setzen beim Bau nicht</b> — dieselbe
/// Unterscheidung wie in <see cref="ProtonSectionViewModel"/>: Nur eine echte Änderung ruft
/// <see cref="ComponentInventory.Choose"/> und liest neu.
/// </para>
///
/// <para>
/// <b>Das Holen hält der Eintrag nicht selbst</b>, sondern ein <see cref="FetchProgressViewModel"/>
/// (Spec §3.5). Solange es läuft, überdeckt es den Befund: Zustand <c>fetching</c>, keine Aktion.
/// Danach wird immer neu gelesen — auch nach einem Abbruch, dessen Rückbau den Stand davor
/// herstellt.
/// </para>
/// </summary>
public sealed partial class DependencyEntryViewModel : ObservableObject
{
    /// <summary>Unterzeile, solange geprüft wird (Spec §3.6).</summary>
    public const string CheckingNote = "Matching files against size and checksum.";

    /// <summary>Unterzeile einer Fassung ohne Rezept — der Satz aus dem Bedarfseintrag (Spec §3.6).</summary>
    public const string NoRecipeNote =
        "Anvil can recognise this version but has no recipe to install it. Nothing is missing here — there is nothing to fetch. Pick a version with a recipe, or install it yourself.";

    /// <summary>Die Aktion der Box (Spec §3.6), <see cref="GetMissingFilesCommand"/>.</summary>
    public const string GetMissingFilesLabel = "Get missing files";

    private readonly ComponentDemand _demand;
    private readonly IReadOnlyList<string> _moduleDisplayNames;
    private readonly ComponentInventory _inventory;
    private readonly Func<string, CatalogEntry?> _sourceLookup;
    private readonly Action<Action> _post;
    private readonly Action<string> _requestModuleCheck;

    /// <summary>
    /// Hält den Melde-Pfad von <see cref="SelectedVersion"/> an, während der Eintrag die Wahl
    /// selbst nachzieht — nach einer Meldung des Bestands ist das kein Wunsch des Nutzers.
    /// </summary>
    private bool _syncing;

    /// <summary>
    /// Die gewählte Fassung. Setzt der Nutzer sie, speichert der Bestand die Wahl, und der Eintrag
    /// liest neu (Spec §3.6, „Wann neu gelesen wird").
    /// </summary>
    [ObservableProperty]
    private string _selectedVersion;

    /// <summary>Steht der Aufklapper der Bestandteile offen? Beim Bau immer zu (Spec §3.6).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PartsToggleText))]
    private bool _isPartsOpen;

    /// <param name="demand">Der Bedarf, aus <c>ModuleRegistry.ComponentDemands()</c>.</param>
    /// <param name="moduleDisplayNames">Die Anzeigenamen der fordernden Module, in der Reihenfolge von <see cref="ComponentDemand.ModuleKeys"/>.</param>
    /// <param name="inventory">Die eine Wahrheit über Fassung und Verfügbarkeit (E-6).</param>
    /// <param name="sourceLookup">Das zweite Manifest (<c>PackageCatalog.SourceFor</c>) — trennt „holbar" von „selbst ablegen".</param>
    /// <param name="fetch">Das Holen dieses Eintrags — je Eintrag eines, über den einen Bezug.</param>
    /// <param name="post">Marschiert auf den UI-Faden; der Bestand meldet vom Thread-Pool.</param>
    /// <param name="requestModuleCheck">Stößt die Prüfung eines Moduls an — damit wird es ohne Neustart bereit.</param>
    public DependencyEntryViewModel(
        ComponentDemand demand,
        IReadOnlyList<string> moduleDisplayNames,
        ComponentInventory inventory,
        Func<string, CatalogEntry?> sourceLookup,
        FetchProgressViewModel fetch,
        Action<Action> post,
        Action<string> requestModuleCheck)
    {
        _demand = demand;
        _moduleDisplayNames = moduleDisplayNames;
        _inventory = inventory;
        _sourceLookup = sourceLookup;
        Fetch = fetch;
        _post = post;
        _requestModuleCheck = requestModuleCheck;

        Versions = inventory.Candidates(demand.Required).Select(candidate => candidate.Version).ToList();

        // Direkt ins Feld: Der Bau ist kein Wunsch des Nutzers und ruft deshalb nicht Choose.
        _selectedVersion = inventory.Resolve(demand.Required).Version;

        inventory.AvailabilityChanged += OnAvailabilityChanged;
        fetch.PropertyChanged += (_, args) =>
        {
            // Beginn und Ende des Holens ändern Zustand, Ton, Punkt und Aktion.
            if (args.PropertyName == nameof(FetchProgressViewModel.IsRunning))
            {
                OnPropertyChanged(string.Empty);
            }
        };
    }

    /// <summary>Das Holen dieses Eintrags: Balken, Prozent, <c>Cancel</c>, roter Satz.</summary>
    public FetchProgressViewModel Fetch { get; }

    /// <summary>Der versionsfreie Schlüssel der Komponente.</summary>
    public string Key => _demand.Required.Key;

    /// <summary>Der Anzeigename der geltenden Fassung.</summary>
    public string Name => _inventory.Resolve(_demand.Required).DisplayName;

    /// <summary>Die wählbaren Fassungen, in Katalogreihenfolge.</summary>
    public IReadOnlyList<string> Versions { get; }

    /// <summary>Ab zwei Kandidaten ein Auswahlfeld, sonst Mono-Text.</summary>
    public bool HasVersionChoice => Versions.Count > 1;

    /// <summary>Der zuletzt abgelegte Befund, oder <c>null</c> — „wird geprüft".</summary>
    public ComponentAvailability? Availability => _inventory.AvailabilityOf(Key);

    /// <summary>Der Zustand als Wort (Spec §3.6, Tabelle „Zustand").</summary>
    public string StateText => Fetch.IsRunning ? "fetching" : Availability switch
    {
        null => "checking",
        { State: ComponentAvailabilityState.Ready } => "complete",
        { State: ComponentAvailabilityState.Missing } => "nothing here yet",
        { State: ComponentAvailabilityState.NotBuildable } => "no recipe",
        { Missing.Count: 1 } => "1 file missing",
        var availability => $"{availability.Missing.Count} files missing",
    };

    /// <summary>Der Ton des Zustands: gedämpft, Warnung oder Ember.</summary>
    public EntryTone StateTone => Fetch.IsRunning ? EntryTone.Active : Availability switch
    {
        null => EntryTone.Active,
        { State: ComponentAvailabilityState.Incomplete or ComponentAvailabilityState.Missing } => EntryTone.Warning,
        _ => EntryTone.Muted,
    };

    /// <summary>Der Punkt neben dem Zustand, solange etwas läuft.</summary>
    public bool ShowsActivityDot => Fetch.IsRunning || Availability is null;

    /// <summary>Die Unterzeile; leer heißt keine.</summary>
    public string Note
    {
        get
        {
            var availability = Availability;
            if (availability is null)
            {
                return CheckingNote;
            }

            if (availability.State == ComponentAvailabilityState.NotBuildable)
            {
                return NoRecipeNote;
            }

            var sentences = new List<string>(2);
            var fetchable = MissingWithSource(availability);
            if (fetchable.Count == 1)
            {
                sentences.Add($"{Capitalize(fetchable[0].Ingredient.DisplayName)} is missing.");
            }
            else if (fetchable.Count > 1)
            {
                sentences.Add($"{fetchable.Count} of {availability.Ingredients.Count} files are missing.");
            }

            var unsourced = MissingWithoutSource(availability);
            if (unsourced.Count > 0)
            {
                // E-13: der gezeichnete Satz, auch wenn er Wiedererkennung verspricht, die es ohne
                // Quelleneintrag nicht gibt — gemeldet als Anforderung 44.
                var names = string.Join(" and ", unsourced.Select(state => state.Ingredient.DisplayName));
                sentences.Add(
                    $"No vendor address for {names} — put it into dependencies/ yourself. The file is recognised by size and checksum, the name does not matter.");
            }

            return string.Join(" ", sentences);
        }
    }

    /// <summary>
    /// Der Ton der Unterzeile: Sekundärtext, sobald sie etwas sagt, das der Nutzer selbst tun muss
    /// (kein Rezept, keine Quelle); sonst gedämpft.
    /// </summary>
    public EntryTone NoteTone
    {
        get
        {
            var availability = Availability;
            if (availability is null)
            {
                return EntryTone.Muted;
            }

            return availability.State == ComponentAvailabilityState.NotBuildable
                || MissingWithoutSource(availability).Count > 0
                ? EntryTone.Secondary
                : EntryTone.Muted;
        }
    }

    /// <summary>
    /// <see cref="StateTone"/> als Wahrheitswert für die Sicht: Das <c>.axaml</c> setzt Stilklassen
    /// über <c>Classes.warning</c> und kennt keinen Vergleich mit einem Aufzählungswert — so
    /// braucht es keinen Konverter.
    /// </summary>
    public bool IsStateWarning => StateTone == EntryTone.Warning;

    /// <inheritdoc cref="IsStateWarning"/>
    public bool IsStateActive => StateTone == EntryTone.Active;

    /// <summary><see cref="NoteTone"/> als Wahrheitswert für die Sicht (wie <see cref="IsStateWarning"/>).</summary>
    public bool IsNoteSecondary => NoteTone == EntryTone.Secondary;

    /// <summary>
    /// Mindestens ein fehlender Bestandteil hat eine Quelle und nichts läuft — dann gibt es
    /// <c>Get missing files</c>; während des Holens steht an seiner Stelle der Balken.
    /// </summary>
    public bool CanGetMissingFiles
        => !Fetch.IsRunning && Availability is { } availability && MissingWithSource(availability).Count > 0;

    /// <summary>Der geteilt-Satz; leer ohne fordernde Module (Spec §3.6).</summary>
    public string SharedText
        => _moduleDisplayNames.Count == 0
            ? string.Empty
            : $"Needed by {JoinNames(_moduleDisplayNames)}. Switching a module off removes nothing — Anvil never deletes from the storage location.";

    /// <summary>
    /// Die Bestandteile der geltenden Fassung, in Rezeptreihenfolge. <b>Aus dem Rezept, nicht aus
    /// dem Befund</b>: Während geprüft wird, ist der Befund <c>null</c>, und der Aufklapper
    /// verschwände für diesen Augenblick. Ob ein Bestandteil da ist, sagt der Befund; ohne Befund
    /// gilt er als nicht da.
    /// </summary>
    public IReadOnlyList<DependencyPartViewModel> Parts
    {
        get
        {
            var ingredients = _inventory.Resolve(_demand.Required).Recipe?.Ingredients ?? [];
            var states = Availability?.Ingredients ?? [];
            return ingredients
                .Select(ingredient => new DependencyPartViewModel(
                    Capitalize(ingredient.DisplayName),
                    states.Any(state => state.Ingredient == ingredient && state.Present)))
                .ToList();
        }
    }

    /// <summary>Kein Aufklapper bei <c>no recipe</c>.</summary>
    public bool HasParts => Parts.Count > 0;

    /// <summary>Beschriftung des Aufklappers.</summary>
    public string PartsToggleText
    {
        get
        {
            var count = Parts.Count;
            var files = count == 1 ? "1 file" : $"{count} files";
            return IsPartsOpen ? $"Hide {files}" : $"Show {files}";
        }
    }

    /// <summary>
    /// Liest neu: <see cref="ComponentInventory.CheckAsync"/>, danach die Prüfung jedes fordernden
    /// Moduls — damit wird Wand Bridge <b>ohne Neustart</b> bereit, wenn die letzte Datei da ist
    /// (Spec §3.6, E-11).
    /// </summary>
    public async Task RefreshAsync()
    {
        await _inventory.CheckAsync(_demand.Required);

        foreach (var moduleKey in _demand.ModuleKeys)
        {
            _requestModuleCheck(moduleKey);
        }
    }

    /// <summary>
    /// Holt die fehlenden Bestandteile <b>mit</b> Quelle, in Rezeptreihenfolge — die ohne Quelle
    /// bleiben dem Nutzer (Spec §3.6). Danach <b>immer</b> <see cref="RefreshAsync"/>: nach Erfolg
    /// wird das Modul ohne Neustart bereit, nach Abbruch oder Fehler zeigt der Eintrag, was
    /// wirklich liegt.
    /// </summary>
    [RelayCommand]
    private async Task GetMissingFilesAsync()
    {
        if (Fetch.IsRunning || Availability is not { } availability)
        {
            return;
        }

        var entries = MissingWithSource(availability)
            .Select(state => _sourceLookup(state.Ingredient.FileName)!)
            .ToList();
        if (entries.Count == 0)
        {
            return;
        }

        await Fetch.RunAsync(_inventory.Store, entries);
        await RefreshAsync();
    }

    partial void OnSelectedVersionChanged(string value)
    {
        if (_syncing || value is null)
        {
            // Die ComboBox meldet null, wenn ihre Auswahl kurz entfällt — das ist keine Wahl.
            return;
        }

        _inventory.Choose(Key, value);
        _ = RefreshAsync();
    }

    private void OnAvailabilityChanged(object? sender, string key)
    {
        if (key != Key)
        {
            return;
        }

        _post(Reload);
    }

    /// <summary>Zieht die Wahl nach und meldet alle abgeleiteten Eigenschaften neu.</summary>
    private void Reload()
    {
        _syncing = true;
        try
        {
            SelectedVersion = _inventory.Resolve(_demand.Required).Version;
        }
        finally
        {
            _syncing = false;
        }

        OnPropertyChanged(string.Empty);
    }

    private List<IngredientState> MissingWithSource(ComponentAvailability availability)
        => availability.Missing.Where(state => _sourceLookup(state.Ingredient.FileName) is not null).ToList();

    private List<IngredientState> MissingWithoutSource(ComponentAvailability availability)
        => availability.Missing.Where(state => _sourceLookup(state.Ingredient.FileName) is null).ToList();

    /// <summary><c>A</c> · <c>A and B</c> · <c>A, B and C</c>.</summary>
    private static string JoinNames(IReadOnlyList<string> names)
        => names.Count == 1
            ? names[0]
            : string.Join(", ", names.Take(names.Count - 1)) + " and " + names[^1];

    // Dieselbe Regel wie in WandBridgeModule: Der Anzeigename einer Zutat ist ein Satzteil
    // („the .NET 4.0 installer") und steht hier am Satzanfang.
    private static string Capitalize(string text)
        => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];
}
