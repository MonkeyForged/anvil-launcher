using System.Diagnostics;
using Anvil.Core.Modules;
using Microsoft.Extensions.Logging;

namespace Anvil.Core.Sessions;

/// <summary>
/// Ein losgeloest gestarteter Prozess, so weit, wie der Starter ihn braucht (Spec S4.4 §3.4).
///
/// <para>
/// <b>Delegaten statt Interface</b> — Hausform (<c>PackageStore</c>, <c>AttachWaiter</c>). Und
/// <b>bewusst ohne „toete ihn"</b>: Diese Form kann nicht abraeumen, und genau das ist die
/// Zusicherung (E-12). Ein <c>Kill()</c> riss im Vorgaengerprojekt den ganzen Container ab.
/// </para>
/// </summary>
public sealed record StartedProcess(int Pid, Func<bool> HasExited, Func<int?> ExitCode);

/// <summary>Wie ein Anhaengen ausgegangen ist.</summary>
public enum AttachStartOutcome
{
    /// <summary>Gestartet. Ob das Programm im Container lebt, misst erst S4.5.</summary>
    Started,

    /// <summary>Steams Startclient wurde nicht gefunden.</summary>
    NoLaunchClient,

    /// <summary>Das Programm liegt nicht da, wo das Modul es nennt.</summary>
    ProgramMissing,

    /// <summary>Das Spiel war laenger weg als die Kulanzzeit.</summary>
    GameGone,

    /// <summary>Das Spiel laeuft, nimmt aber nichts an — der Normalfall ist ein natives Spiel.</summary>
    NotAttachable,

    /// <summary>Der Startclient liess sich nicht starten oder starb sofort mit einem Fehlercode.</summary>
    StartFailed,
}

/// <param name="Outcome">Der Ausgang.</param>
/// <param name="Reason">
/// Ein ganzer englischer Satz bei jedem Fehlausgang, <c>null</c> bei Erfolg — dieselbe Form wie
/// <c>ModuleReadiness</c>. Er geht unveraendert vor den Nutzer, sobald M5 einen Knopf dafuer hat.
/// </param>
/// <param name="Pid">Die PID des gestarteten Startclients; <c>null</c> bei jedem Fehlausgang.</param>
/// <param name="Wait">
/// Wie lange auf „ist anhaengbar" gewartet wurde und wie das Warten ausging (S5.1, E-7);
/// <c>null</c>, wenn der Starter <b>vor</b> dem Warten abgesagt hat. Der Starter kennt die Zahl
/// ohnehin — wer sie hier nicht mitgibt, zwingt seinen Aufrufer, eine zweite Uhr zu stellen.
/// </param>
public readonly record struct AttachStart(
    AttachStartOutcome Outcome, string? Reason, int? Pid, AttachWait? Wait);

/// <summary>
/// Die Klammer des Anhaengens (Spec S4.4 §3.4): aus AppID und Programm wird ein laufendes
/// Programm im Container des Spiels.
///
/// <para>
/// <b>Die Reihenfolge ist die Entscheidung</b> (E-10): Erst die billigen Absagen — kein
/// Startclient, kein Programm —, dann das Warten, das bis zu zwei Minuten dauern darf, dann der
/// Start. Wer zuerst wartet, verbraucht 120 Sekunden fuer eine Antwort, die vorher feststand.
/// </para>
///
/// <para>
/// <b>Nie abwarten, nie abraeumen</b> (E-12). Der Startclient bleibt am Leben, solange das
/// Programm laeuft — pv-adverb ist Subreaper. Ein abgewarteter Aufruf mit Zeitgrenze riss im
/// Vorgaengerprojekt beim Ablauf den ganzen Container ab und toetete das Programm mitsamt dem
/// Spiel (poc-findings.md §4). Ein Fehlschlag raeumt hier nichts auf.
/// </para>
/// </summary>
public sealed class AttachStarter
{
    /// <summary>
    /// So lange wird nach dem Start hingesehen. Danach gilt der Start als geglueckt: Was
    /// spaeter im Container stirbt, ist keine Aussage mehr ueber den Aufruf.
    /// </summary>
    private static readonly TimeSpan DefaultStartWindow = TimeSpan.FromSeconds(3);

    /// <summary>Der Takt innerhalb des Startfensters.</summary>
    private static readonly TimeSpan DefaultStartTick = TimeSpan.FromMilliseconds(200);

    /// <summary>
    /// Der Weg in eine eigene Sitzung (E-11). .NET kennt kein <c>start_new_session</c>; ohne
    /// eigene Sitzung haengt der Startclient in der Prozessgruppe des Aufrufers, und ein Strg-C
    /// im Terminal — der Live-Test laeuft dort — erreicht ihn und ueber ihn den Container.
    /// <c>--wait</c> erhaelt dabei den Blick auf den Ausgang.
    /// </summary>
    private const string SetsidPath = "/usr/bin/setsid";

    private readonly Func<string?> _findLaunchClient;
    private readonly Func<uint, CancellationToken, Task<AttachWait>> _waitForAttachable;
    private readonly Func<string, bool> _fileExists;
    private readonly Func<ProcessStartInfo, StartedProcess> _start;
    private readonly ILogger? _logger;
    private readonly TimeProvider _time;
    private readonly TimeSpan _startWindow;
    private readonly TimeSpan _startTick;

    /// <param name="findLaunchClient">
    /// Im Betrieb <c>() =&gt; SteamLaunchClient.Find(steamRoot)</c>. Delegat, weil die
    /// Steam-Wurzel aus einem Scan kommt, der sich im Betrieb aendern kann.
    /// </param>
    /// <param name="waitForAttachable">
    /// Im Betrieb <c>waiter.WaitAsync</c> — der erste Verbraucher der Naht aus S4.3
    /// (architecture.md §4.3).
    /// </param>
    /// <param name="fileExists">
    /// Im Betrieb <c>File.Exists</c>. Als Delegat, damit die Tests kopflos bleiben; dieselbe
    /// Bauform wie die Umgebungsabfrage in <c>SteamScanner</c>.
    /// </param>
    /// <param name="start">
    /// Die einzige Stelle, die einen Prozess startet. Im Betrieb ein Lambda um
    /// <see cref="Process.Start(ProcessStartInfo)"/>, in Tests ein Aufzeichner.
    /// </param>
    public AttachStarter(
        Func<string?> findLaunchClient,
        Func<uint, CancellationToken, Task<AttachWait>> waitForAttachable,
        Func<string, bool> fileExists,
        Func<ProcessStartInfo, StartedProcess> start,
        ILogger? logger = null,
        TimeProvider? timeProvider = null,
        TimeSpan? startWindow = null,
        TimeSpan? startTick = null)
    {
        _findLaunchClient = findLaunchClient;
        _waitForAttachable = waitForAttachable;
        _fileExists = fileExists;
        _start = start;
        _logger = logger;
        _time = timeProvider ?? TimeProvider.System;
        _startWindow = startWindow ?? DefaultStartWindow;
        _startTick = startTick ?? DefaultStartTick;
    }

    /// <summary>
    /// Haengt <paramref name="program"/> an das Spiel <paramref name="appId"/> an. Wirft nur bei
    /// Abbruch; jeder andere Ausgang ist ein Ergebnis mit Grund.
    /// </summary>
    public async Task<AttachStart> StartAsync(
        uint appId, AttachProgram program, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var client = _findLaunchClient();
        if (client is null)
        {
            return Refuse(
                AttachStartOutcome.NoLaunchClient,
                "Steam's launch client was not found — is Steam installed?");
        }

        if (!_fileExists(program.ExecutablePath))
        {
            return Refuse(
                AttachStartOutcome.ProgramMissing,
                $"{program.ExecutablePath} does not exist — nothing to attach.");
        }

        var wait = await _waitForAttachable(appId, cancellationToken).ConfigureAwait(false);
        switch (wait.Outcome)
        {
            case AttachOutcome.GameGone:
                return Refuse(
                    AttachStartOutcome.GameGone,
                    "The game stopped before it could be attached to.",
                    wait);
            case AttachOutcome.TimedOut:
                return Refuse(
                    AttachStartOutcome.NotAttachable,
                    "The game never accepted a program in its container — is the launcher service set?",
                    wait);
            default:
                break;
        }

        StartedProcess started;
        try
        {
            started = _start(Detached(SteamLaunchClient.StartInfoFor(client, appId, program)));
        }
        catch (Exception exception)
        {
            _logger?.LogError(exception, "cannot start the launch client for appid {AppId}", appId);
            return Refuse(
                AttachStartOutcome.StartFailed,
                "The launch client could not be started — see the log for details.");
        }

        var early = await WatchTheStartAsync(started, wait, cancellationToken).ConfigureAwait(false);
        if (early is { } failure)
        {
            return failure;
        }

        _logger?.LogInformation(
            "attached to appid {AppId}: {Path} (pid {Pid})", appId, program.ExecutablePath, started.Pid);
        return new AttachStart(AttachStartOutcome.Started, null, started.Pid, wait);
    }

    /// <summary>
    /// Sieht dem Start <see cref="_startWindow"/> lang zu (E-12). <c>null</c> heisst „nichts
    /// dagegen einzuwenden".
    ///
    /// <para>
    /// <b>Ein sauberes Ende ist Erfolg</b>, kein Fehlschlag: Der Startclient hat dann seine
    /// Arbeit getan, und das Programm laeuft im Container weiter. Nur ein Fehlercode <i>in
    /// diesem Fenster</i> ist eine Aussage ueber den Aufruf.
    /// </para>
    /// </summary>
    private async Task<AttachStart?> WatchTheStartAsync(
        StartedProcess started, AttachWait wait, CancellationToken cancellationToken)
    {
        var deadline = _time.GetUtcNow() + _startWindow;
        using var timer = new PeriodicTimer(_startTick, _time);

        while (_time.GetUtcNow() < deadline)
        {
            await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false);

            if (!started.HasExited())
            {
                continue;
            }

            var code = started.ExitCode();
            if (code is null or 0)
            {
                return null;
            }

            return Refuse(
                AttachStartOutcome.StartFailed,
                $"The launch client exited with code {code} — see the log for details.",
                wait);
        }

        return null;
    }

    private AttachStart Refuse(AttachStartOutcome outcome, string reason, AttachWait? wait = null)
    {
        _logger?.LogWarning("attach refused: {Outcome} — {Reason}", outcome, reason);
        return new AttachStart(outcome, reason, null, wait);
    }

    /// <summary>
    /// Legt <c>setsid --wait</c> vor den Aufruf, wenn es das auf dieser Maschine gibt (E-11);
    /// sonst bleibt der Aufruf, wie er ist. Fehlt <c>setsid</c>, ist nur dieser Schutz weg,
    /// sonst nichts.
    /// </summary>
    private ProcessStartInfo Detached(ProcessStartInfo info)
    {
        if (!_fileExists(SetsidPath))
        {
            return info;
        }

        var detached = new ProcessStartInfo(SetsidPath) { UseShellExecute = false };
        detached.ArgumentList.Add("--wait");
        detached.ArgumentList.Add(info.FileName);
        foreach (var argument in info.ArgumentList)
        {
            detached.ArgumentList.Add(argument);
        }

        return detached;
    }
}
