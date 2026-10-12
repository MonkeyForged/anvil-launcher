namespace Anvil.Core.Modules;

/// <summary>
/// Der Ton eines Chips — <b>eine Aufzählung, nie eine Farbe</b> (Spec S5.3 §3.1).
///
/// <para>
/// Ein Modul, das <c>#FFBF8F</c> nennen dürfte, zeichnete in eine fremde Fläche. Dieselbe
/// Begründung wie beim Symbolschlüssel in <see cref="IModule.IconKey"/> (S2.2, E-A): Der
/// Vertrag nennt den Schlüssel, die Sicht löst ihn auf. Welcher Pinsel zu welchem Ton gehört,
/// steht in <c>Theme/Styles/Library.axaml</c> und nirgends sonst.
/// </para>
/// </summary>
public enum ModuleChipTone
{
    /// <summary>Getönt (Ember) — das Modul ist an diesem Spiel bereit.</summary>
    Accent,

    /// <summary>
    /// Neutral — eine Angabe ohne Zustand, etwa ein Zählwert („1 DLL",
    /// Elementbeschreibung §3).
    ///
    /// <para>
    /// <b>Heute ohne Verbraucher</b>, und das ist eine bewusste Ausnahme von „keine Abstraktion
    /// auf Verdacht" (Spec E-3): Es ist kein Bauteil, sondern ein Aufzählungswert, den das
    /// Design bereits benennt. Der Preis des Weglassens wäre eine Vertragsänderung beim ersten
    /// Modul, das einen Zählwert trägt.
    /// </para>
    /// </summary>
    Neutral,

    /// <summary>Grün getönt — es hängt etwas an und läuft.</summary>
    Attached,
}

/// <summary>
/// Was ein Modul auf der Spielkarte über sich sagt (Spec S5.3 §3.1): eine Beschriftung und ein
/// Ton. <b>Der Core zeichnet daraus</b>, das Modul zeichnet nie selbst (Vision §3).
/// </summary>
/// <param name="Label">
/// Englisch, wie die ganze Oberfläche. <b>Der Chip trägt den Zustand der Anwendung, nicht den
/// des Moduls</b> (Elementbeschreibung §3) — Wand Bridge schreibt deshalb „Wand", nicht seinen
/// <see cref="IModule.DisplayName"/>.
/// </param>
public readonly record struct ModuleChip(string Label, ModuleChipTone Tone);
