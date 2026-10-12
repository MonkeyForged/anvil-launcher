namespace Anvil.Core.Steam;

/// <summary>Die fünf Zustände eines Glieds (Anforderung 30 a).</summary>
public enum ChainState
{
    Done,
    Running,
    Coming,
    Failed,
    Skipped,
}

/// <summary>Ein Glied der Folge, wie die Leiste es zeichnet (Spec S3.7 §3.1).</summary>
public sealed record ChainLink(
    string Label, bool NeedsSteamClosed, ChainState State, uint? AppId = null, string? Hint = null);

/// <summary>
/// Eine Fortschrittsmeldung: <b>die ganze Folge</b> als Momentaufnahme, dazu die Stufe des laufenden
/// Glieds, falls es welche meldet (Spec S3.7 §3.3). Die Leiste muss nichts mitschreiben.
/// </summary>
public sealed record SaveProgress(IReadOnlyList<ChainLink> Chain, StageProgress? Stage = null);

/// <summary>
/// Die Folge eines Vorgangs (Spec S3.7 §3.1). <b>Sortiert wird allein nach der Angabe des Schritts</b>:
/// erst alle ohne Steam, dann — nur wenn einer dabei ist — die Klammer aus Beenden und Starten um die
/// Steam-Schritte. Das ist die ganze Fallunterscheidung des Core (Vision Wand Bridge §4.7).
/// </summary>
public sealed class SaveChain
{
    public const string ClosingSteam = "Closing Steam…";

    public const string Writing = "Writing…";

    public const string StartingSteam = "Starting Steam…";

    private readonly List<Entry> _entries;

    public SaveChain(IReadOnlyList<SaveStep> steps)
    {
        BeforeSteam = steps.Where(step => !step.NeedsSteamClosed).ToList();
        WithSteamClosed = steps.Where(step => step.NeedsSteamClosed).ToList();

        _entries = BeforeSteam.Select(Of).ToList();
        if (WithSteamClosed.Count > 0)
        {
            _entries.Add(new Entry(new ChainLink(ClosingSteam, true, ChainState.Coming), Bracket.Closing));
            _entries.AddRange(WithSteamClosed.Select(Of));
            _entries.Add(new Entry(new ChainLink(StartingSteam, true, ChainState.Coming), Bracket.Starting));
        }
    }

    /// <summary>Die feste Folge für Zurückspielen und Wiederherstellen (E-8).</summary>
    public static SaveChain ForSteamWrite()
        => new([new SaveStep(Writing, true, _ => Task.FromResult(SaveStepOutcome.Done))]);

    public IReadOnlyList<SaveStep> BeforeSteam { get; }

    public IReadOnlyList<SaveStep> WithSteamClosed { get; }

    public bool HasSteamPart => WithSteamClosed.Count > 0;

    public IReadOnlyList<ChainLink> Links => _entries.Select(entry => entry.Link).ToList();

    public ChainLink Advance()
    {
        var index = _entries.FindIndex(entry => entry.Link.State == ChainState.Coming);
        if (index < 0)
        {
            throw new InvalidOperationException("no coming link left");
        }

        Set(index, ChainState.Running);
        return _entries[index].Link;
    }

    public void Complete() => Set(RunningIndex(), ChainState.Done);

    /// <summary>
    /// Das laufende Glied scheitert; alle kommenden stehen danach als übersprungen — außer
    /// <see cref="StartingSteam"/>, wenn Anvil Steam beendet hat (E-6): Steam kommt in jedem Fall zurück.
    /// </summary>
    public void Fail(bool steamWillRestart)
    {
        var index = RunningIndex();
        Set(index, ChainState.Failed);

        for (var i = index + 1; i < _entries.Count; i++)
        {
            var keep = steamWillRestart && _entries[i].Bracket == Bracket.Starting;
            if (!keep && _entries[i].Link.State == ChainState.Coming)
            {
                Set(i, ChainState.Skipped);
            }
        }
    }

    /// <summary>
    /// Steam lief nicht: Der Vorgang <b>besteht</b> dann aus seinen Schritten (Anforderung 23 b) —
    /// die Klammer verschwindet, statt als übersprungen stehen zu bleiben (E-4).
    /// </summary>
    public void DropSteamBracket() => _entries.RemoveAll(entry => entry.Bracket != Bracket.None);

    /// <summary>Die Laufform ohne Auslassungszeichen — für „Writing failed".</summary>
    public static string Bare(string label) => label.TrimEnd('…');

    private static Entry Of(SaveStep step)
        => new(
            new ChainLink(step.Label, step.NeedsSteamClosed, ChainState.Coming, step.AppId, step.Hint),
            Bracket.None);

    private int RunningIndex()
    {
        var index = _entries.FindIndex(entry => entry.Link.State == ChainState.Running);
        return index >= 0 ? index : throw new InvalidOperationException("no running link");
    }

    private void Set(int index, ChainState state)
        => _entries[index] = _entries[index] with { Link = _entries[index].Link with { State = state } };

    private enum Bracket
    {
        None,
        Closing,
        Starting,
    }

    private sealed record Entry(ChainLink Link, Bracket Bracket);
}
