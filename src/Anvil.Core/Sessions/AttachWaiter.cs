using Microsoft.Extensions.Logging;

namespace Anvil.Core.Sessions;

/// <summary>
/// Wartet, bis ein laufendes Spiel ein Programm in seinem Container annimmt — die
/// Sitzungsnaht „ist anhaengbar" (Vision §4.5, Spec S4.3).
///
/// <para>
/// <b>Warum das nicht im Waechter steckt</b> (E-1): <see cref="GameSessionWatcher"/> ist ein
/// synchroner /proc-Takt, der nie wirft; Anhaengbarkeit ist eine asynchrone Busfrage. Beides
/// in eine Schleife zu legen hiesse, <c>PollOnce</c> asynchron zu machen — und damit jeden
/// heutigen Aufrufer. Der Waechter wird hier <b>gelesen</b>, als Delegat, wie in
/// <see cref="GameLauncher"/>. Eine zweite Schleife ueber „was laeuft" entsteht dadurch
/// nicht.
/// </para>
///
/// <para>
/// <b>Gewartet wird auf den Bus-Namen</b>, nicht auf <c>STEAM_COMPAT_LAUNCHER_SERVICE</c> in
/// der Umgebung: Gemessen steht der Dienst rund 0,9 s frueher da als der Bus-Name
/// (messungen-anhaengen.md §2). Wer auf die Umgebung wartet, haengt zu frueh an.
/// </para>
///
/// <para>
/// Der ganze Verstand steckt in <see cref="AttachWatch"/>; hier steht nur die Schleife, das
/// Verschlucken der Sondenunsicherheit und die eine Logzeile am Ende.
/// </para>
/// </summary>
public sealed class AttachWaiter
{
    /// <summary>
    /// Die Messung tastete mit 250 ms ab — das war zum <i>Charakterisieren</i>. Zum
    /// <i>Handeln</i> halbiert 500 ms den Busverkehr und kostet hoechstens eine halbe Sekunde
    /// vor einem Programm, das selbst Sekunden zum Erscheinen braucht (E-3).
    /// </summary>
    private static readonly TimeSpan DefaultInterval = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// Gemessen lebt die Vorstartkette 4–5 s, die echte Sitzung folgt 1–10 s spaeter
    /// (messungen-anhaengen.md §2). 15 s deckt das mit Rand, ohne dass ein echtes Spielende lange
    /// nachhaengt (Projektplan M4, Entscheidung 4).
    /// </summary>
    private static readonly TimeSpan DefaultGrace = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Ein allererster Start kann einen Prefix von Grund auf hochziehen. Wer es eiliger hat,
    /// kuerzt ueber sein Abbruch-Token (E-3).
    /// </summary>
    private static readonly TimeSpan DefaultDeadline = TimeSpan.FromSeconds(120);

    private readonly Func<uint, bool> _isRunning;
    private readonly Func<uint, CancellationToken, Task<bool>> _isAttachable;
    private readonly ILogger? _logger;
    private readonly TimeProvider _time;
    private readonly TimeSpan _interval;
    private readonly TimeSpan _grace;
    private readonly TimeSpan _deadline;

    /// <param name="isRunning">
    /// Der Waechter, als Delegat — <c>watcher.IsRunning</c>. Delegat statt Objekt, damit hier
    /// nichts vom Waechter abhaengt ausser dieser einen Frage.
    /// </param>
    /// <param name="isAttachable">
    /// Die Busfrage. Die Composition Root bindet <c>SessionBusNameProbe</c> daran; ein Test
    /// reicht seine eigene Antwort. <b>Kein <c>see cref</c></b>: Die Sonde entsteht erst eine
    /// Aufgabe spaeter, und ein Verweis ins Leere ist unter <c>-warnaserror</c> ein Baufehler.
    /// </param>
    public AttachWaiter(
        Func<uint, bool> isRunning,
        Func<uint, CancellationToken, Task<bool>> isAttachable,
        ILogger? logger = null,
        TimeProvider? timeProvider = null,
        TimeSpan? interval = null,
        TimeSpan? grace = null,
        TimeSpan? deadline = null)
    {
        _isRunning = isRunning;
        _isAttachable = isAttachable;
        _logger = logger;
        _time = timeProvider ?? TimeProvider.System;
        _interval = interval ?? DefaultInterval;
        _grace = grace ?? DefaultGrace;
        _deadline = deadline ?? DefaultDeadline;
    }

    /// <summary>
    /// Die Busfrage mit verschluckter Unsicherheit (E-2): Was die Sonde wirft, wird zu
    /// <c>false</c> — „noch nicht", nicht „Fehler". Dieselbe Linie wie
    /// <c>StatusNotifierProbe</c>. Ein dauerhaft toter Bus laeuft dadurch in die Frist statt
    /// in eine Ausnahme, mit der der Aufrufer nichts anfangen kann.
    ///
    /// <para>
    /// <b>Der Abbruch des Aufrufers geht durch</b> — er ist keine Aussage ueber die Welt.
    /// </para>
    /// </summary>
    public async Task<bool> IsAttachableAsync(uint appId, CancellationToken cancellationToken)
    {
        try
        {
            return await _isAttachable(appId, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger?.LogDebug(
                exception, "bus probe for appid {AppId} failed; counting as not yet", appId);
            return false;
        }
    }

    /// <summary>
    /// Wartet, bis das Spiel anhaengbar ist, aufgibt oder die Frist reisst. Wirft nur bei
    /// Abbruch.
    /// </summary>
    public async Task<AttachWait> WaitAsync(uint appId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var watch = new AttachWatch(_time, appId, _grace, _deadline, _logger);
        using var timer = new PeriodicTimer(_interval, _time);

        while (true)
        {
            var attachable = await IsAttachableAsync(appId, cancellationToken)
                .ConfigureAwait(false);
            if (watch.Step(attachable, _isRunning(appId)) is { } wait)
            {
                // Die eine Zeile, an der die Live-Abnahme von S4.3 haengt.
                // Math.Round statt ToString mit CultureInfo: Der Formatierer von
                // Microsoft.Extensions.Logging setzt ohnehin invariant, und eine Zahl bleibt
                // im strukturierten Log eine Zahl.
                _logger?.LogInformation(
                    "attach wait for appid {AppId}: {Outcome} after {Seconds} s",
                    appId,
                    wait.Outcome,
                    Math.Round(wait.Elapsed.TotalSeconds, 1));
                return wait;
            }

            // Beendet wird der Timer allein ueber das Token — dann wirft er. Sein
            // bool-Ergebnis („der Timer wurde entsorgt") kann hier nicht false werden, weil
            // er im using dieser Methode lebt.
            await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false);
        }
    }
}
