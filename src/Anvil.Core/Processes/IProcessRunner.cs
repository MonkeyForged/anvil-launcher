namespace Anvil.Core.Processes;

/// <summary>
/// Führt einen Prozess aus und liefert, ob er von selbst geendet hat.
///
/// <para>
/// <b>Warum es diese Naht gibt:</b> Die Fertig-Kriterien von S3.5 verlangen wörtlich einen
/// gefälschten Ausführer für die Tests des Bauers (Spec §3.1) — ohne ihn wäre jeder Test dieser
/// Story ein Live-Lauf von mehreren Minuten gegen echtes Wine. Das ist keine Abstraktion auf
/// Verdacht (CLAUDE.md verbietet sie): Der Bedarf steht explizit in den Fertig-Kriterien.
/// </para>
///
/// <para>
/// <b>Warum sie so eng ist:</b> ein Aufruf, ein Ergebnis — kein Datenstrom, keine Ereignisse,
/// kein Fortschritt während des Laufs. Wer mehr braucht (den Herzschlag aus Spec §3.8 etwa),
/// baut das eine Ebene darüber; die Naht selbst bleibt so schmal wie möglich.
/// </para>
/// </summary>
public interface IProcessRunner
{
    /// <summary>
    /// Startet <paramref name="command"/> und wartet auf sein Ende, höchstens
    /// <paramref name="timeout"/> lang oder bis <paramref name="cancellationToken"/> ausgelöst
    /// wird. Läuft der Prozess länger oder wird abgebrochen, wird er getötet und das Ergebnis
    /// trägt <c>Completed = false</c>.
    /// </summary>
    Task<ProcessOutcome> RunAsync(
        ProcessCommand command, TimeSpan timeout, CancellationToken cancellationToken);
}
