namespace Anvil.Core.Steam;

/// <summary>
/// Ein Eintrag aus genau einem <c>appmanifest_*.acf</c> — Spiel oder Werkzeug, derselbe Record
/// (Spec §6). Bewusst nur fünf Felder: was keine Story braucht, wird nicht gelesen.
/// </summary>
/// <param name="AppId">Steams AppId; <c>uint</c>, damit das Format beim Lesen mitgeprüft wird.</param>
/// <param name="Name">Der Anzeigename aus dem Manifest.</param>
/// <param name="InstallDirectory">Absolut zusammengesetzt: &lt;library&gt;/steamapps/common/&lt;installdir&gt;.</param>
/// <param name="LibraryPath">Die Bibliothek, in der das Manifest lag — S2.3 sucht compatdata daneben.</param>
/// <param name="LastPlayed">Steams <c>0</c> wird zu <c>null</c>: nie gestartet ist kein Zeitpunkt.</param>
public sealed record SteamGame(
    uint AppId,
    string Name,
    string InstallDirectory,
    string LibraryPath,
    DateTimeOffset? LastPlayed);
