using Anvil.Core.Sessions;

namespace Anvil.Core.Steam;

/// <summary>Wozu ein Prozess in Steams Baum gehoert (Spec S4.2 §4.2).</summary>
public enum SteamProcessRole
{
    /// <summary>Kein Steam — auch nicht das Overlay, der Logger oder ein Spiel.</summary>
    None,

    /// <summary><c>bash …/steam.sh</c> — zaehlt als <see cref="SteamProcessSnapshot.Running"/>.</summary>
    LaunchScript,

    /// <summary><c>ubuntu12_32/steam</c> — zaehlt als <see cref="SteamProcessSnapshot.Running"/>.</summary>
    Client,

    /// <summary>
    /// <c>steamwebhelper</c> oder <c>steam-runtime-launcher-service --alongside-steam</c> —
    /// zaehlt als <see cref="SteamProcessSnapshot.UiReady"/>.
    /// </summary>
    UserInterface,
}

/// <summary>
/// Sieht in /proc nach, ob Steam laeuft (Spec S4.2 §4.2). Liest nur Argumentlisten, nie
/// Prozessumgebungen; eine Frage, keine Schleife.
///
/// <para>
/// <b>Vier Regeln, alle an <c>argv[0]</c> festgemacht</b> (Lehre aus S3.1: das blosse Wort
/// traefe jede Suchshell). Ausdruecklich <b>nicht</b> Steam: <c>gameoverlayui</c> (ueberlebt
/// das Spielende — am 2026-09-06 standen zwei davon ohne Spiel), <c>srt-logger</c>,
/// die <c>/proc/self/exe</c>-Kinder des Webhelpers und der gleichnamige Launcher-Dienst
/// eines Spiels (er traegt <c>--bus-name=…</c> statt <c>--alongside-steam</c>).
/// </para>
///
/// <para>
/// <b>Faengt nichts ab</b> (Spec E-C): <see cref="ProcFileSystem"/> wirft, wenn das
/// Listing misslingt, und genau das soll hier durchschlagen — eine leere Liste waere von
/// „Steam ist weg" nicht zu unterscheiden, und das ist beim Beenden die teuerste Fehlmessung.
/// </para>
/// </summary>
public sealed class SteamProcessProbe
{
    private const string LaunchScriptSuffix = "/steam.sh";
    private const string ClientName = "steam";
    private const string WebHelperName = "steamwebhelper";
    private const string LauncherServiceName = "steam-runtime-launcher-service";
    private const string AlongsideSteamFlag = "--alongside-steam";

    private static readonly string[] Shells = ["bash", "sh"];
    private static readonly string[] ClientDirectories = ["ubuntu12_32", "ubuntu12_64"];

    private readonly ProcFileSystem _procFileSystem;

    public SteamProcessProbe(ProcFileSystem procFileSystem)
        => _procFileSystem = procFileSystem;

    /// <summary>Die Sonde fuer den Betrieb: das echte /proc.</summary>
    public static SteamProcessProbe ForCurrentSystem() => new(new ProcFileSystem("/proc"));

    /// <summary>Die vier Regeln als reine Funktion ueber die Argumentliste.</summary>
    public static SteamProcessRole Classify(IReadOnlyList<string> arguments)
    {
        if (arguments.Count == 0)
        {
            return SteamProcessRole.None;
        }

        var program = arguments[0];
        var name = Path.GetFileName(program);

        // Das Startskript: argv[0] ist der Interpreter (Shebang „#!/usr/bin/env bash"), das
        // Skript steht an Position 1 — und nur dort (Spec E-B). „bash -c 'cat …/steam.sh'"
        // hat dort „-c".
        if (Shells.Contains(name, StringComparer.Ordinal))
        {
            return arguments.Count >= 2
                && arguments[1].EndsWith(LaunchScriptSuffix, StringComparison.Ordinal)
                ? SteamProcessRole.LaunchScript
                : SteamProcessRole.None;
        }

        // Der Hauptclient: „steam" aus ubuntu12_32 oder ubuntu12_64. /usr/bin/steam ist ein
        // Skript, das per exec im Startskript aufgeht — es erscheint nie als Prozess.
        if (string.Equals(name, ClientName, StringComparison.Ordinal))
        {
            var directory = Path.GetFileName(Path.GetDirectoryName(program) ?? string.Empty);
            return ClientDirectories.Contains(directory, StringComparer.Ordinal)
                ? SteamProcessRole.Client
                : SteamProcessRole.None;
        }

        // Der Webhelper: nur der Dateiname, Steam startet ihn als „./steamwebhelper".
        if (string.Equals(name, WebHelperName, StringComparison.Ordinal))
        {
            return SteamProcessRole.UserInterface;
        }

        // Steams Launcher-Dienst — der eines Spiels heisst gleich, traegt die Marke aber nicht.
        if (string.Equals(name, LauncherServiceName, StringComparison.Ordinal)
            && arguments.Contains(AlongsideSteamFlag, StringComparer.Ordinal))
        {
            return SteamProcessRole.UserInterface;
        }

        return SteamProcessRole.None;
    }

    /// <summary>Ein Durchgang durch /proc. Wirft, wenn das Listing misslingt (Spec E-C).</summary>
    public SteamProcessSnapshot Look()
    {
        var running = false;
        var uiReady = false;

        foreach (var process in _procFileSystem.Processes())
        {
            switch (Classify(process.Arguments))
            {
                case SteamProcessRole.LaunchScript:
                case SteamProcessRole.Client:
                    running = true;
                    break;
                case SteamProcessRole.UserInterface:
                    uiReady = true;
                    break;
            }

            if (running && uiReady)
            {
                // Mehr kann die Momentaufnahme nicht werden — der Rest des Baums ist egal.
                break;
            }
        }

        return new SteamProcessSnapshot(running, uiReady);
    }
}
