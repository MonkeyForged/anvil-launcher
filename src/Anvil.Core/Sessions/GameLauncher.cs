using System.Diagnostics;
using Anvil.Core.Steam;
using Microsoft.Extensions.Logging;

namespace Anvil.Core.Sessions;

/// <summary>Was ein Startversuch ergeben hat (Spec §4.7).</summary>
public enum StartOutcome
{
    Started,
    AlreadyRunning,
    Failed,
}

/// <summary>
/// Was ein Stoppversuch ergeben hat (Spec E-E). Welche Stufe gewirkt hat, steht im Log —
/// die Oberflaeche braucht nur „hat es geklappt".
/// </summary>
public enum StopOutcome
{
    NotRunning,
    Stopped,
    Failed,
}

/// <summary>
/// Startet und beendet Steam-Spiele (Spec §4.7). Der einzige Baustein dieser Story mit
/// einem Ablauf; alles andere ist reine Funktion.
///
/// <para>
/// <b>Alle Nahtstellen sind Delegaten</b>, wie ueberall im Core
/// (<c>GameLibrary(Func&lt;SteamSnapshot&gt; scan)</c>): der Scanner, das Warten auf den
/// Waechter, die Overlay-Suche, der Signalgeber und der Prozessstart. Ein Interface mit
/// einer Methode und einer Implementierung waere die Abstraktion, die die Bauprinzipien
/// untersagen.
/// </para>
///
/// <para>
/// <b>Der Launcher taktet nicht — und er hat keine eigene Wahrheit ueber „laeuft es".</b>
/// Die beantwortet der Waechter (Vision §3: eine Wahrheit je Sache). Den Scanner ruft der
/// Launcher nur, wenn er <i>frische PIDs</i> braucht, an die ein Signal geht. Ob ein Stop
/// gewirkt hat, sagt ihm ebenfalls der Waechter, nicht der Rueckgabewert eines Kommandos.
/// </para>
///
/// <para>
/// <b>Warum das zaehlt</b> (Fund der externen Analyse, 2026-09-05): Bis dahin machte jeder
/// Klick auf „Start game" oder „Stop game" einen vollen Durchgang durch /proc, synchron auf
/// dem UI-Faden. Im Log der Owner-Maschine stehen dafuer bis zu zwei Sekunden, in denen die
/// Oberflaeche stand.
/// </para>
/// </summary>
public sealed class GameLauncher
{
    /// <summary>
    /// Die Fristen der Staffel (Spec S3.3b §4.3). Der Waechter meldet ein Ende binnen einer
    /// Sekunde nach der Tat — seit S3.3a ohne Kulanzzeit, im Sekundentakt. Gemessen am
    /// 2026-09-06: sanfter Weg 0,89 s, SIGTERM an den Baum 0,44 s, jeweils bis zur Meldung.
    /// Fuenf Sekunden fuer Stufe 1 sind das Doppelte dessen, was der Vorgaenger beim sanften
    /// Weg sah, plus der Takt; drei Sekunden nach einem SIGKILL sind zwei Takte Reserve.
    /// Bis S3.3b waren es 15 / 6 / 10 s — allein der Kulanzzeit geschuldet, die es nicht
    /// mehr gibt. Schlimmster Fall: 11 s bis <see cref="StopOutcome.Failed"/>.
    /// </summary>
    private static readonly TimeSpan SoftWindow = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan AfterFirstKill = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan AfterSecondKill = TimeSpan.FromSeconds(3);

    private readonly Func<IReadOnlyList<GameSession>> _scan;
    private readonly Func<uint, GameSession?> _currentSession;
    private readonly Func<uint, CancellationToken, Task> _waitForEnd;
    private readonly Func<uint, IReadOnlyList<int>> _findOverlayPids;
    private readonly string? _launchClientPath;
    private readonly Func<uint, GamePlatform?> _platformOf;
    private readonly Action<int, ProcessSignal> _sendSignal;
    private readonly Action<ProcessStartInfo> _run;
    private readonly ILogger? _logger;
    private readonly TimeProvider _timeProvider;

    /// <param name="scan">
    /// Frische Sitzungen — die Composition Root reicht
    /// <c>scanner.ScanWithProcessTree</c>. <b>Nur fuer PIDs, an die ein Signal geht</b>;
    /// die Frage „laeuft es" beantwortet <paramref name="currentSession"/>.
    /// </param>
    /// <param name="currentSession">
    /// Was der Waechter gerade fuehrt — die Composition Root reicht <c>watcher.For</c>.
    /// </param>
    /// <param name="waitForEnd">
    /// Wartet, bis der Waechter das Ende meldet — die Composition Root reicht
    /// <c>watcher.WaitForEndAsync</c>.
    /// </param>
    /// <param name="findOverlayPids">Steams Overlay zu einer AppID (<c>finder.Pids</c>).</param>
    /// <param name="launchClientPath">
    /// Steams Startclient, oder null. Ohne ihn entfaellt der sanfte Weg ersatzlos (Spec E-D).
    /// </param>
    /// <param name="platformOf">
    /// Was die Bibliothek ueber ein Spiel weiss, oder null, wenn sie es nicht kennt — die
    /// Composition Root reicht ein Nachschlagen in <c>GameLibrary.Current</c>. <b>Nur ein
    /// natives Spiel</b> verliert damit den sanften Weg (Spec S3.3b E-A bis E-C): Steam gibt
    /// ihm einen Launcher-Dienst, aber in seinem Container gibt es keinen wineserver, den
    /// <c>wineserver -k</c> treffen koennte. Gemessen an Vampire Survivors, 2026-09-05.
    /// </param>
    public GameLauncher(
        Func<IReadOnlyList<GameSession>> scan,
        Func<uint, GameSession?> currentSession,
        Func<uint, CancellationToken, Task> waitForEnd,
        Func<uint, IReadOnlyList<int>> findOverlayPids,
        string? launchClientPath,
        Func<uint, GamePlatform?> platformOf,
        Action<int, ProcessSignal>? sendSignal = null,
        Action<ProcessStartInfo>? run = null,
        ILogger? logger = null,
        TimeProvider? timeProvider = null)
    {
        _scan = scan;
        _currentSession = currentSession;
        _waitForEnd = waitForEnd;
        _findOverlayPids = findOverlayPids;
        _launchClientPath = launchClientPath;
        _platformOf = platformOf;
        _sendSignal = sendSignal ?? ProcessSignals.Send;
        _run = run ?? RunProcess;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// Wie der Startaufruf aussieht — getrennt vom Start, weil dort ein fremder Prozess
    /// entsteht und die Uebergabe trotzdem pruefbar bleiben soll (Muster
    /// <see cref="DesktopShell.StartInfoFor"/>).
    /// </summary>
    public static ProcessStartInfo StartInfoFor(uint appId)
        => DesktopShell.StartInfoFor(SteamUrl.RunGame(appId));

    /// <summary>
    /// Startet ein Spiel ueber Steam. <b>Wartet auf nichts</b> — ob und wann eine Sitzung
    /// erscheint, sagt der Waechter; die Startphase mit ihrem Deckel ist S3.3.
    /// </summary>
    public StartOutcome Start(uint appId)
    {
        if (_currentSession(appId) is not null)
        {
            _logger?.LogInformation(
                "start skipped: appid {AppId} is already running", appId);
            return StartOutcome.AlreadyRunning;
        }

        var url = SteamUrl.RunGame(appId);
        _logger?.LogInformation("starting appid {AppId} via {Url}", appId, url);
        try
        {
            _run(StartInfoFor(appId));
        }
        catch (Exception exception)
        {
            // Fehlt xdg-open oder der steam://-Handler, darf das in S3.3 kein Command
            // mitreissen.
            _logger?.LogWarning(exception, "start failed: appid {AppId}", appId);
            return StartOutcome.Failed;
        }

        return StartOutcome.Started;
    }

    /// <summary>
    /// Beendet ein Spiel in Stufen (Spec §5.2). Der sanfte Weg zuerst, dann zweimal hart;
    /// <b>bestaetigt wird jede Stufe vom Waechter</b>, nie vom Rueckgabewert eines
    /// Kommandos (Spec §5.5).
    ///
    /// <para>
    /// <b>Der Stop ist hart — das Spiel speichert dabei nicht.</b> Ob davor gewarnt wird,
    /// entscheidet die Oberflaeche; der Core hat niemanden zu fragen.
    /// </para>
    /// </summary>
    public async Task<StopOutcome> StopAsync(
        uint appId, CancellationToken cancellationToken = default)
    {
        // Was laeuft, sagt der Waechter — kein Durchgang durch /proc auf dem Klickpfad.
        var session = _currentSession(appId);
        if (session is null)
        {
            _logger?.LogInformation("stop skipped: appid {AppId} is not running", appId);
            return StopOutcome.NotRunning;
        }

        var startedAt = _timeProvider.GetTimestamp();

        // Stufe 1: der sanfte Weg, wenn es ihn gibt — und wenn er das Spiel treffen kann.
        if (!TryGentleWay(session, appId))
        {
            // Erst hier wird gescannt: An dieser Stelle gehen Signale an PIDs, und die
            // sollen frisch sein. Der sanfte Weg braucht nur den Launcher-Dienst und kommt
            // ganz ohne aus.
            var pids = Session(appId)?.Pids ?? session.Pids;
            _logger?.LogInformation(
                "stopping appid {AppId}: SIGTERM to {Count} process(es)",
                appId,
                pids.Count);
            Signal(pids, ProcessSignal.Terminate);
        }

        if (await EndedWithin(appId, SoftWindow, cancellationToken).ConfigureAwait(false))
        {
            return Stopped(appId, startedAt);
        }

        // Stufe 2: hart, auf den frischen Stand — plus Steams Overlay, das nicht zur
        // Sitzung gehoert.
        if (await KillAndWait(appId, AfterFirstKill, second: false, cancellationToken)
            .ConfigureAwait(false))
        {
            return Stopped(appId, startedAt);
        }

        // Stufe 3: der zweite Schuss holt, was beim ersten gerade neu entstanden war.
        if (await KillAndWait(appId, AfterSecondKill, second: true, cancellationToken)
            .ConfigureAwait(false))
        {
            return Stopped(appId, startedAt);
        }

        var left = Session(appId)?.Pids ?? [];
        _logger?.LogError(
            "failed to stop appid {AppId}: still alive after {Seconds:F0}s, pids {Pids}",
            appId,
            _timeProvider.GetElapsedTime(startedAt).TotalSeconds,
            string.Join(", ", left));
        return StopOutcome.Failed;
    }

    /// <summary>Ein SIGKILL auf den frischen Stand, dann warten.</summary>
    private async Task<bool> KillAndWait(
        uint appId, TimeSpan window, bool second, CancellationToken cancellationToken)
    {
        var session = Session(appId);
        var pids = session?.Pids ?? [];
        var overlay = Overlay(appId);

        if (second)
        {
            _logger?.LogWarning(
                "appid {AppId} still alive: second SIGKILL to {Count} process(es)",
                appId,
                pids.Count + overlay.Count);
        }
        else
        {
            _logger?.LogWarning(
                "appid {AppId} still alive after {Seconds:F0}s: SIGKILL to {Count} "
                + "process(es) and {OverlayCount} overlay process(es)",
                appId,
                SoftWindow.TotalSeconds,
                pids.Count,
                overlay.Count);
        }

        Signal(pids, ProcessSignal.Kill);
        Signal(overlay, ProcessSignal.Kill);
        return await EndedWithin(appId, window, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Nimmt den sanften Weg, wenn er offensteht, und sagt, ob er genommen wurde. Drei
    /// Bedingungen, und jede, die fehlt, steht mit ihrem Grund im Log: Launcher-Dienst,
    /// Startclient — und ein Spiel, in dessen Container ueberhaupt ein wineserver laeuft.
    /// </summary>
    private bool TryGentleWay(GameSession session, uint appId)
    {
        if (session.LauncherService is not { } service)
        {
            return false;
        }

        if (_launchClientPath is not { } client)
        {
            _logger?.LogInformation(
                "no steam-runtime-launch-client found — falling back to SIGTERM");
            return false;
        }

        // Nur „nativ" schliesst aus; Unknown und „nicht in der Bibliothek" bekommen den
        // sanften Weg — geht er ins Leere, kostet das die erste Frist, mehr nicht (E-C).
        if (Platform(appId) is GamePlatform.NativeLinux)
        {
            _logger?.LogInformation(
                "gentle way skipped: appid {AppId} runs natively, no wineserver to stop",
                appId);
            return false;
        }

        _logger?.LogInformation(
            "stopping appid {AppId} gently: launcher service {Service}, wineserver -k",
            appId,
            service);
        Run(SteamLaunchClient.StartInfoFor(client, appId), appId);
        return true;
    }

    private GamePlatform? Platform(uint appId)
    {
        try
        {
            return _platformOf(appId);
        }
        catch (Exception exception)
        {
            // Ein Nachschlagen, das wirft, ist „weiss ich nicht" — und das heisst sanfter Weg.
            _logger?.LogDebug(exception, "platform lookup failed: appid {AppId}", appId);
            return null;
        }
    }

    /// <summary>
    /// Wartet, bis der Waechter das Ende meldet oder die Frist ablaeuft; <c>true</c> heisst
    /// „gemeldet".
    ///
    /// <para>
    /// <b>Die Frist wird vor dem Waechter angemeldet.</b> Nur so steht sie sicher, sobald
    /// <c>waitForEnd</c> gerufen wurde — daran haengen die Tests der Staffel.
    /// </para>
    /// </summary>
    private async Task<bool> EndedWithin(
        uint appId, TimeSpan window, CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var elapsed = Task.Delay(window, _timeProvider, linked.Token);
        var ended = _waitForEnd(appId, linked.Token);

        var first = await Task.WhenAny(ended, elapsed).ConfigureAwait(false);

        // Die andere Seite wird abgeraeumt: Ein Abbruch laesst eine Task „canceled"
        // zurueck, nicht „faulted" — es bleibt keine unbeobachtete Ausnahme liegen.
        await linked.CancelAsync().ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return first == ended;
    }

    private StopOutcome Stopped(uint appId, long startedAt)
    {
        _logger?.LogInformation(
            "stopped appid {AppId} after {Seconds:F0}s",
            appId,
            _timeProvider.GetElapsedTime(startedAt).TotalSeconds);
        return StopOutcome.Stopped;
    }

    private void Signal(IReadOnlyList<int> pids, ProcessSignal signal)
    {
        foreach (var pid in pids)
        {
            try
            {
                _sendSignal(pid, signal);
            }
            catch (Exception exception)
            {
                // Die naechste PID ist vielleicht die, auf die es ankommt.
                _logger?.LogDebug(
                    exception, "signal {Signal} to pid {Pid} failed", signal, pid);
            }
        }
    }

    private IReadOnlyList<int> Overlay(uint appId)
    {
        try
        {
            return _findOverlayPids(appId);
        }
        catch (Exception exception)
        {
            _logger?.LogDebug(exception, "overlay lookup failed: appid {AppId}", appId);
            return [];
        }
    }

    private void Run(ProcessStartInfo info, uint appId)
    {
        try
        {
            _run(info);
        }
        catch (Exception exception)
        {
            // Der Rueckgabewert des Startclients sagt ohnehin nichts (Spec §2.2) — dass er
            // gar nicht erst lief, gehoert aber ins Log.
            _logger?.LogWarning(exception, "launch client failed: appid {AppId}", appId);
        }
    }

    /// <summary>
    /// Die Sitzung dieses Spiels aus einem <b>frischen</b> Scan, oder null. Teuer — deshalb
    /// nur dort, wo gleich ein Signal an PIDs geht.
    /// </summary>
    private GameSession? Session(uint appId)
    {
        try
        {
            return _scan().FirstOrDefault(session => session.AppId == appId);
        }
        catch (Exception exception)
        {
            // Der Scanner wirft laut S3.1 nicht; passiert es doch, ist „laeuft nicht" die
            // harmlosere Annahme — ein Start zu viel statt eines Signals ins Blaue.
            _logger?.LogWarning(exception, "session scan failed: appid {AppId}", appId);
            return null;
        }
    }

    /// <summary>
    /// Der Standardwert fuer <c>run</c>: der einzige ungepruefte Teil dieser Klasse. Er
    /// heisst nicht <c>Run</c>, weil Task 7 eine gleichnamige Instanzmethode fuer den
    /// geloggten Aufruf des Startclients dazubekommt.
    /// </summary>
    private static void RunProcess(ProcessStartInfo info)
    {
        using var process = Process.Start(info);
    }
}
