namespace Anvil.Core.Steam;

/// <summary>
/// Alles, was Anvil über ein Spiel weiß (Spec §5) — das Manifest-Ergebnis aus S2.2 und die
/// vier Angaben aus S2.3.
///
/// <para>
/// <b>Warum das Spiel darin steckt und nicht daneben liegt:</b> S2.4 braucht das
/// Plattform-Symbol auf <i>jeder</i> Karte, nicht nur der ausgewählten — der Steckbrief
/// entsteht also ohnehin für alle. Ein zweites Nachschlagewerk neben <c>Games</c> würde an
/// jeder Anzeigestelle ein <c>TryGetValue</c> und eine Behandlung für „Schlüssel fehlt"
/// erzwingen, die es sachlich nicht gibt. Eine Liste, eine Wahrheit (Spec §7, E2).
/// </para>
///
/// <para>
/// <b><c>null</c> heißt „steht nicht da", nie „ist leer".</b> Das gilt besonders für
/// <paramref name="LaunchOptions"/>: <c>""</c> ist ein Eintrag, <c>null</c> ist keiner.
/// </para>
///
/// <para>
/// <b>Warum das Artwork mit hier steht:</b> die Karte braucht das Bild auf <i>jeder</i>
/// Kachel, nicht nur der ausgewählten — es entsteht also ohnehin für alle. Ein Wörterbuch
/// daneben erzwänge an jeder Anzeigestelle ein <c>TryGetValue</c> und eine Behandlung für
/// „Schlüssel fehlt", die es sachlich nicht gibt (Spec §6, E2).
/// </para>
/// </summary>
/// <param name="Game">Der Manifest-Eintrag, unverändert aus S2.2.</param>
/// <param name="Platform">Windows, nativ, oder unbekannt (Spec §4).</param>
/// <param name="PrefixPath">Der bewiesene Wine-Prefix, oder <c>null</c>.</param>
/// <param name="Proton">Version und Herkunft, oder <c>null</c> ohne <c>config_info</c>.</param>
/// <param name="CompatToolKey">
/// Die vom Nutzer erzwungene Proton-Wahl aus <c>CompatToolMapping</c> (Spec S4.3 §4.4), als
/// Schlüssel; <c>null</c> heißt Auto. Steht <b>neben</b> <paramref name="Proton"/>, nicht
/// darin: <paramref name="Proton"/> ist die Wahrheit aus <c>config_info</c> (was lief),
/// dies die Vorgabe (was gewählt ist) — beide dürfen fehlen und sich widersprechen.
/// </param>
/// <param name="LaunchOptions">Steams Startoptionen, oder <c>null</c> ohne Eintrag.</param>
/// <param name="ArtworkPath">
/// Das Querformat-Artwork aus Steams <c>librarycache</c>, oder <c>null</c>. Ein Pfad, kein
/// Bild: geladen wird in der Oberfläche (Spec §7) — der Core kennt kein Avalonia.
/// </param>
public sealed record GameProfile(
    SteamGame Game,
    GamePlatform Platform,
    string? PrefixPath,
    ProtonRuntime? Proton,
    string? CompatToolKey,
    string? LaunchOptions,
    string? ArtworkPath);
