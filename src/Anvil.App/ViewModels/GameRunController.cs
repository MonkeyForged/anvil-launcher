using Anvil.Core.Sessions;
using Microsoft.Extensions.Logging;

namespace Anvil.App.ViewModels;

/// <summary>
/// Die Start- und Stopp-<i>Phasen</i> der Oberfläche (Spec S4.5 §3.2) — ausgegliedert aus dem
/// <see cref="LibraryViewModel"/>, das mit zwölf Konstruktorparametern und siebenhundert Zeilen
/// keinen dritten Zuständigkeitsbereich mehr verträgt (Vormerkung seit S3.7).
///
/// <para>
/// <b>Der Regler kennt keine Karte.</b> Er sagt „für dieses Spiel läuft eine Phase" und meldet,
/// wenn sich das ändert; das <see cref="LibraryViewModel"/> hört zu und setzt die Marke — genau
/// wie es dem Wächter zuhört. Eine Aussage über die Oberfläche, nicht über das Spiel: <b>was
/// läuft, sagt allein der Wächter.</b>
/// </para>
///
/// <para>
/// <b>Der Regler hört selbst beim Wächter</b>, weil die Startphase mit der <i>Sitzung</i> endet
/// und nicht mit dem Rückgabewert des Starts. Dass beide beim selben Wächter hören, ist kein
/// zweiter Kreislauf — es ist dieselbe eine Wahrheit mit zwei Schlüssen.
/// </para>
/// </summary>
public sealed class GameRunController
{
    /// <summary>
    /// Der Deckel der Startphase (Projektplan Entscheidung 3). Steam zieht ein Spiel in
    /// Sekunden hoch — es kann aber auch nichts passieren, weil ein Update lädt oder ein
    /// Dialog im Weg steht. Ohne Deckel bliebe der Knopf für immer gesperrt.
    /// </summary>
    private static readonly TimeSpan StartWindow = TimeSpan.FromSeconds(60);

    private readonly Func<uint, StartOutcome> _startGame;
    private readonly Func<uint, CancellationToken, Task<StopOutcome>> _stopGame;
    private readonly Action<Action> _dispatch;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger? _logger;

    /// <summary>AppID → Deckel-Uhr der laufenden Startphase; zugleich die Menge „startet gerade".</summary>
    private readonly Dictionary<uint, ITimer> _starting = [];

    /// <summary>AppIDs mit laufendem Stop.</summary>
    private readonly HashSet<uint> _stopping = [];

    public GameRunController(
        GameSessionWatcher watcher,
        Func<uint, StartOutcome> startGame,
        Func<uint, CancellationToken, Task<StopOutcome>> stopGame,
        Action<Action> dispatch,
        TimeProvider? timeProvider = null,
        ILogger? logger = null)
    {
        _startGame = startGame;
        _stopGame = stopGame;
        _dispatch = dispatch;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger;

        // Die Startphase endet mit der Sitzung, nicht mit dem Rückgabewert des Starts.
        watcher.SessionStarted += (_, session)
            => _dispatch(() => EndStartPhase(session.AppId, timedOut: false));
    }

    /// <summary>Für dieses Spiel hat sich „läuft eine Phase" geändert.</summary>
    public event EventHandler<uint>? BusyChanged;

    /// <summary>
    /// Läuft für dieses Spiel gerade eine Phase? Eine Aussage über die Oberfläche, nicht über
    /// das Spiel.
    /// </summary>
    public bool IsBusy(uint appId)
        => _starting.ContainsKey(appId) || _stopping.Contains(appId);

    /// <summary>
    /// Startet ein Spiel über Steam. Der Rückgabewert des Delegaten sagt nur, ob der Aufruf
    /// abgesetzt wurde — <b>ob das Spiel läuft, sagt der Wächter</b>.
    /// </summary>
    public void Start(uint appId)
    {
        _logger?.LogInformation("start requested: appid {AppId}", appId);

        StartOutcome outcome;
        try
        {
            outcome = _startGame(appId);
        }
        catch (Exception error)
        {
            // Der Delegat startet einen fremden Prozess — fällt der aus, darf er den Aufrufer
            // nicht mitreißen.
            _logger?.LogWarning("start failed: appid {AppId}: {Reason}", appId, error.Message);
            return;
        }

        if (outcome is not StartOutcome.Started)
        {
            // Ein Fehlschlag sperrte den Knopf sonst sechzig Sekunden lang für nichts.
            _logger?.LogInformation(
                "start not begun: appid {AppId} ({Outcome})", appId, outcome);
            return;
        }

        BeginStartPhase(appId);
    }

    /// <summary>
    /// Beendet ein Spiel. <b>Das dauert</b> — der sanfte Weg meldet nach 4 bis 6 Sekunden, der
    /// schlimmste Fall läuft 11 Sekunden (Spec S3.3b E-F). Solange bleibt die Phase stehen.
    ///
    /// <para>
    /// <b>Die Marke erlischt nicht durch das Ergebnis, sondern durch den Wächter.</b> Meldet der
    /// Stop <c>Failed</c>, läuft das Spiel weiter — und das stimmt dann auch.
    /// </para>
    /// </summary>
    public async Task StopAsync(uint appId)
    {
        _stopping.Add(appId);
        Raise(appId);
        _logger?.LogInformation("stop requested: appid {AppId}", appId);

        StopOutcome outcome;
        try
        {
            // Task.Run, weil der Stop mit Arbeit beginnt, die dauern kann: Ohne
            // Launcher-Dienst holt er frische PIDs aus /proc — auf der Owner-Maschine
            // gemessen 1,6 bis 1,7 Sekunden. Ohne das Auslagern stünde die Oberfläche.
            outcome = await Task.Run(
                () => _stopGame(appId, CancellationToken.None)).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            outcome = StopOutcome.Failed;
            _logger?.LogWarning("stop failed: appid {AppId}: {Reason}", appId, error.Message);
        }

        // Die Fortsetzung läuft auf dem Faden, der den Stop beendet hat — nicht auf dem
        // UI-Faden. Der Weg zurück ist derselbe wie bei den Ereignissen des Wächters.
        _dispatch(() =>
        {
            _stopping.Remove(appId);
            Raise(appId);
            _logger?.LogInformation(
                "stop finished: appid {AppId} ({Outcome})", appId, outcome);
        });
    }

    /// <summary>
    /// Die Startphase: gesperrter Knopf, bis der Wächter die Sitzung meldet.
    ///
    /// <para>
    /// <b>Die Sperre ist keine Kosmetik</b> (Spec S3.2 §5.5): Solange sie steht, kann der Nutzer
    /// nichts stoppen, was der Wächter noch nie gesehen hat — der eine Wettlauf, den der Core
    /// nicht selbst repariert.
    /// </para>
    /// </summary>
    private void BeginStartPhase(uint appId)
    {
        _starting[appId] = _timeProvider.CreateTimer(
            _ => _dispatch(() => EndStartPhase(appId, timedOut: true)),
            null,
            StartWindow,
            Timeout.InfiniteTimeSpan);
        Raise(appId);
        _logger?.LogDebug("start phase begun: appid {AppId}", appId);
    }

    /// <summary>Beendet die Phase auf beiden Wegen, damit die Uhr in jedem Fall entsorgt wird.</summary>
    private void EndStartPhase(uint appId, bool timedOut)
    {
        if (!_starting.Remove(appId, out var timer))
        {
            // Die Phase ist schon vorbei: Die Uhr schlug, während die Sitzung gemeldet wurde,
            // oder umgekehrt.
            return;
        }

        timer.Dispose();
        if (timedOut)
        {
            _logger?.LogWarning(
                "start phase timed out after {Seconds:F0}s: appid {AppId}",
                StartWindow.TotalSeconds,
                appId);
        }
        else
        {
            _logger?.LogDebug("start phase ended: appid {AppId}", appId);
        }

        Raise(appId);
    }

    private void Raise(uint appId) => BusyChanged?.Invoke(this, appId);
}
