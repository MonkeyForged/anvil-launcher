namespace Anvil.Core.Sessions;

/// <summary>
/// Das Spiel-Programm einer Sitzung (Spec S5.4a §3.2): <b>wonach gesucht wurde</b> und
/// <b>ob es gefunden wurde</b>.
///
/// <para>
/// <b>Warum es diesen Satz gibt.</b> Steams Startklammer ist kein Beweis mehr dafuer, dass ein
/// Spiel laeuft: Ein angehaengtes Programm im Container haelt sie am Leben, und wer sein Spiel
/// im Spiel beendet, sieht es in Steam wie in Anvil weiter als laufend (gemessen am
/// 2026-09-19, Spec S5.4 §9). Das Merkmal, das beides unterscheidet, ist dieser Satz.
/// </para>
///
/// <para>
/// <b>Ein Satz und nicht zwei Felder:</b> Ein Name neben einem Ja/Nein liesse die Verbindung
/// „kein Name, aber lebendig" zu, die nichts bedeutet. Dieser Satz kann sie nicht bilden — es
/// gibt ihn nur ganz oder gar nicht (<see cref="GameSession.Program"/> ist dann <c>null</c>).
/// </para>
/// </summary>
/// <param name="FileName">
/// Der Dateiname des Spiel-Programms aus der Argumentliste der Klammer — <b>nie sein Pfad</b>.
/// </param>
/// <param name="Alive">
/// Lebt ein Nachfahre der Klammer, dessen <c>comm</c> zu diesem Namen passt? Verglichen wird
/// als Praefix, weil der Kernel <c>comm</c> kuerzt.
/// </param>
public sealed record GameProgram(string FileName, bool Alive);
