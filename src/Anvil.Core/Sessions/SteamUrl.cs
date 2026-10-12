using System.Globalization;

namespace Anvil.Core.Sessions;

/// <summary>
/// Steams eigene Adressen an einer Stelle (Spec S3.2 §4.1). Sie gehen ueber dieselbe Naht wie
/// jeder andere Pfad — <see cref="DesktopShell.StartInfoFor"/> baut daraus
/// <c>xdg-open &lt;url&gt;</c>.
/// </summary>
public static class SteamUrl
{
    /// <summary>Startet ein Spiel ueber das laufende Steam; Steam startet bei Bedarf selbst.</summary>
    public static string RunGame(uint appId)
        => "steam://rungameid/" + appId.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// Die Spieleseite in Steams Bibliothek (S3.4, „Show in Steam"). Kandidat eins aus
    /// Projektplan Entscheidung 7, ungemessen — traegt die Adresse live nicht, wird hier
    /// <c>steam://url/GameLibrary/&lt;appid&gt;</c> eingesetzt, sonst nirgends (Spec S3.4 E-G).
    /// </summary>
    public static string GameDetails(uint appId)
        => "steam://nav/games/details/" + appId.ToString(CultureInfo.InvariantCulture);
}
