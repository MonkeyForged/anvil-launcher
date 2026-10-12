using Microsoft.Extensions.Logging;

namespace Anvil.Core.Sessions;

/// <summary>
/// Die eine Wahrheit ueber „was laeuft gerade" (Vision §3, Spec §4.5). Sieht in einem
/// einzigen Takt nach und meldet Beginn und Ende einer Sitzung; wer etwas davon braucht,
/// haengt sich an die Ereignisse, statt eine zweite Schleife zu bauen.
///
/// <para>
/// <b>Seit S5.4a heisst „laeuft" nicht mehr „die Startklammer existiert", sondern „das
/// Spiel-Programm lebt".</b> Der Grund ist gemessen (Spec S5.4 §9): Ein angehaengtes Programm
/// im Container haelt Steams Klammer am Leben, und wer sein Spiel im Spiel beendet, saehe es
/// sonst ewig als laufend. Der Anfang haengt weiterhin an der Klammer — bis zum
/// Spiel-Programm vergehen Sekunden, und so lange duerfte Anvil nicht „laeuft nicht" sagen,
/// waehrend Steam laengst startet (Spec S5.4a, E-2).
/// </para>
///
/// <para>
/// <b>Keine Kulanzzeit — und warum das kein Wagnis ist.</b> Bis zum 2026-09-05 hielt der
/// Waechter ein verschwundenes Spiel noch fuenf Sekunden fuer laufend. Der Grund war ein
/// gemessener Prozesswechsel kurz nach dem Start; die Marke flackerte sonst. Die Ursache war
/// aber nicht das Spiel, sondern Steams Vorbereitungslauf, den Anvil als Sitzung zaehlte:
/// Er faehrt einen vollen Wine-Prefix hoch, beendet sich, und erst danach startet das Spiel.
/// Seit <see cref="SteamLaunchChain"/> ihn an <c>Install=1</c> erkennt und aussortiert, sieht
/// der Waechter genau einen Prozess ueber die ganze Sitzung — es gibt nichts zu ueberbruecken.
/// Ein Verschwinden ist ein Ende, im selben Takt (Spec S3.3a).
/// </para>
///
/// <para>
/// <b>Kein Bestaetigungstakt</b> (Fund der externen Analyse, 2026-09-05). Bis dahin meldete
/// der erste Takt, der eine Sitzung vermisste, nie — er merkte sich nur den Zeitpunkt, und
/// erst der naechste entschied. Das sollte einen „unvollstaendigen Scan" abfangen, den es
/// nicht gibt: Ein lebender Prozess fehlt in einem gelungenen Verzeichnislisting nicht. Der
/// einzige echte Fehlerfall ist, dass sich /proc gar nicht auflisten laesst — und der gehoert
/// eine Schicht tiefer, wo er seit demselben Tag geworfen statt verschluckt wird. Hier faengt
/// ihn <see cref="PollOnce"/> und aendert dann nichts.
/// </para>
///
/// <para>
/// Die Ereignisse laufen auf dem Faden des Waechters. Das Umschalten auf den UI-Faden ist
/// Sache der Oberflaeche (S3.3, gereichter <c>dispatch</c> wie im LogViewModel).
/// </para>
/// </summary>
public sealed class GameSessionWatcher
{
    /// <summary>
    /// Ein Durchgang kostet gemessen 7,8 ms (Owner-Maschine, 580 Prozesse). Bei einer Sekunde
    /// Takt ist das unter einem Prozent eines Kerns — und genau eine Sekunde ist die
    /// Obergrenze dafuer, wie lange Anvil ein Spielende verschlaeft.
    /// </summary>
    private static readonly TimeSpan DefaultInterval = TimeSpan.FromSeconds(1);

    private readonly Func<IReadOnlyList<GameSession>> _scan;
    private readonly ILogger? _logger;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _interval;

    private readonly Dictionary<uint, GameSession> _known = [];

    /// <summary>
    /// Die Apps, deren Spiel-Programm schon einmal gelebt hat. <b>Die Aussage lautet „war da
    /// und ist weg", nicht „ist nicht da"</b> (Spec S5.4a §3.1, Falle 3): Zwischen der
    /// Startklammer und dem Spiel-Programm vergehen Sekunden, und wer sofort urteilte, machte
    /// aus jedem Spielanfang ein Spielende.
    /// </summary>
    private readonly HashSet<uint> _gameProgramSeen = [];

    /// <summary>
    /// <b>Der Nachlauf:</b> Apps, deren Spiel gegangen ist, waehrend ihre Startklammer noch
    /// steht (Spec S5.4a §3.4). Ohne ihn waere die Umstellung ein stiller Dauerfehler — die
    /// Klammer steht ja weiter, also faende der naechste Takt sie wieder und meldete einen
    /// neuen Spielanfang, im Sekundentakt, fuer ein Spiel, das nicht mehr laeuft.
    ///
    /// <para>
    /// Nach aussen ist der Nachlauf <b>kein dritter Zustand</b>: <see cref="IsRunning"/>,
    /// <see cref="For"/> und <see cref="Current"/> lesen allein <c>_known</c>.
    /// </para>
    /// </summary>
    private readonly HashSet<uint> _lingering = [];

    // Bis S3.1 lief der Waechter auf einem Faden. Ab S3.2 fragt der Launcher von seinem
    // eigenen (Spec S3.2 §4.6) — und ein Dictionary vertraegt kein gleichzeitiges Lesen und
    // Schreiben. Das Schloss deckt den Stand, nie das Melden: Ein fremder Handler unter dem
    // Schloss hielte den naechsten Takt an.
    private readonly Lock _gate = new();
    private readonly Dictionary<uint, List<TaskCompletionSource>> _waiting = [];

    /// <param name="scan">
    /// Woher der Stand kommt. Ein Delegat, kein Typ — dasselbe Muster wie
    /// <c>GameLibrary(Func&lt;SteamSnapshot&gt; scan)</c>: Die Composition Root reicht
    /// <c>scanner.Scan</c>, ein Test reicht seine eigene Liste.
    /// </param>
    public GameSessionWatcher(
        Func<IReadOnlyList<GameSession>> scan,
        ILogger? logger = null,
        TimeProvider? timeProvider = null,
        TimeSpan? interval = null)
    {
        _scan = scan;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _interval = interval ?? DefaultInterval;
    }

    /// <summary>Der gemeldete Stand — genau das, was der letzte gelungene Durchgang sah.</summary>
    public IReadOnlyList<GameSession> Current { get; private set; } = [];

    public event EventHandler<GameSession>? SessionStarted;

    public event EventHandler<GameSession>? SessionEnded;

    public GameSession? For(uint appId)
    {
        lock (_gate)
        {
            return _known.TryGetValue(appId, out var session) ? session : null;
        }
    }

    public bool IsRunning(uint appId)
    {
        lock (_gate)
        {
            return _known.ContainsKey(appId);
        }
    }

    /// <summary>
    /// Fertig, sobald der Waechter diese AppID nicht mehr fuehrt — die Bestaetigung, dass
    /// ein Stop gewirkt hat (Spec S3.2 §5.5).
    ///
    /// <para>
    /// <b>Fuehrt er sie beim Aufruf schon nicht, ist der Task sofort fertig.</b> Das ist der
    /// bekannte Wettlauf aus Spec S3.2 §5.5: Wer ein eben gestartetes Spiel stoppt, bevor der
    /// erste Takt es gesehen hat, bekommt sofort ein Ja. Ueber die Oberflaeche kann das nicht
    /// eintreten — der Stop-Knopf erscheint erst mit der Marke, und die kommt von hier.
    /// </para>
    /// </summary>
    public Task WaitForEndAsync(uint appId, CancellationToken cancellationToken)
    {
        TaskCompletionSource source;
        lock (_gate)
        {
            if (!_known.ContainsKey(appId))
            {
                return Task.CompletedTask;
            }

            // Asynchrone Fortsetzungen: Sonst liefe der Rest des Stopps im Takt-Faden des
            // Waechters weiter.
            source = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!_waiting.TryGetValue(appId, out var waiting))
            {
                waiting = [];
                _waiting[appId] = waiting;
            }

            waiting.Add(source);
        }

        return source.Task.WaitAsync(cancellationToken);
    }

    /// <summary>Ein Durchgang: nachsehen, vergleichen, melden. Wirft nie.</summary>
    public void PollOnce()
    {
        IReadOnlyList<GameSession> found;
        try
        {
            found = _scan();
        }
        catch (Exception exception)
        {
            // Ein Fehler beim Nachsehen ist keine Aussage ueber die Welt: nichts aendern,
            // kein Ereignis.
            _logger?.LogWarning(exception, "game session scan failed");
            return;
        }

        var started = new List<GameSession>();
        var ended = new List<(GameSession Session, string Reason)>();
        var waiters = new List<TaskCompletionSource>();

        lock (_gate)
        {
            var fresh = found.ToDictionary(session => session.AppId);

            foreach (var session in found)
            {
                if (_lingering.Contains(session.AppId))
                {
                    // Die Klammer steht noch, das Spiel war schon weg. Erst ein Spiel-Programm,
                    // das wieder lebt, ist ein neuer Start — alles andere waere jeder Takt.
                    if (session.Program is not { Alive: true })
                    {
                        continue;
                    }

                    _lingering.Remove(session.AppId);
                }

                if (session.Program is { Alive: true })
                {
                    _gameProgramSeen.Add(session.AppId);
                }

                if (_known.ContainsKey(session.AppId))
                {
                    // Da geblieben: still auf den frischen Stand bringen. PIDs und
                    // Launcher-Dienst wachsen im Lauf einer Sitzung nach — ohne das
                    // Nachziehen bliebe der erste Takt dauerhaft die Wahrheit, und der Stop
                    // haette eine veraltete Liste.
                    _known[session.AppId] = session;
                    continue;
                }

                _known[session.AppId] = session;
                started.Add(session);
            }

            foreach (var (appId, known) in _known.ToArray())
            {
                if (!fresh.TryGetValue(appId, out var current))
                {
                    _known.Remove(appId);
                    _gameProgramSeen.Remove(appId);
                    ended.Add((known, "launch chain gone"));
                    continue;
                }

                // Das Spiel war da und ist weg — das Ende dieser Story. Die Klammer steht
                // weiter; sie faellt erst, wenn niemand mehr im Container haengt.
                if (_gameProgramSeen.Contains(appId) && current.Program is { Alive: false } gone)
                {
                    _known.Remove(appId);
                    _gameProgramSeen.Remove(appId);
                    _lingering.Add(appId);
                    ended.Add((current, $"game program {gone.FileName} gone"));
                }
            }

            // Eine Klammer, die endlich faellt, raeumt ihren Nachlauf mit — ohne Ereignis,
            // denn das Ende war schon.
            _lingering.RemoveWhere(appId => !fresh.ContainsKey(appId));

            foreach (var (session, _) in ended)
            {
                if (_waiting.Remove(session.AppId, out var waiting))
                {
                    waiters.AddRange(waiting);
                }
            }

            // Erst den Stand setzen, dann melden: Wer im Ereignis auf Current schaut, soll den
            // Stand sehen, ueber den gerade berichtet wird.
            Current = [.. _known.Values.OrderBy(session => session.AppId)];
        }

        foreach (var session in started)
        {
            _logger?.LogInformation(
                "game session started: appid {AppId}, pid {Pid}, {ProcessCount} process(es), "
                + "launcher service {LauncherService}",
                session.AppId,
                session.LeadPid,
                session.Pids.Count,
                session.LauncherService ?? "none");
            SessionStarted?.Invoke(this, session);
        }

        foreach (var (session, reason) in ended)
        {
            _logger?.LogInformation(
                "game session ended: appid {AppId}, reason {Reason}", session.AppId, reason);
            SessionEnded?.Invoke(this, session);
        }

        // Auch die Wartenden erst nach dem Schloss: Ihre Fortsetzungen sind fremder Code.
        foreach (var waiter in waiters)
        {
            waiter.TrySetResult();
        }
    }

    /// <summary>
    /// Sieht sofort und danach im Takt nach, bis abgebrochen wird. Die Schleife ist
    /// absichtlich duenn — der ganze Verstand steckt in <see cref="PollOnce"/>.
    /// </summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(_interval, _timeProvider);
        PollOnce();
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                PollOnce();
            }
        }
        catch (OperationCanceledException)
        {
            // Der Abbruch ist der vorgesehene Weg, die Schleife zu beenden.
        }
    }
}
