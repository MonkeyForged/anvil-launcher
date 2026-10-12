using System.Globalization;

namespace Anvil.Core.Sessions;

/// <summary>
/// Erkennt Steams Startklammer in einer Argumentliste (Spec §4.2). Reine Textauswertung —
/// dieser Baustein fasst keine Datei an.
///
/// <para>
/// Steam schiebt <b>jeden</b> Spielstart durch seinen <c>reaper</c>-Prozess, das Proton-Spiel
/// wie das native. Deshalb ist die Klammer das Kriterium und nicht das Prefix: ein natives
/// Spiel hat keins und muss trotzdem erkannt werden (Projektplan M3, Abgleich A2).
/// </para>
///
/// <para>
/// <b>Die Reihenfolge der Pruefungen ist kein Zufall.</b> Am 2026-09-05 traf ein Probelauf
/// ueber das echte /proc die eigene Suchshell, weil das Wort <c>SteamLaunch</c> in ihrer
/// Kommandozeile stand. Die tragende Bedingung ist <c>argv[0]</c> endet auf <c>reaper</c>;
/// <c>SteamLaunch</c> und <c>AppId=</c> sind die Bestaetigung.
/// </para>
///
/// <para>
/// <b>Und was keine Klammer eines Spielstarts ist.</b> Vor jedem Proton-Spiel schiebt Steam
/// einen Vorbereitungslauf durch dieselbe Klammer: <c>iscriptevaluator.exe</c> prueft, ob
/// Abhaengigkeiten nachzuinstallieren sind, und faehrt dafuer einen vollstaendigen Wine-Prefix
/// hoch. Von aussen ist er von einem laufenden Spiel nicht zu unterscheiden — bis auf
/// <c>Install=1</c> in seiner Argumentliste. Gemessen am 2026-09-05: Anvil meldete ihn als
/// Sitzung, rund 3,3 Sekunden bevor das Spiel lief, und die Luecke danach war der einzige
/// Grund fuer die Kulanzzeit des Waechters (Spec S3.3a §2).
/// </para>
/// </summary>
public static class SteamLaunchChain
{
    private const string ReaperName = "reaper";
    private const string LaunchMarker = "SteamLaunch";
    private const string AppIdPrefix = "AppId=";

    /// <summary>
    /// Steams Vorbereitungslauf traegt dieses Argument (Spec S3.3a §2, gemessen am
    /// 2026-09-05). Verglichen wird ordinal und als ganzes Argument, wie die Startmarke.
    /// </summary>
    private const string InstallMarker = "Install=1";

    /// <summary>Die AppID des gestarteten Spiels, oder null: kein Steam-Spielstart.</summary>
    public static uint? ParseAppId(IReadOnlyList<string> arguments)
    {
        if (arguments.Count == 0
            || Path.GetFileName(arguments[0]) != ReaperName
            || !arguments.Contains(LaunchMarker, StringComparer.Ordinal)
            || arguments.Contains(InstallMarker, StringComparer.Ordinal))
        {
            return null;
        }

        foreach (var argument in arguments)
        {
            if (!argument.StartsWith(AppIdPrefix, StringComparison.Ordinal))
            {
                continue;
            }

            // NumberStyles.None: kein Vorzeichen, keine Trennzeichen, kein Leerraum.
            return uint.TryParse(
                argument[AppIdPrefix.Length..],
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var appId)
                ? appId
                : null;
        }

        return null;
    }

    private const string WindowsExecutableSuffix = ".exe";

    /// <summary>
    /// <b>Der Dateiname des Spiel-Programms</b> aus der Argumentliste der Klammer, oder
    /// <c>null</c>: kein Windows-Programm in der Kette (Spec S5.4a §3.1).
    ///
    /// <para>
    /// Genommen wird das <b>erste</b> Argument, das auf <c>.exe</c> endet. Die Kette laeuft von
    /// aussen nach innen — Klammer, Laufzeitumgebung, Proton, Spiel —, und keiner der aeusseren
    /// Teile traegt die Endung: Der Proton-Starter heisst <c>proton</c>, der Einstiegspunkt
    /// <c>_v2-entry-point</c>. Steams Vorbereitungslauf mit <c>iscriptevaluator.exe</c> ist
    /// schon an <c>Install=1</c> ausgeschieden und wird hier nie gefragt.
    /// </para>
    ///
    /// <para>
    /// <b>Nur der Dateiname, nie der Pfad.</b> Der Pfad liegt unter dem Heimatverzeichnis oder
    /// einem Einhaengepunkt; gebraucht wird er nicht, denn verglichen wird gegen <c>comm</c>.
    /// </para>
    /// </summary>
    public static string? ParseExecutableName(IReadOnlyList<string> arguments)
    {
        foreach (var argument in arguments)
        {
            if (argument.EndsWith(WindowsExecutableSuffix, StringComparison.OrdinalIgnoreCase)
                && Path.GetFileName(argument) is { Length: > 0 } name)
            {
                return name;
            }
        }

        return null;
    }
}
