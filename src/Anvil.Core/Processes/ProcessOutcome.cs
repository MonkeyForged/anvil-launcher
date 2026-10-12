namespace Anvil.Core.Processes;

/// <summary>
/// Das Ergebnis eines Prozessaufrufs.
///
/// <para>
/// <b><see cref="Completed"/> ist das einzige Urteil</b> (Plan P-2). <c>false</c> heißt „die Zeit
/// ist abgelaufen" oder „abgebrochen" — eine dritte Ausprägung gibt es nicht. Der
/// <see cref="ExitCode"/> ist Auskunft fürs Log, <b>kein Erfolgszeichen</b>: Vision §6.2 hat
/// gemessen, dass der .NET-4.8-Aufruf nach 36 Sekunden mit Rückgabewert 0 zurückkam, während
/// <c>Setup.exe</c> im Hintergrund noch minutenlang weiterarbeitete. Wer aus <c>ExitCode == 0</c>
/// „fertig" ableitet, baut genau diesen Fehlschlag nach. Ob wirklich etwas entstanden ist, sagt
/// nie dieser Gegenstand, sondern der Befund auf dem Prefix (S3.1, <c>PrefixMarkProbe</c>).
/// </para>
/// </summary>
/// <param name="Completed">
/// <c>true</c>, wenn der Prozess von selbst beendet wurde — unabhängig vom Rückgabewert.
/// <c>false</c> bei Zeitüberschreitung oder Abbruch; in beiden Fällen wurde er getötet.
/// </param>
/// <param name="ExitCode">
/// Der Rückgabewert, wenn der Prozess von selbst endete; sonst <c>null</c>. Nur fürs Log.
/// </param>
public sealed record ProcessOutcome(bool Completed, int? ExitCode);
