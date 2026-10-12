namespace Anvil.Core.Steam;

/// <summary>
/// Streicht aus einer Wunschliste alles, was schon so auf der Platte steht (Spec S4.4 §3.2).
///
/// <para>
/// <b>Eine Regel, zwei Orte</b> (Projektplan M4, Entscheidung 9): S4.5 sortiert damit beim
/// Sammeln aus, damit die Speicherleiste gar nicht erst erscheint; die Transaktion sortiert
/// damit ein zweites Mal aus — nach dem Beenden von Steam, gegen ein frisches Abbild
/// (Korrektur c). Zwei Zeitpunkte, dieselbe Funktion.
/// </para>
/// </summary>
public static class SteamChangeFilter
{
    public static IReadOnlyList<SteamGameChange> Reduce(
        IReadOnlyList<SteamGameChange> changes, SteamSnapshot current)
    {
        var kept = new List<SteamGameChange>();

        foreach (var change in changes)
        {
            var profile = current.Games
                .FirstOrDefault(game => game.Game.AppId == change.AppId);

            // Was die Momentaufnahme nicht kennt, kann sie nicht widerlegen. Der Schreiber
            // legt den Eintrag an — ein Wunsch fuer ein deinstalliertes Spiel ist gueltig.
            if (profile is null)
            {
                kept.Add(change);
                continue;
            }

            var proton = change.Proton is not null
                && string.Equals(change.Proton.ToolKey, profile.CompatToolKey, StringComparison.Ordinal)
                    ? null
                    : change.Proton;

            // null im Profil heisst „kein Eintrag", "" heisst „leerer Eintrag" — ein Wunsch ""
            // auf ein Profil ohne Eintrag ist deshalb nicht gleich (Spec §3.2).
            var options = change.LaunchOptions is not null
                && string.Equals(change.LaunchOptions, profile.LaunchOptions, StringComparison.Ordinal)
                    ? null
                    : change.LaunchOptions;

            if (proton is null && options is null)
            {
                continue;
            }

            kept.Add(change with { Proton = proton, LaunchOptions = options });
        }

        return kept;
    }
}
