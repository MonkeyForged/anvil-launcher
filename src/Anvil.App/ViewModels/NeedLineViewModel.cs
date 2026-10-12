using Anvil.Core.Prefixes;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Anvil.App.ViewModels;

/// <summary>
/// Eine Bedarfszeile der Modulkarte (Spec S3.5b §3.7): <c>Needs .NET Framework 4.8</c>, dahinter
/// der Nachsatz, wenn etwas fehlt, und der Verweis <c>Show in Dependencies</c>.
///
/// <para>
/// <b>Der Core zeichnet sie, nicht das Modul</b> (E-5): Das Modul meldet nur
/// <c>RequiredComponents</c>; eine zweite Bridge liefert Daten, keine neue Zeile.
/// </para>
///
/// <para>
/// <b>Alles wird bei jedem Zugriff aus dem <see cref="ComponentInventory"/> gelesen</b>, nichts
/// gemerkt — dieselbe Haltung wie <see cref="DependencyEntryViewModel"/>: Zeile und Eintrag lesen
/// dieselbe Wahrheit (E-6) und können einander nicht widersprechen.
/// </para>
/// </summary>
public sealed partial class NeedLineViewModel : ObservableObject
{
    /// <summary>Der Verweis auf die Karte <c>Dependencies</c> (Spec §3.7).</summary>
    public const string ShowLabel = "Show in Dependencies";

    private readonly PrefixComponent _required;
    private readonly ComponentInventory _inventory;
    private readonly Action<Action> _post;
    private readonly Action _showDependencies;

    /// <param name="required">Die Forderung des Moduls, aus <c>IModule.RequiredComponents</c>.</param>
    /// <param name="inventory">Die eine Wahrheit über Fassung und Verfügbarkeit (E-6).</param>
    /// <param name="post">Marschiert auf den UI-Faden; der Bestand meldet vom Thread-Pool.</param>
    /// <param name="showDependencies">Im Betrieb <c>DependenciesViewModel.Reveal</c>.</param>
    public NeedLineViewModel(
        PrefixComponent required, ComponentInventory inventory, Action<Action> post, Action showDependencies)
    {
        _required = required;
        _inventory = inventory;
        _post = post;
        _showDependencies = showDependencies;

        inventory.AvailabilityChanged += OnAvailabilityChanged;
    }

    /// <summary>Anzeigename und Version der <b>geltenden</b> Fassung, dahinter der Nachsatz.</summary>
    public string Text
    {
        get
        {
            var component = _inventory.Resolve(_required);
            return $"Needs {component.DisplayName} {component.Version}{Tail}";
        }
    }

    /// <summary>Warn-Ton genau dann, wenn ein Nachsatz steht; sonst Kreis mit Ember-Haken.</summary>
    public bool IsWarning => Tail.Length > 0;

    /// <summary>
    /// Der Nachsatz aus dem Befund: <c>Incomplete</c> und <c>Missing</c> sagen, was fehlt; ohne
    /// Befund (wird geprüft), bereit oder ohne Rezept steht keiner.
    /// </summary>
    private string Tail => _inventory.AvailabilityOf(_required.Key) switch
    {
        { State: ComponentAvailabilityState.Missing } => " — nothing here yet",
        { State: ComponentAvailabilityState.Incomplete, Missing.Count: 1 } => " — 1 file missing",
        { State: ComponentAvailabilityState.Incomplete } availability => $" — {availability.Missing.Count} files missing",
        _ => string.Empty,
    };

    [RelayCommand]
    private void ShowInDependencies() => _showDependencies();

    private void OnAvailabilityChanged(object? sender, string key)
    {
        if (key != _required.Key)
        {
            return;
        }

        _post(() =>
        {
            OnPropertyChanged(nameof(Text));
            OnPropertyChanged(nameof(IsWarning));
        });
    }
}
