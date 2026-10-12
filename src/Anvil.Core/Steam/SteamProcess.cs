using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Anvil.Core.Steam;

/// <summary>Was ein Beenden ergeben hat (Spec S4.2 §4.4).</summary>
public enum SteamStopOutcome
{
    Stopped,
    NotRunning,
    TimedOut,

    /// <summary>
    /// Kein brauchbares Kommando: nicht gefunden, oder da, aber nicht startbar (Spec E-F).
    /// Warum genau, steht im Log.
    /// </summary>
    CommandMissing,
}

/// <summary>Was ein Starten ergeben hat (Spec S4.2 §4.4).</summary>
public enum SteamStartOutcome
{
    Started,
    AlreadyRunning,
    TimedOut,

    /// <summary>Kein brauchbares Kommando (Spec E-F) — wie beim Beenden.</summary>
    CommandMissing,
}

/// <summary>
/// Beendet und startet Steam (Spec S4.2 §4.4). <b>Beurteilt wird immer der Prozessbaum, nie
/// der Ausgang eines Kommandos:</b> Ein „Erfolg" mit haengendem Client waere der gefaehrlichste
/// denkbare Fehlschlag — die Transaktion (S4.4) schriebe, waehrend Steam noch aus dem Speicher
/// zurueckschreibt.
///
/// <para>
/// <b>Alle Nahtstellen sind Delegaten</b>, wie in <c>GameLauncher</c>: die Sonde, die
/// Kommando-Suche und der Prozessstart. Die Zeit kommt ueber <see cref="TimeProvider"/>.
/// </para>
///
/// <para>
/// <b>Ob ein Spiel laeuft, prueft dieser Ablauf nicht</b> (Spec E-G, Entscheidung 4):
/// <c>steam -shutdown</c> beendet jedes laufende Spiel mit. Der Riegel liegt beim Aufrufer —
/// der Transaktion ueber den Waechter, der einen Wahrheit ueber „was laeuft".
/// </para>
/// </summary>
public sealed class SteamProcess
{
    /// <summary>
    /// Die Fristen (Spec §4.4). Beenden: Archiv-Messungen 2–16 s, Steam raeumt selbst noch
    /// auf. Starten: Steam prueft beim Start auf Updates, bevor die Oberflaeche steht.
    /// Der Takt ist zehnmal feiner als die Sekunden-Uhr des Waechters, ohne spuerbare Last.
    /// </summary>
    private static readonly TimeSpan Poll = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan StopLimit = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan StartLimit = TimeSpan.FromSeconds(120);

    /// <summary>
    /// Zwei leere Messungen hintereinander, bevor „beendet" gilt: Steam schreibt beim
    /// Beenden noch aus dem Speicher (PoC §5b). Bereitschaft dagegen faellt nicht zurueck —
    /// dort genuegt eine.
    /// </summary>
    private const int StopConfirmations = 2;
    private const int StartConfirmations = 1;

    private readonly Func<SteamProcessSnapshot> _look;
    private readonly Func<string?> _locateCommand;
    private readonly Func<ProcessStartInfo, Task<int>> _run;
    private readonly ILogger? _logger;
    private readonly TimeProvider _time;

    /// <param name="look">Die Sonde — die Composition Root reicht <c>probe.Look</c>.</param>
    /// <param name="locateCommand">
    /// Wo „steam" liegt, je Aufruf gesucht (Spec E-J) — die Composition Root reicht
    /// <c>SteamCommand.Find</c> mit Umgebung und aktueller Steam-Wurzel.
    /// </param>
    /// <param name="run">
    /// Startet den Prozess und liefert die Task auf sein Ende mit dem Exit-Code (Spec E-H).
    /// Der Ablauf wartet nie darauf; der Ausgang wird nur protokolliert. Ein synchroner Wurf
    /// heisst „liess sich nicht starten".
    /// </param>
    /// <param name="logger">Die Mitschrift, Kategorie <c>Steam</c>; null heisst „stumm".</param>
    /// <param name="timeProvider">Die Uhr; null heisst die echte.</param>
    public SteamProcess(
        Func<SteamProcessSnapshot> look,
        Func<string?> locateCommand,
        Func<ProcessStartInfo, Task<int>>? run = null,
        ILogger? logger = null,
        TimeProvider? timeProvider = null)
    {
        _look = look;
        _locateCommand = locateCommand;
        _run = run ?? RunProcess;
        _logger = logger;
        _time = timeProvider ?? TimeProvider.System;
    }

    /// <summary>Was die Sonde gerade sagt.</summary>
    public SteamProcessSnapshot Look() => _look();

    /// <summary>
    /// Beendet Steam und wartet, bis <b>kein</b> Steam-Prozess mehr da ist. Lief Steam nicht,
    /// ist das ein uebersprungener Schritt, kein Fehler. <b>Beendet laufende Spiele mit</b> —
    /// der Riegel dagegen ist Sache des Aufrufers (Spec E-G).
    /// </summary>
    public async Task<SteamStopOutcome> StopAsync(CancellationToken cancellationToken = default)
    {
        var startedAt = _time.GetTimestamp();

        // Nur die Oberflaeche uebrig (ein zurueckgebliebener Webhelper) zaehlt hier mit:
        // -shutdown raeumt auch das ab, und nur so ist das Feld hinterher wirklich leer.
        if (!_look().Alive)
        {
            _logger?.LogInformation("steam is not running — shutdown skipped");
            return SteamStopOutcome.NotRunning;
        }

        if (Locate() is not { } command)
        {
            return SteamStopOutcome.CommandMissing;
        }

        _logger?.LogInformation("shutting down steam: {Command} -shutdown", command);
        if (!Run(
                SteamCommand.ShutdownStartInfoFor(command),
                "steam -shutdown could not be run: {Command}",
                command,
                "steam -shutdown"))
        {
            return SteamStopOutcome.CommandMissing;
        }

        var gone = await WaitUntilAsync(
                () => !_look().Alive, StopConfirmations, StopLimit, cancellationToken)
            .ConfigureAwait(false);
        if (!gone)
        {
            _logger?.LogWarning(
                "steam did not shut down within {Seconds}s", StopLimit.TotalSeconds);
            return SteamStopOutcome.TimedOut;
        }

        _logger?.LogInformation(
            "steam shut down after {Seconds:F1}s",
            _time.GetElapsedTime(startedAt).TotalSeconds);
        return SteamStopOutcome.Stopped;
    }

    /// <summary>
    /// Startet Steam losgeloest und wartet, bis Client <b>und</b> Oberflaeche da sind. Laeuft
    /// Steam schon, ist das kein Fehler. Ein Webhelper ohne Client ist ein Rest, kein Steam
    /// (bekannte Grenze, Spec §8).
    /// </summary>
    public async Task<SteamStartOutcome> StartAsync(CancellationToken cancellationToken = default)
    {
        var startedAt = _time.GetTimestamp();

        if (_look().Running)
        {
            _logger?.LogInformation("steam is already running — start skipped");
            return SteamStartOutcome.AlreadyRunning;
        }

        if (Locate() is not { } command)
        {
            return SteamStartOutcome.CommandMissing;
        }

        _logger?.LogInformation("starting steam: {Command}", command);
        if (!Run(
                SteamCommand.StartStartInfoFor(command),
                "steam could not be started: {Command}",
                command,
                "steam"))
        {
            return SteamStartOutcome.CommandMissing;
        }

        var ready = await WaitUntilAsync(
                () =>
                {
                    var snapshot = _look();
                    return snapshot.Running && snapshot.UiReady;
                },
                StartConfirmations,
                StartLimit,
                cancellationToken)
            .ConfigureAwait(false);
        if (!ready)
        {
            _logger?.LogWarning(
                "steam did not become ready within {Seconds}s", StartLimit.TotalSeconds);
            return SteamStartOutcome.TimedOut;
        }

        _logger?.LogInformation(
            "steam is ready after {Seconds:F1}s",
            _time.GetElapsedTime(startedAt).TotalSeconds);
        return SteamStartOutcome.Started;
    }

    private string? Locate()
    {
        var command = _locateCommand();
        if (command is null)
        {
            _logger?.LogWarning("steam command not found in PATH or steam root");
        }

        return command;
    }

    /// <summary>
    /// Startet den Prozess; <c>false</c> heisst „liess sich nicht starten". Auf das Ende
    /// wartet niemand — der Exit-Code wird protokolliert, sobald er kommt, und nie bewertet
    /// (Spec E-H). Beim Starten laeuft Steam Stunden; die Task lebt so lange mit.
    /// </summary>
    private bool Run(ProcessStartInfo info, string failureMessage, string command, string what)
    {
        Task<int> exit;
        try
        {
            exit = _run(info);
        }
        catch (Exception exception)
        {
            _logger?.LogWarning(exception, failureMessage, command);
            return false;
        }

        _ = LogExitAsync(exit, what);
        return true;
    }

    private async Task LogExitAsync(Task<int> exit, string what)
    {
        try
        {
            var code = await exit.ConfigureAwait(false);
            _logger?.LogDebug("{What} exited with {ExitCode}", what, code);
        }
        catch (Exception exception)
        {
            // Der Prozess lief an und starb schraeg — fuers Log, nicht fuer die Entscheidung.
            _logger?.LogDebug(exception, "{What} ended with an error", what);
        }
    }

    /// <summary>
    /// Wartet, bis die Bedingung <paramref name="confirmations"/>-mal hintereinander stimmt.
    /// Die erste Messung passiert sofort, danach im Takt; <c>false</c> heisst Frist abgelaufen.
    /// Ein Abbruch wirft — das ist die Antwort auf eine Bitte des Aufrufers, kein Normalfall.
    /// </summary>
    private async Task<bool> WaitUntilAsync(
        Func<bool> condition,
        int confirmations,
        TimeSpan limit,
        CancellationToken cancellationToken)
    {
        var startedAt = _time.GetTimestamp();
        var seen = 0;
        while (true)
        {
            seen = condition() ? seen + 1 : 0;
            if (seen >= confirmations)
            {
                return true;
            }

            if (_time.GetElapsedTime(startedAt) >= limit)
            {
                return false;
            }

            await Task.Delay(Poll, _time, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Der Standardwert fuer <c>run</c>: der einzige ungepruefte Teil dieser Klasse.
    /// <b>Der Start ist synchron</b>, damit ein Fehlstart als Wurf beim Aufrufer ankommt und
    /// nicht als fehlgeschlagene Task, auf die niemand wartet; erst das Warten aufs Ende ist
    /// asynchron.
    /// </summary>
    private static Task<int> RunProcess(ProcessStartInfo info)
    {
        var process = Process.Start(info)
            ?? throw new InvalidOperationException("process did not start: " + info.FileName);
        return WaitForExitAsync(process);
    }

    private static async Task<int> WaitForExitAsync(Process process)
    {
        using (process)
        {
            await process.WaitForExitAsync().ConfigureAwait(false);
            return process.ExitCode;
        }
    }
}
