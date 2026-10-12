namespace Anvil.Core.Steam;

/// <summary>Wie der Vorgang ausging (Spec S4.4 §3.5).</summary>
public enum TransactionOutcome
{
    /// <summary>Alles geschrieben; Steam laeuft wieder oder lief nie.</summary>
    Succeeded,

    /// <summary>Es gab nichts zu schreiben — Steam hat die Werte schon.</summary>
    NothingToDo,

    /// <summary>Ein Riegel hat gehalten; kein Kommando ist gelaufen.</summary>
    Blocked,

    /// <summary>Fehlschlag mit Klartext. Was geschrieben war, ist zurueckgespielt.</summary>
    Failed,

    /// <summary>
    /// Geschrieben, aber Steam kam nicht wieder hoch (E-H). <b>Kein <see cref="Failed"/>:</b>
    /// Die Dateien sind vollstaendig und gueltig, es gibt nichts zurueckzuspielen, und S4.5
    /// muss trotzdem neu einlesen — sonst zeigt die Oberflaeche alte Werte, waehrend auf der
    /// Platte die neuen stehen.
    /// </summary>
    WrittenButSteamNotStarted,
}

/// <summary>
/// Was die Transaktion zu sagen hat (Spec S4.4 §3.5). <b>Titel und Nebentext getrennt</b>
/// (E-G): Die Speicherleiste zeichnet zwei Zeilen — ein zusammengesetzter Satz muesste dort
/// wieder auseinandergeschnitten werden. Beide Texte sind englisch, wie alles, was der Nutzer
/// sieht.
/// </summary>
/// <param name="Files">Das Ergebnis des Schreibers, falls er lief; sonst <c>null</c>.</param>
public sealed record TransactionResult(
    TransactionOutcome Outcome,
    string Title,
    string? Detail,
    SteamWriteResult? Files = null)
{
    /// <summary>
    /// Die Folge im Endzustand (Spec S3.7 §3.3) — was geschafft, gescheitert, übersprungen ist.
    /// Leer bei <see cref="TransactionOutcome.Blocked"/> und <see cref="TransactionOutcome.NothingToDo"/>:
    /// Dort ist keine Folge angelaufen.
    /// </summary>
    public IReadOnlyList<ChainLink> Chain { get; init; } = [];

    /// <summary>
    /// Ob auf der Platte etwas Neues steht — die einzige Frage, die die Oberflaeche an das
    /// Ergebnis hat, um ueber den Rescan zu entscheiden (Projektplan M4, Entscheidung 8).
    /// </summary>
    public bool Wrote => Outcome
        is TransactionOutcome.Succeeded
        or TransactionOutcome.WrittenButSteamNotStarted;
}
