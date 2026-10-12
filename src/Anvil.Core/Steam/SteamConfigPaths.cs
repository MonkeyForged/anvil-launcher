using System.Globalization;

namespace Anvil.Core.Steam;

/// <summary>
/// Wo Steams zwei Konfigurationsdateien liegen (Spec S4.3 §4.1) — und seit S4.3a der
/// App-Cache, aus demselben Grund. Reine Pfadrechnung, kein Dateizugriff — <b>eine</b>
/// Stelle weiß es, Leser (<see cref="GameLaunchOptions"/>, <see cref="CompatToolMapping"/>,
/// <see cref="ValveCompatTools"/>) und Schreiber (<see cref="SteamConfigWriter"/>) fragen hier.
/// </summary>
public static class SteamConfigPaths
{
    public const string ConfigFileName = "config.vdf";

    public const string LocalConfigFileName = "localconfig.vdf";

    public const string AppInfoFileName = "appinfo.vdf";

    /// <summary><c>&lt;root&gt;/config/config.vdf</c> — trägt <c>CompatToolMapping</c>.</summary>
    public static string Config(string steamRoot)
        => Path.Combine(steamRoot, "config", ConfigFileName);

    /// <summary>
    /// <c>&lt;root&gt;/userdata/&lt;accountId&gt;/config/localconfig.vdf</c> — trägt
    /// <c>LaunchOptions</c>. Die Konto-ID ist der Ordnername (Spec S2.2 §6.4).
    /// </summary>
    public static string LocalConfig(string steamRoot, uint accountId)
        => Path.Combine(
            steamRoot, "userdata", accountId.ToString(CultureInfo.InvariantCulture),
            "config", LocalConfigFileName);

    /// <summary>
    /// <c>&lt;root&gt;/appcache/appinfo.vdf</c> — Steams binärer App-Cache, trägt die Liste der
    /// Valve-Compat-Tools (Spec S4.3a §2). Kein Konfigurationsfile, aber derselbe Grund für
    /// diese Stelle: ein Pfad, eine Wahrheit.
    /// </summary>
    public static string AppInfo(string steamRoot)
        => Path.Combine(steamRoot, "appcache", AppInfoFileName);

    /// <summary>
    /// Wohin eine Sicherung zurueckgehoert (Spec S4.7 E-D): Der Dateiname allein sagt es,
    /// weil Anvil nur diese zwei Dateien schreibt. <c>null</c> heisst „nicht zurueckspielbar" —
    /// ein fremder Name, oder <c>localconfig.vdf</c> ohne bekanntes Konto.
    /// Gross-/Kleinschreibung zaehlt: Steams Dateien heissen genau so.
    /// </summary>
    public static string? TargetFor(string fileName, string steamRoot, uint? accountId)
        => fileName switch
        {
            ConfigFileName => Config(steamRoot),
            LocalConfigFileName => accountId is { } id ? LocalConfig(steamRoot, id) : null,
            _ => null,
        };

    /// <summary>
    /// In welcher Datei ein Feld liegt (Spec S4.8 §3.3). Der Vermerk je Sicherung nennt nur die
    /// Felder <b>dieser</b> Datei — eine <c>config.vdf</c>-Sicherung, die Startoptionen
    /// auflistet, wäre schlicht falsch.
    ///
    /// <para>
    /// <b>Doppelpunkt, bewusst</b> (E-K): <see cref="SteamConfigWriter.Apply"/> trifft dieselbe
    /// Zuordnung durch seinen Aufbau — zwei Aufrufe, einer je Datei. Ihn darauf umzubauen brächte
    /// keine Sicherheit dazu und kostet Risiko in der einzigen Klasse, die in Steams Dateien
    /// schreibt. Wer eines ändert, ändert beides.
    /// </para>
    /// </summary>
    public static string FileFor(SteamField field)
        => field switch
        {
            SteamField.Proton => ConfigFileName,
            SteamField.LaunchOptions => LocalConfigFileName,
            _ => throw new ArgumentOutOfRangeException(nameof(field)),
        };
}
