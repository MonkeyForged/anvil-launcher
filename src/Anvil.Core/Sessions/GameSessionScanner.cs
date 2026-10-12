namespace Anvil.Core.Sessions;

/// <summary>
/// Sieht in einem Durchgang durch den /proc-Baum nach, welche Steam-Spiele laufen
/// (Spec §4.4). Die Steam-Bibliothek wird dabei nicht gefragt: Was laeuft, sagt /proc.
///
/// <para>
/// <b>Drei Phasen.</b> Zuerst die Argumentlisten aller Prozesse — allein daran haengt, dass
/// ueberhaupt etwas laeuft, auch bei nativen Spielen. Dann die Umgebung <i>nur</i> der
/// Treffer, fuer den Launcher-Dienst. Und zuletzt der Prozessbaum: Er sagt, ob das
/// Spiel-Programm noch lebt, und auf Verlangen liefert er die volle Prozessliste der Sitzung
/// (<see cref="ScanWithProcessTree"/>).
/// </para>
///
/// <para>
/// <b>Warum die dritte Phase den Baum nimmt und nicht die Umgebung</b> (Fund der
/// Live-Abnahme von S3.3, gemessen 2026-09-05): Bis S3.3a las sie die Umgebung <i>jedes</i>
/// Prozesses der Maschine, um Prozesse mit derselben Prefix-Marke einzusammeln. Bei fremden
/// Prozessen darf sie das nicht — auf der Owner-Maschine 384 von 580 —, und jeder dieser
/// Versuche wirft eine Ausnahme, die eine Schicht tiefer gefangen wird. Im Betrieb kostet das
/// kaum etwas, aber unter einem Debugger ist jede geworfene Ausnahme ein eigenes Ereignis:
/// Der Waechter machte die Anwendung dort alle zwei Sekunden unbenutzbar. Der Baum aus
/// <c>/proc/&lt;pid&gt;/stat</c> liefert dieselben Prozesse, ohne eine einzige Ausnahme.
/// </para>
///
/// <para>
/// <b>Warum die dritte Phase seit S5.4a in jedem Takt laeuft</b> (Spec S5.4a §2): Bis dahin
/// war sie Arbeit auf Verdacht — ihre Liste ist die <b>Kill-Liste</b>, und die braucht nur
/// ein Stop, der den Scanner ohnehin noch einmal fragt (Spec S3.2 §5.4). Der Waechter
/// beantwortete „laeuft es" allein an der Startklammer. Am 2026-09-19 wurde gemessen, dass
/// die Klammer das nicht mehr beweist: Ein angehaengtes Programm im Container haelt sie am
/// Leben, und wer sein Spiel <i>im Spiel</i> beendet, saehe es sonst ewig als laufend. Das
/// unterscheidende Merkmal steht in den Nachfahren, also muss der Baum jedes Mal gebaut
/// werden. Der Preis ist rund das Doppelte eines Takts — und nur, solange ein Spiel laeuft:
/// Ohne eine einzige Klammer steigt der Durchgang schon vorher aus.
/// </para>
/// </summary>
public sealed class GameSessionScanner
{
    private const string LauncherServiceVariable = "STEAM_COMPAT_LAUNCHER_SERVICE";

    private readonly ProcFileSystem _procFileSystem;

    public GameSessionScanner(ProcFileSystem procFileSystem)
        => _procFileSystem = procFileSystem;

    /// <summary>Der Scanner fuer den Betrieb: das echte /proc.</summary>
    public static GameSessionScanner ForCurrentSystem() => new(new ProcFileSystem("/proc"));

    /// <summary>
    /// <b>Welche Spiele laufen</b>, nach AppID aufsteigend — der Durchgang des Waechters.
    /// Die Prozessliste enthaelt nur, was Steams Startklammer traegt; wer die vollstaendige
    /// braucht, nimmt <see cref="ScanWithProcessTree"/>.
    ///
    /// <para>
    /// <b>Wirft, wenn /proc sich nicht auflisten laesst.</b> Ein einzelner Prozess, der
    /// zwischen Auflisten und Lesen verschwindet, ist „weg" und kein Fehler; laesst sich aber
    /// gar nicht erst nachsehen, gibt es keine Antwort — und dann wird auch keine erfunden.
    /// Der Waechter macht daraus „nichts aendern" (Fund der externen Analyse, 2026-09-05).
    /// </para>
    /// </summary>
    public IReadOnlyList<GameSession> Scan() => Scan(withProcessTree: false);

    /// <summary>
    /// <b>Die Kill-Liste eines Stops:</b> jeder Prozess, der an Steams Startklammer haengt —
    /// ueber beliebig viele Stufen, aus dem Prozessbaum.
    ///
    /// <para>
    /// <b>Warum der Baum und nicht die Umgebung</b> (gemessen 2026-09-05, Spec S3.3a §4.3):
    /// Bis dahin las diese Phase die Umgebung <i>jedes</i> Prozesses der Maschine, um Prozesse
    /// mit derselben Prefix-Marke einzusammeln. Bei fremden Prozessen darf sie das nicht — auf
    /// der Owner-Maschine 384 von 580 —, und jeder Versuch wirft eine Ausnahme. Der Baum aus
    /// <c>/proc/&lt;pid&gt;/stat</c> liefert gegen laufendes Boltgun exakt dieselben 17
    /// Prozesse, ohne eine einzige.
    /// </para>
    ///
    /// <para>
    /// <b>Was er aufgibt:</b> einen Prozess, der sich vom Baum abhaengt und an PID 1 landet.
    /// Genau damit war die alte Phase begruendet; gemessen tritt der Fall bei keinem der drei
    /// geprueften Spiele auf.
    /// </para>
    /// </summary>
    public IReadOnlyList<GameSession> ScanWithProcessTree() => Scan(withProcessTree: true);

    private IReadOnlyList<GameSession> Scan(bool withProcessTree)
    {
        // Phase 1: Argumentlisten. Alles, was keine Startklammer eines Spiels traegt, kommt
        // beiseite — wird aber aufgehoben, weil Phase 3 daraus den Baum baut.
        var pidsByApp = new Dictionary<uint, SortedSet<int>>();
        var reapersByApp = new Dictionary<uint, List<ProcProcess>>();
        var all = new List<ProcProcess>();

        foreach (var process in _procFileSystem.Processes())
        {
            all.Add(process);

            var appId = SteamLaunchChain.ParseAppId(process.Arguments);
            if (appId is null)
            {
                continue;
            }

            if (!pidsByApp.TryGetValue(appId.Value, out var pids))
            {
                pids = [];
                pidsByApp[appId.Value] = pids;
                reapersByApp[appId.Value] = [];
            }

            pids.Add(process.Pid);
            reapersByApp[appId.Value].Add(process);
        }

        if (pidsByApp.Count == 0)
        {
            return [];
        }

        // Phase 2: die Umgebung nur der Treffer — und nur noch fuer den Launcher-Dienst.
        // Bei mehreren reaper-Prozessen derselben AppID gewinnt der erste, der einen Wert
        // liefert.
        var launcherServiceByApp = new Dictionary<uint, string>();

        foreach (var (appId, reapers) in reapersByApp)
        {
            foreach (var reaper in reapers.OrderBy(process => process.Pid))
            {
                if (!launcherServiceByApp.ContainsKey(appId)
                    && Value(reaper.ReadEnvironment(), LauncherServiceVariable)
                        is { } launcherService)
                {
                    launcherServiceByApp[appId] = launcherService;
                    break;
                }
            }
        }

        // Phase 3: der Prozessbaum. Seit S5.4a immer, sobald ueberhaupt eine Klammer gefunden
        // wurde — das Merkmal „lebt das Spiel-Programm" haengt an den Nachfahren, und der
        // Waechter braucht es in jedem Takt. Was withProcessTree noch entscheidet, ist allein,
        // ob die Nachfahren in Pids landen: Der Waechter bekommt die Klammer, der Stop die
        // Kill-Liste. Der Preis steht in Spec S5.4a §2 — rund das Doppelte eines Takts,
        // und nur, solange ein Spiel laeuft.
        var childrenByParent = new Dictionary<int, List<int>>();
        foreach (var process in all)
        {
            if (process.ReadParentPid() is not { } parent)
            {
                continue;
            }

            if (!childrenByParent.TryGetValue(parent, out var children))
            {
                children = [];
                childrenByParent[parent] = children;
            }

            children.Add(process.Pid);
        }

        var byPid = all.ToDictionary(process => process.Pid);
        var programByApp = new Dictionary<uint, GameProgram>();

        foreach (var (appId, pids) in pidsByApp)
        {
            var session = new SortedSet<int>(pids);
            var pending = new Stack<int>(pids);
            while (pending.Count > 0)
            {
                if (!childrenByParent.TryGetValue(pending.Pop(), out var children))
                {
                    continue;
                }

                foreach (var child in children)
                {
                    // Add meldet false, wenn die PID schon drin ist — das ist zugleich
                    // der Schutz gegen einen Kreis im Baum.
                    if (session.Add(child))
                    {
                        pending.Push(child);
                    }
                }
            }

            if (ProgramOf(reapersByApp[appId], session, byPid) is { } program)
            {
                programByApp[appId] = program;
            }

            if (withProcessTree)
            {
                pids.UnionWith(session);
            }
        }

        return
        [
            .. pidsByApp
                .OrderBy(pair => pair.Key)
                .Select(pair => new GameSession(
                    AppId: pair.Key,
                    Pids: [.. pair.Value],
                    LauncherService: launcherServiceByApp.GetValueOrDefault(pair.Key),
                    Program: programByApp.GetValueOrDefault(pair.Key))),
        ];
    }

    private static string? Value(IReadOnlyDictionary<string, string> environment, string name)
        => environment.TryGetValue(name, out var value) && !string.IsNullOrEmpty(value)
            ? value
            : null;

    /// <summary>
    /// <b>Lebt ein Nachfahre der Klammer, dessen <c>comm</c> der Dateiname des
    /// Spiel-Programms ist?</b> (Spec S5.4a §3.1.) <c>null</c> heisst „nicht beurteilbar":
    /// keine <c>.exe</c> in der Klammer.
    ///
    /// <para>
    /// <b>Verglichen wird ueber <c>comm</c>, nie ueber die Argumentliste</b> (Falle 1,
    /// gemessen): Der Pfad des Spiels steht nach dem Beenden weiterhin in drei lebenden
    /// Argumentlisten — in der Klammer selbst, im Proton-Starter und in <c>steam.exe</c>. Wer
    /// ihn sucht, findet ihn immer. Nur der Prozess, der das Spiel <i>ist</i>, traegt den
    /// Dateinamen als <c>comm</c>.
    /// </para>
    ///
    /// <para>
    /// <b>Und verglichen wird als Praefix</b> (Falle 2): Der Kernel kuerzt <c>comm</c> auf 15
    /// Bytes. Ein Gleichheitsvergleich gegen einen selbst gekuerzten Namen traefe bei jedem
    /// Zeichen ausserhalb von ASCII daneben — still. Der Praefixvergleich irrt hoechstens in
    /// die ungefaehrliche Richtung: Ein falsches „lebt noch" ist das Verhalten von vor dieser
    /// Story, ein falsches „ist weg" erfaende ein Spielende.
    /// </para>
    /// </summary>
    private static GameProgram? ProgramOf(
        IReadOnlyList<ProcProcess> reapers,
        IEnumerable<int> sessionPids,
        IReadOnlyDictionary<int, ProcProcess> byPid)
    {
        var fileName = reapers
            .OrderBy(reaper => reaper.Pid)
            .Select(reaper => SteamLaunchChain.ParseExecutableName(reaper.Arguments))
            .FirstOrDefault(name => name is not null);

        if (fileName is null)
        {
            return null;
        }

        var alive = sessionPids.Any(pid
            => byPid.TryGetValue(pid, out var process)
                && process.ReadComm() is { } comm
                && fileName.StartsWith(comm, StringComparison.Ordinal));

        return new GameProgram(fileName, alive);
    }
}
