namespace Anvil.Core.Steam;

/// <summary>
/// Eine Stufe innerhalb eines laufenden Schritts, z. B. Datei 3 von 7 (Antwort 18, Anforderung 30 b).
/// <b>Stufen statt Prozent:</b> ein Schritt meldet, wie weit er in einer bekannten Anzahl ist — der
/// Aufrufer rechnet daraus keinen Anteil, er zeigt die Zahlen wörtlich.
/// </summary>
public readonly record struct StageProgress(int Stage, int Of);

/// <summary>
/// Wie ein <see cref="SaveStep"/> ausging (Spec S3.7 §3.2). <b>Kein Ausnahme-Pfad:</b> ein Schritt
/// meldet Scheitern als Wert, damit die Folge weiß, ob sie weiterlaufen darf, ohne einen Fang zu
/// brauchen.
/// </summary>
/// <param name="Subject">Der Gegenstand im Fehlertitel, z. B. <c>localconfig.vdf</c>; <c>null</c>,
/// wenn keiner benannt werden kann.</param>
/// <param name="Detail">Der Nebentext zum Fehler, falls es einen gibt.</param>
public sealed record SaveStepOutcome(bool Succeeded, string? Subject = null, string? Detail = null)
{
    /// <summary>Der Schritt ist ohne Beanstandung durchgelaufen.</summary>
    public static SaveStepOutcome Done { get; } = new(true);

    /// <summary>Der Schritt ist gescheitert; <paramref name="subject"/> und <paramref name="detail"/> sind optional.</summary>
    public static SaveStepOutcome Failed(string? subject = null, string? detail = null)
        => new(false, subject, detail);
}

/// <summary>
/// Ein Schritt der Speicherroutine (Spec S3.7 §3.1). <b>Ein Record mit Delegat</b> (E-2): Der Schritt
/// trägt nur seine eigene Angabe — Beschriftung, ob er geschlossenes Steam braucht, das zugehörige
/// Spiel — und den Code, der ihn ausführt. Die Folge (<see cref="SaveChain"/>) sortiert und zählt die
/// Schritte, ohne ihren Inhalt zu kennen.
/// </summary>
/// <param name="Label">Beschriftung in der Laufform, englisch, z. B. <c>"Writing…"</c> (E-3).</param>
/// <param name="NeedsSteamClosed">Ob dieser Schritt geschlossenes Steam voraussetzt — die einzige
/// Angabe, nach der <see cref="SaveChain"/> sortiert.</param>
/// <param name="RunAsync">Der Code des Schritts. Er bekommt einen Melder für Stufen und liefert das
/// Ergebnis.</param>
/// <param name="AppId">Das betroffene Spiel, falls der Schritt eines hat.</param>
/// <param name="Hint">
/// Ein Satz, der die Erwartung setzt, solange der Schritt läuft — <c>null</c> ist der Normalfall.
///
/// <para>
/// <b>Er kommt vom Schritt, nicht von der Leiste</b> (Owner-Wunsch vom 2026-09-17): Nur der Schritt
/// weiß vorher, ob er lange dauert. Ein Prefix-Schritt, der ein vorhandenes Paket anwendet, ist
/// nach einer Sekunde fertig und schweigt; einer, der erst bauen muss, sagt es. Ein Hinweis, der
/// immer stünde, wäre beim zweiten Mal falsch — und ein falscher Hinweis wird beim dritten Mal
/// nicht mehr gelesen.
/// </para>
/// </param>
public sealed record SaveStep(
    string Label,
    bool NeedsSteamClosed,
    Func<Action<StageProgress>, Task<SaveStepOutcome>> RunAsync,
    uint? AppId = null,
    string? Hint = null);
