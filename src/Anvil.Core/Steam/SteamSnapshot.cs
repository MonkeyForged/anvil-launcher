namespace Anvil.Core.Steam;

/// <summary>
/// Was in einem Augenblick auf der Platte stand (Spec §7). Unveränderlich: es gibt keinen
/// Zwischenspeicher und keine Aktualisierung an Ort und Stelle — neu einlesen heißt neu laden,
/// und das alte Abbild bleibt gültig.
/// </summary>
/// <param name="RootPath">Die aufgelöste Steam-Wurzel, oder <c>null</c> auf einer Maschine ohne Steam.</param>
/// <param name="Libraries">Die Bibliothekspfade. Eine Bibliothek ist ein Pfad, kein eigener Typ.</param>
/// <param name="Games">Die Spiele mit ihrem Steckbrief, alphabetisch nach Namen.</param>
/// <param name="Tools">
/// Die aussortierten Einträge. Bleiben <see cref="SteamGame"/>: ein Compat-Tool hat keinen
/// Prefix, keine Plattform im Sinne von Spec §4 und keine Startoptionen (Spec §7, E6).
/// Der Katalog braucht von ihnen nur Name und Pfad.
/// </param>
/// <param name="CompatTools">
/// Der Katalog der Compat-Tools (Spec S4.3 §4.4, E-D): aus <paramref name="Tools"/> und den
/// <c>compatibilitytools.d</c>-Ordnern abgeleitet. Keine zweite Schleife — der Scanner liest
/// einmal, der Katalog entsteht daraus. S4.6 zeigt <see cref="CompatToolCatalog.Selectable"/>.
/// </param>
/// <param name="User">
/// Das aktive Konto, oder <c>null</c> (E-E). Der Scanner ermittelt es ohnehin für die
/// Startoptionen; der Schreiber braucht die Konto-ID für den Pfad zu <c>localconfig.vdf</c>.
/// </param>
/// <param name="Warnings">Was schiefging, in der Reihenfolge des Auftretens; englisch, für den Nutzer.</param>
public sealed record SteamSnapshot(
    string? RootPath,
    IReadOnlyList<string> Libraries,
    IReadOnlyList<GameProfile> Games,
    IReadOnlyList<SteamGame> Tools,
    CompatToolCatalog CompatTools,
    SteamUser? User,
    IReadOnlyList<string> Warnings)
{
    /// <summary>Das Ergebnis einer Maschine ohne Steam — kein Fehler, nur nichts da.</summary>
    public static SteamSnapshot Empty(string warning)
        => new(null, [], [], [], CompatToolCatalog.Empty, null, [warning]);
}
