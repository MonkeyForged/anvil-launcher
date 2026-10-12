namespace Anvil.Core.Steam;

/// <summary>
/// Was der Prozessbaum in einem Augenblick ueber Steam sagt (Spec S4.2 §4.1). Nur zwei
/// Felder (Spec E-A): Welche Spiele laufen, weiss der Waechter; eine PID braucht niemand.
/// </summary>
/// <param name="Running">Startskript oder Hauptclient vorhanden.</param>
/// <param name="UiReady">Webhelper oder Steams eigener Launcher-Dienst vorhanden.</param>
public sealed record SteamProcessSnapshot(bool Running, bool UiReady)
{
    /// <summary>
    /// Lebt <b>irgendein</b> Prozess von Steam? Das ist die Frage beim Beenden — und sie ist
    /// strenger als <see cref="Running"/>: Steams Oberflaeche stirbt nach dem Hauptclient,
    /// und ein zurueckgebliebener <c>steamwebhelper</c> liesse den naechsten Start sofort als
    /// „bereit" gelten (Archiv, gemessen 2026-08-27: „bereit" nach 1,5 s).
    /// </summary>
    public bool Alive => Running || UiReady;

    /// <summary>Das Bild, wenn nichts von Steam laeuft.</summary>
    public static SteamProcessSnapshot None { get; } = new(false, false);
}
