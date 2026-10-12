namespace Anvil.Core.Backups;

/// <summary>
/// Was unmittelbar nach dieser Sicherung geschrieben wurde (Spec S4.8 §3.2) — die Geschichte
/// zum Datum.
///
/// <para>
/// <b>Reine Zeichenketten, kein Steam-Begriff</b> (E-D): <see cref="BackupStore"/> kennt seit
/// S4.1 nur Dateien. Wer den Vermerk <i>füllt</i>, weiß von Steam
/// (<c>BackupNoteBuilder</c>); wer ihn <i>ablegt</i>, nicht.
/// </para>
/// </summary>
/// <param name="ModuleKey">Der auslösende Modulschlüssel; <c>mixed</c> bei mehreren.</param>
/// <param name="Summary">Eine Zeile für die Liste. Gekürzt wird in der Sicht, nicht hier.</param>
/// <param name="Games">
/// Die Aufstellung fürs Detail-Popup. <b>Leer heißt „keine Aufstellung"</b> — dann zeigt die
/// Oberfläche keine Affordanz und öffnet kein Popup (E-O); so steht der Restore da.
/// </param>
public sealed record BackupNote(
    string ModuleKey,
    string Summary,
    IReadOnlyList<BackupNoteGame> Games);

/// <param name="Name">Der Spielname, wie ihn die Bibliothek zeigt.</param>
/// <param name="Fields">Die geänderten Felder dieses Spiels in dieser Datei.</param>
public sealed record BackupNoteGame(string Name, IReadOnlyList<BackupNoteField> Fields);

/// <summary>
/// Ein geändertes Feld mit beiden Werten (Design-Antwort zu 26): Die Frage am Popup lautet
/// nicht „was steht jetzt drin?", sondern „welchen Stand hole ich zurück?" — und die Antwort
/// ist <paramref name="Before"/>.
/// </summary>
/// <param name="Label">Der Feldname, englisch — <c>Proton</c>, <c>Launch options</c>.</param>
/// <param name="Before">
/// Der Wert vor dem Schreiben. <c>null</c> heißt <b>leer</b>, nicht „unbekannt" (E-E): Ein leeres
/// Startoptionen-Feld ist ein gültiger Wert. Das Wort <c>empty</c> entsteht erst im ViewModel.
/// </param>
/// <param name="After">Der Wert danach; <c>null</c> ebenso.</param>
public sealed record BackupNoteField(string Label, string? Before, string? After);
