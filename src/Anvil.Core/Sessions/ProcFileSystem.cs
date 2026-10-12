using System.Globalization;

namespace Anvil.Core.Sessions;

/// <summary>
/// Ein Prozess aus dem /proc-Baum. PID und Argumentliste stehen sofort fest, die Umgebung
/// wird erst auf Anfrage gelesen und dann behalten (Spec §4.1): Die meisten Prozesse sind
/// schon nach der Argumentliste aussortiert, und fremde environ sind ohnehin nicht lesbar.
/// </summary>
public sealed class ProcProcess
{
    private readonly ProcFileSystem _procFileSystem;
    private IReadOnlyDictionary<string, string>? _environment;

    internal ProcProcess(ProcFileSystem procFileSystem, int pid, IReadOnlyList<string> arguments)
    {
        _procFileSystem = procFileSystem;
        Pid = pid;
        Arguments = arguments;
    }

    public int Pid { get; }

    public IReadOnlyList<string> Arguments { get; }

    /// <summary>Die Umgebung des Prozesses; leer, wenn sie nicht (mehr) lesbar ist.</summary>
    public IReadOnlyDictionary<string, string> ReadEnvironment()
        => _environment ??= _procFileSystem.ReadEnvironment(Pid);

    /// <summary>
    /// Die PID des Elternprozesses; null heisst „nicht lesbar" — wie ueberall hier.
    ///
    /// <para>
    /// <b>Kein Zwischenspeicher</b>, anders als bei <see cref="ReadEnvironment"/>: Der Baum
    /// fragt jeden Prozess genau einmal je Durchgang, und ein ProcProcess lebt nicht laenger.
    /// </para>
    /// </summary>
    public int? ReadParentPid() => _procFileSystem.ReadParentPid(Pid);

    /// <summary>
    /// Der Prozessname (<c>comm</c>); <c>null</c> heisst „nicht lesbar". <b>Kein
    /// Zwischenspeicher</b>, wie bei <see cref="ReadParentPid"/>: Je Durchgang fragt der
    /// Scanner nur die Nachfahren einer Sitzung, und ein ProcProcess lebt nicht laenger.
    /// </summary>
    public string? ReadComm() => _procFileSystem.ReadComm(Pid);
}

/// <summary>
/// Liest Prozesse unterhalb einer gereichten Wurzel — im Betrieb <c>/proc</c>, im Test ein
/// Temp-Baum (Spec §4.1). Der Wurzelpfad ist die Naht; eine Dateisystem-Abstraktion gibt es
/// hier so wenig wie sonst im Core.
///
/// <para>
/// <b>/proc ist flüchtig.</b> Zwischen dem Auflisten der Verzeichnisse und dem Lesen einer
/// Datei kann der Prozess weg sein, und fremde <c>environ</c> sind gar nicht lesbar — das ist
/// der Normalfall. Jede Störung heißt hier „nicht vorhanden", nie ein Fehler.
/// </para>
/// </summary>
public sealed class ProcFileSystem
{
    private readonly string _rootDirectory;

    public ProcFileSystem(string rootDirectory) => _rootDirectory = rootDirectory;

    /// <summary>Jedes numerische Verzeichnis, dessen Argumentliste sich noch lesen lässt.</summary>
    public IEnumerable<ProcProcess> Processes()
    {
        foreach (var entry in ListEntries())
        {
            if (!int.TryParse(
                    Path.GetFileName(entry),
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out var pid))
            {
                // self, net, sys … — kein Prozess.
                continue;
            }

            var arguments = ReadNulSeparated(pid, "cmdline");
            if (arguments is null)
            {
                // Zwischen Auflisten und Lesen verschwunden — kein Fehler, nur weg.
                continue;
            }

            yield return new ProcProcess(this, pid, arguments);
        }
    }

    internal IReadOnlyDictionary<string, string> ReadEnvironment(int pid)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        var entries = ReadNulSeparated(pid, "environ");
        if (entries is null)
        {
            return result;
        }

        foreach (var entry in entries)
        {
            var separator = entry.IndexOf('=', StringComparison.Ordinal);
            if (separator <= 0)
            {
                // Kein „=" oder ein leerer Name: beides kommt in echten environ vor.
                continue;
            }

            result[entry[..separator]] = entry[(separator + 1)..];
        }

        return result;
    }

    /// <summary>
    /// Verzeichnisnamen aufsteigend nach PID sortiert. <see cref="Directory.GetDirectories"/>
    /// liefert die Reihenfolge des Dateisystems (bei ext4/tmpfs Hash-Reihenfolge, nicht
    /// Erstellungsreihenfolge) — ohne Sortierung wäre die Prozessliste zwischen zwei Takten
    /// unnötig unruhig. Nicht-numerische Einträge (self, net, sys …) landen ans Ende; ihre
    /// Reihenfolge untereinander ist gleichgültig, sie werden ohnehin verworfen.
    /// </summary>
    /// <summary>
    /// Das Verzeichnislisting von /proc. <b>Faengt nichts ab</b> — und das ist der Punkt:
    /// Ein misslungenes Listing ist keine Aussage ueber die Welt (Fund der externen Analyse,
    /// 2026-09-05).
    ///
    /// <para>
    /// Bis dahin stand hier ein <c>catch</c>, das eine leere Liste zurueckgab. Die ist von
    /// „kein Prozess laeuft" nicht zu unterscheiden — der Waechter haette daraufhin jede
    /// laufende Sitzung fuer beendet erklaert. Genau dagegen trug er einen Bestaetigungstakt,
    /// der jedes Spielende um zwei Sekunden verzoegerte: ein Pflaster ueber einer Stelle, die
    /// hier richtig gehoert.
    /// </para>
    ///
    /// <para>
    /// Wer damit umgehen muss, ist <c>GameSessionWatcher.PollOnce</c>: Es faengt den Fehler,
    /// aendert nichts und meldet nichts. Ein lebender Prozess fehlt in einem gelungenen
    /// Listing dagegen nie — deshalb braucht es keinen zweiten Takt zur Bestaetigung.
    /// </para>
    /// </summary>
    private string[] ListEntries()
        => [.. Directory.GetDirectories(_rootDirectory)
            .OrderBy(entry => int.TryParse(
                    Path.GetFileName(entry),
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out var pid)
                ? pid
                : int.MaxValue)];

    /// <summary>
    /// Das vierte Feld aus <c>stat</c>. Der Programmname im zweiten Feld steht in Klammern und
    /// darf Leerzeichen und Klammern enthalten (<c>(Blasphemous 2.exe)</c>) — deshalb wird ab
    /// der <b>letzten</b> schliessenden Klammer geteilt und von dort das zweite Feld genommen.
    /// </summary>
    internal int? ReadParentPid(int pid)
    {
        var text = ReadText(pid, "stat");
        if (text is null)
        {
            return null;
        }

        var lastClose = text.LastIndexOf(')');
        if (lastClose < 0)
        {
            return null;
        }

        var fields = text[(lastClose + 1)..]
            .Split(' ', StringSplitOptions.RemoveEmptyEntries);

        return fields.Length >= 2
            && int.TryParse(
                fields[1], NumberStyles.None, CultureInfo.InvariantCulture, out var parentPid)
            ? parentPid
            : null;
    }

    /// <summary>
    /// Der Prozessname aus <c>/proc/&lt;pid&gt;/comm</c>; <c>null</c> heisst „nicht lesbar" —
    /// wie ueberall hier. Der Kernel haengt einen Zeilenumbruch an und <b>kuerzt den Namen</b>
    /// (TASK_COMM_LEN, 15 Zeichen plus Null), weshalb der Vergleich im
    /// <see cref="GameSessionScanner"/> ein Praefixvergleich ist und kein Gleichheitsvergleich.
    ///
    /// <para>
    /// <b>Nicht aus <c>stat</c> gelesen</b>, obwohl der Name dort auch steht: Dort steht er in
    /// Klammern und darf selbst Klammern und Leerzeichen enthalten
    /// (<c>(Blasphemous 2.exe)</c>). <c>comm</c> ist die Datei, die genau diese eine Angabe
    /// fuehrt.
    /// </para>
    /// </summary>
    internal string? ReadComm(int pid)
        => ReadText(pid, "comm")?.Trim('\n') is { Length: > 0 } comm ? comm : null;

    /// <summary>Liest eine NUL-getrennte Liste; null heißt „nicht lesbar".</summary>
    private IReadOnlyList<string>? ReadNulSeparated(int pid, string fileName)
        => ReadText(pid, fileName)?.Split('\0', StringSplitOptions.RemoveEmptyEntries);

    /// <summary>Liest eine Datei des Prozesses; null heißt „nicht lesbar".</summary>
    private string? ReadText(int pid, string fileName)
    {
        var path = Path.Combine(
            _rootDirectory, pid.ToString(CultureInfo.InvariantCulture), fileName);
        try
        {
            return File.ReadAllText(path);
        }
        catch (Exception exception)
            when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }
}
