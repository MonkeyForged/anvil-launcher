using System.Globalization;

namespace Anvil.Core.Sessions;

/// <summary>
/// Der Name, unter dem der Container eines Spiels auf dem Sitzungsbus erreichbar ist.
///
/// <para>
/// <b>Warum eine eigene Stelle fuer einen String.</b> Seit S4.3 fragen zwei Verbraucher nach
/// demselben Namen: die Sonde, um zu <i>warten</i>, und <see cref="SteamLaunchClient"/>, um
/// im Container zu <i>starten</i>. Zwei Schreibweisen waeren zwei Gelegenheiten, verschieden
/// zu antworten — und der Fehler fiele erst live auf, weil beide Seiten fuer sich genommen
/// plausibel aussehen (Spec S4.3 §3.1).
/// </para>
/// </summary>
public static class SteamBusName
{
    /// <summary>
    /// <c>com.steampowered.App&lt;appid&gt;</c> — gemessen der Name, dessen Erscheinen das
    /// erste Signal ist, auf das ein Anhaengen folgen kann (messungen-anhaengen.md §2).
    /// </summary>
    public static string ForApp(uint appId)
        => "com.steampowered.App" + appId.ToString(CultureInfo.InvariantCulture);
}
