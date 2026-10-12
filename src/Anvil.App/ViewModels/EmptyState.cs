using System.Windows.Input;

namespace Anvil.App.ViewModels;

/// <summary>
/// Der leere Zustand eines Bereichs nach der Vorlage des Designkonzepts (Elementbeschreibung
/// §10): Symbol des Bereichs, Titel (was fehlt), Erklärung (was den Bereich füllt) und
/// höchstens eine Aktion. Ein Wert, den die Seite liefert — <c>null</c> heißt „Inhalt da".
/// </summary>
public sealed record EmptyState(
    string IconKey,
    string Title,
    string Explanation,
    string? ActionLabel = null,
    ICommand? Action = null);
