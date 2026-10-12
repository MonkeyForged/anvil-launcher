using System.Globalization;

namespace Anvil.Core.Sessions;

/// <summary>
/// Findet Steams Spiel-Overlay zu einer AppID (Spec §4.5).
///
/// <para>
/// <b>Warum getrennt von der Sitzung.</b> <c>gameoverlayui</c> haengt am Spielprozess, gehoert
/// aber Steam: Es traegt kein <c>STEAM_COMPAT_DATA_PATH</c> und ist deshalb kein Teil von
/// <see cref="GameSession"/> (nachgemessen 2026-08-26, Uebergabe §2). Nach einem harten
/// Abbruch bleibt es mitsamt Fenster stehen — der Stop raeumt es getrennt ab.
/// </para>
/// </summary>
public sealed class SteamOverlayFinder
{
    private const string OverlayProgramName = "gameoverlayui";
    private const string GameIdOption = "-gameid";

    private readonly ProcFileSystem _procFileSystem;

    public SteamOverlayFinder(ProcFileSystem procFileSystem)
        => _procFileSystem = procFileSystem;

    /// <summary>Der Finder fuer den Betrieb: das echte /proc.</summary>
    public static SteamOverlayFinder ForCurrentSystem() => new(new ProcFileSystem("/proc"));

    /// <summary>
    /// Die Regel als reine Funktion: <c>argv[0]</c> ist das Overlay, und irgendein Argument
    /// nennt genau diese AppID — als <c>-gameid=&lt;id&gt;</c> oder als <c>-gameid</c> mit
    /// der Zahl dahinter (beide Formen kennt schon <c>game.sh kill_overlay</c>).
    ///
    /// <para>
    /// Getragen wird die Bedingung von <c>argv[0]</c>, nicht vom Vorkommen des Wortes: Sonst
    /// traefe sie jede Suchshell, die das Wort in ihrer eigenen Kommandozeile stehen hat —
    /// derselbe Fund wie in S3.1 (Spec S3.1 §2.2).
    /// </para>
    /// </summary>
    public static bool Matches(IReadOnlyList<string> arguments, uint appId)
    {
        if (arguments.Count == 0)
        {
            return false;
        }

        var program = arguments[0];
        var name = program[(program.LastIndexOf('/') + 1)..];
        if (!string.Equals(name, OverlayProgramName, StringComparison.Ordinal))
        {
            return false;
        }

        var wanted = appId.ToString(CultureInfo.InvariantCulture);
        for (var index = 1; index < arguments.Count; index++)
        {
            // Verglichen wird der ganze Wert, nie ein Praefix: -gameid 21147400 ist nicht
            // -gameid 2114740.
            if (string.Equals(arguments[index], GameIdOption + "=" + wanted, StringComparison.Ordinal))
            {
                return true;
            }

            if (string.Equals(arguments[index], GameIdOption, StringComparison.Ordinal)
                && index + 1 < arguments.Count
                && string.Equals(arguments[index + 1], wanted, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Die Overlay-Prozesse dieses Spiels, aufsteigend. Wirft nicht.</summary>
    public IReadOnlyList<int> Pids(uint appId)
        =>
        [
            .. _procFileSystem.Processes()
                .Where(process => Matches(process.Arguments, appId))
                .Select(process => process.Pid)
                .Order(),
        ];
}
