namespace Anvil.Core.Sessions;

/// <summary>
/// Ein laufendes Steam-Spiel (Spec §4.3): die AppID, alle Prozesse der Sitzung, und was ein
/// Stop davon braucht.
///
/// <para>
/// <b>Was bewusst nicht darin steht:</b> kein Prefix-Pfad (die Prefix-Frage beantwortet
/// <see cref="Steam.GamePrefix"/> ohnehin strenger); kein D-Bus-Name (aus der AppID
/// ableitbar, S3.2 baut ihn dort, wo der Aufruf entsteht); nicht die vollstaendige Umgebung
/// (sie ist gross, enthaelt fremde Zugangsdaten und landete sonst in Log und Zustandsdatei);
/// kein Anzeigename (der Waechter kennt keine Bibliothek).
/// </para>
///
/// <para>
/// <b>Und seit S3.3a auch kein compatdata-Pfad:</b> Er wurde allein gebraucht, um die
/// Kill-Liste ueber die Umgebung fremder Prozesse zu suchen. Die kommt jetzt aus dem
/// Prozessbaum, und den Prefix, den die Oberflaeche zeigt, kennt die Steam-Bibliothek
/// (S2.3) — ihn hier zweitens zu fuehren waere eine zweite Wahrheit ueber dieselbe Sache.
/// </para>
/// </summary>
/// <param name="AppId">Die Steam-AppID des Spiels.</param>
/// <param name="Pids">
/// Die Prozesse der Sitzung, aufsteigend, nie leer. <b>Wie vollstaendig, sagt der Durchgang:</b>
/// <see cref="GameSessionScanner.Scan"/> liefert die mit Steams Startklammer,
/// <see cref="GameSessionScanner.ScanWithProcessTree"/> zusaetzlich alles, was daran haengt —
/// die Kill-Liste, die ein Stop braucht.
/// </param>
/// <param name="LauncherService">
/// Steams Launcher-Dienst, oder null. S3.2 waehlt daran den sanften Stop-Weg.
/// </param>
/// <param name="Program">
/// Das Spiel-Programm, oder <c>null</c> = <b>nicht beurteilbar</b> (Spec S5.4a §3.2): kein
/// <c>.exe</c> in der Klammer — ein natives Spiel —, oder der Prozessbaum war nicht lesbar.
///
/// <para>
/// <b>Der Vorgabewert ist die Wahrheit, nicht die Bequemlichkeit:</b> Eine von Hand gebaute
/// Sitzung hat kein gemessenes Spiel-Programm. Wer <c>null</c> bekommt, aendert nichts und
/// laesst die Sitzung wie bisher mit der Klammer enden.
/// </para>
/// </param>
public sealed record GameSession(
    uint AppId,
    IReadOnlyList<int> Pids,
    string? LauncherService,
    GameProgram? Program = null)
{
    /// <summary>Der aeusserste Prozess der Startkette — die niedrigste PID.</summary>
    public int LeadPid => Pids[0];
}
