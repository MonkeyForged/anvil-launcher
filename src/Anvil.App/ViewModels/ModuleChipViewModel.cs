using Anvil.Core.Modules;

namespace Anvil.App.ViewModels;

/// <summary>
/// Ein Modul-Chip in der Fußzeile der Spielkarte (Elementbeschreibung §3, Spec S5.3 §3.6).
///
/// <para>
/// <b>Drei Boolesche statt eines Aufzählungswertes:</b> Die XAML schaltet den Ton über
/// <c>Classes.is-attached="{Binding IsAttached}"</c>, und das braucht ein <c>bool</c>. Ein
/// Konverter für drei Klassen wäre mehr Bauteil als Nutzen — dieselbe Entscheidung wie bei
/// <c>GameCardViewModel.IsWindows</c> und seinen zwei Geschwistern (S1.4, Spec §9).
/// </para>
///
/// <para>
/// <b>Unveränderlich.</b> Ändert sich der Zustand, baut das <see cref="LibraryViewModel"/> die
/// Chips der Karte neu — leeren und neu füllen, wie bei den Modulkarten der rechten Spalte.
/// </para>
/// </summary>
public sealed class ModuleChipViewModel(ModuleChip chip)
{
    /// <summary>Englisch, wie die ganze Oberfläche — das Modul hat sie formuliert.</summary>
    public string Label => chip.Label;

    public bool IsAccent => chip.Tone == ModuleChipTone.Accent;

    public bool IsNeutral => chip.Tone == ModuleChipTone.Neutral;

    public bool IsAttached => chip.Tone == ModuleChipTone.Attached;

    /// <summary>
    /// Trägt dieser Chip den grünen Punkt? <b>Eine eigene Eigenschaft</b>, obwohl sie heute
    /// <see cref="IsAttached"/> gleicht: Das ist die Frage der Sicht, nicht die des Tons.
    ///
    /// <para>
    /// Der Punkt <b>ruht</b> (Elementbeschreibung §3) — anders als der der Running-Marke oben
    /// rechts im Artwork, der pulsiert. Zwei pulsierende Punkte auf einer Karte wären Unruhe
    /// ohne Aussage. Durchgesetzt wird das in <c>Theme/Styles/Library.axaml</c>, indem es für
    /// <c>Ellipse.chip-dot</c> schlicht keine Animation gibt.
    /// </para>
    /// </summary>
    public bool ShowsDot => IsAttached;
}
